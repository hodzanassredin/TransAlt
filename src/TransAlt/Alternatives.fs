namespace TransAlt
///first class cancellable async operations    
module Alt =
    open State
    open System

    //simle async monad return
    let asyncReturn x = Async.FromContinuations(fun (cont,_,_) -> cont(x))

    type StateChangeOp<'s,'r> = 's -> 's * 'r
    ///result of a transaction
    type Result<'r> = | Ok of 'r
                      | BlockedForever
    type CommitHandler = bool -> Async<unit>       

    let emptyHandler : CommitHandler= fun _ -> asyncReturn ()        
    let rec mergeHandlers handls  : CommitHandler = 
            fun res -> async{
                for handler in handls do
                    do! handler(res)
                }
               
    ///first class cancellable transactional async computation
    type Alt<'s,'r when 's : not struct> = 
        {
            run : ProcessId * StateKeeper<'s>->Async<Result<'r> * CommitHandler> 
            IsMutatesState : bool
        }
    ///checks is any of transactions can mutate state
    let anyMutatesState alts = 
        alts |> Seq.exists(fun x -> x.IsMutatesState)

    ///map ans async result
    let asyncMap wrkfl f = async{
        let! r = wrkfl
        return f(r)
    }
    ///run given computation in a given transaction asynchronously
    ///creates an Alternative from a simple async workflow without onCommit or OnFiledCommit handlers
    let fromAsync wrkfl =
        {
            run = fun (_,_) -> async{
                                    let! res = wrkfl
                                    return Ok(res),emptyHandler
                                }
            IsMutatesState = false
        }
    let private rand = new System.Random(DateTime.Now.Millisecond)
    let shuffle s = Array.sortBy(fun _ -> rand.Next()) s
    ///never commits success
    let never() =
        {
            run = fun (_,_) -> asyncReturn (BlockedForever,emptyHandler)
            IsMutatesState = false
        }
    ///Creates an alternative that is available when any one of the given alternatives is.
    let choose<'s,'r when 's : not struct> (alts:Alt<'s,'r> seq)  =  
        if Seq.length alts < 1 then never()
        elif Seq.length alts = 1 then Seq.head alts
        else
        let rec loop (procId : ProcessId, state : StateKeeper<'s>) =
                async{
                    let parentInitState = state.Value
                    let rec runSub alt = 
                        let subState = SingleStateKeeper(parentInitState, "choose") :> StateKeeper<'s>
                        async{
                            let! res,c = alt.run (0, subState)
                            subState.Stop()
                            return res, subState,c
                        }

                    let! res = alts |> Seq.map runSub |> Async.Parallel  
                    let res = res |> shuffle                

                    let ok, cancels = Array.fold (fun s t -> match s, t with
                                                        | (Some(s), cs), (_,_,c) -> Some(s), c(false)::cs
                                                        | (None, cs), (Ok(r),s,c) -> Some(r,s,c), cs
                                                        | (None, cs), (BlockedForever,_,c) ->  None,  c(false)::cs) (None,[]) res
                    let! _ = Async.Parallel cancels   
                         
                    let swap s = async{
                        let! stateResp =  state.Merge s
                        match stateResp with
                            | Result() -> return true
                            | Die -> return false
                        }      
                    match ok with
                        | Some(r,s,c) -> let! ok = swap s
                                         if ok then return Ok(r),c
                                         else do! c(false)
                                              return! loop (procId,state) 
                        | None -> let! runProcCount = state.RunningProcsCountExcludeMe(procId)
                                  let canResolveBlock = runProcCount > 0 || (obj.ReferenceEquals(parentInitState, state.Value)|> not)
                                  if canResolveBlock 
                                  then return! loop (procId,state)
                                  else return BlockedForever, emptyHandler
                               
                }
        {
            run = loop
            IsMutatesState = anyMutatesState alts
        }

    ///bind an alternative to an continuation
    let bind (one:Alt<'s,'a>, f:'a -> Alt<'s,'b>) : Alt<'s,'b> =  
          {
            run = fun (procId,state) -> async{
                let! res,c = one.run (procId,state)
                match res with
                    | Ok(r) -> let! res2,c2 = (f(r)).run (procId,state)
                               match res2 with
                                | Ok(r2) -> return (res2, mergeHandlers [c;c2]) 
                                | BlockedForever -> do! c(false)
                                                    do! c2(false)
                                                    return BlockedForever, emptyHandler
                    | BlockedForever -> do! c(false)
                                        return BlockedForever, emptyHandler
            }
            IsMutatesState = false
        }//tod static checking
    ///always commits success with a given value
    let always v = v |> asyncReturn |> fromAsync
    ///always commits success with unit
    let unit () = always ()
    ///maps a result of an alternative into other alternative
    let map (alt,f) = bind(alt, fun x -> always(f(x)))
    
    ///whileLoop alternaive for builder
    let rec whileLoop guard body =
        if guard() then bind(body, fun x -> whileLoop guard body)
                        else always () 
    ///try with alternative for builder
    let tryWith body compensation = 
        {
            run = fun (procId,state) -> async{
                                            try
                                                let! res = body.run (procId,state)
                                                return res
                                            with ex -> return compensation(ex)
                                        }
            IsMutatesState = true
        }
    ///try finally alternative for builder
    let tryFinally body compensation = 
       {
            run = fun (procId,state) -> async{
                                            try
                                                let! res = body.run (procId,state)
                                                return res
                                            finally
                                                compensation()
                                        }
            IsMutatesState = body.IsMutatesState
        }

    ///merges two computations in differnet ways to find a way to execute them without blocking
    let mergeChoose (one:Alt<'s,'r>, two:Alt<'s,'r2>) =  
        choose[bind(one,fun r -> map(two, fun r2 -> r,r2)); 
               bind(two,fun r2 -> map(one, fun r -> r,r2))]
    ///merges two alternatives into single one which returns both results as a tuple
    let merge (alts:Alt<'s,'a>[]) : Alt<'s, 'a[]> =  
         if alts.Length < 1 then never()
         elif alts.Length = 1 then map(alts.[0], fun x -> [|x|])
         else {
                run = fun (procId,parentState) -> async{
                    let state = if parentState.IsThreadSafe 
                                    then parentState
                                    else (new MailboxStateKeeper<_>(parentState.Value, "merge")) :> StateKeeper<_>
                    let run i alt =
                        async{
                            let! procId = if i = 0 then asyncReturn procId
                                          else state.GetProcessId(alt.IsMutatesState)
                            return! alt.run (procId, state)
                        }
                    let! res = alts |> Array.mapi run |> Async.Parallel
                    //if parentState.IsThreadSafe then parentState.ReleaseProcessId(procId)
                    
                    
                    if parentState.IsThreadSafe |> not then state.Stop()
                    let ok = Array.TrueForAll(res , fun (r,c) -> match r with
                                                            | Ok(r) -> true 
                                                            | BlockedForever -> false)
                    if ok 
                    then let res,cs = Array.map (fun (Ok(r),c) -> r,c) res |> Array.unzip
                         return Ok(res), mergeHandlers cs
                    else let! _ = res |> Array.map (fun (_,c) -> c(false)) |> Async.Parallel
                         return BlockedForever, emptyHandler
                }
                IsMutatesState = anyMutatesState alts
            }
    ///uses a alternative builder function for alternative creation with specified handlers on commit or commit failure
    let withAck (builder:Alt<'s, bool> -> Async<Alt<'s,'r>>) =  
        {
            run = fun (procId,state) -> async{
                                            let p = Promise.create<_>()
                                            let nack = fromAsync p.future
                                            let! alt = builder(nack)
                                            let! res, c = alt.run (procId,state)
                                            return res, mergeHandlers [c; fun x -> p.signal(x) |> ignore
                                                                                   asyncReturn ()] 
                                        }
            IsMutatesState = true
        }
    ///attahes an on success handler
    let wrap (alt,f) =  
        {
            run = fun (procId,state) -> async{
                                            let! res, c = alt.run (procId,state)
                                            return res, mergeHandlers [c; f] 
                                        }
            IsMutatesState = alt.IsMutatesState
        }
    let guard g = withAck <| fun _ -> g
    let delay f = guard( async{ return! f()})
    //if else expression for alternatives
    let ife (pred,thenAlt, elseAlt) = 
        bind(pred, fun x ->
                    if x then thenAlt
                    else elseAlt)

    ///returns alternative which returns None
    let none() = always None
    ///returns alternative which wraps returned result into a Some(v)
    let some alt = bind(alt, fun x -> always <| Some(x))
    ///commits result only if alt commits a value which is f(value) = true
    let where (alt,f) = bind(alt, fun x ->
                            if f(x) then always x
                            else never ())
    ///commits value after a specified amount of time
    let after ms v = async{
                        do! Async.Sleep(ms)
                        return v} |> fromAsync
    ///reduces seq of altrnatives with mergeChoose
    let mergeChooseXs (xs:Alt<'s,'r> seq) : Alt<'s,'r seq> = 
        Seq.fold (fun (x:Alt<'s,'r seq>) (y:Alt<'s,'r>) -> 
            map(mergeChoose (x,y), fun (x,y) -> seq{yield y
                                                    yield! x})) (always(Seq.empty)) xs
    ///run altrnative and return result as an promise which will return result with a resulting state 
    let pickWithResultState state alt  = 
        let stateR = SingleStateKeeper(state, "pick") :> StateKeeper<_>
        async{
            let res = alt.run (0, stateR)
            return res, stateR.Value
        }
    ///run altrnative and return result as an promise
    let pick state alt  =
        async{
            let! r,_ = pickWithResultState state alt 
            return r
        }
    ///map state
    let mapSt lens alt =
        {
            run = fun (procId,state) -> 
                                        async{
                                            let st = new MapStateKeeper<_,_>(state, lens) :> StateKeeper<_>
                                            let! res, c = alt.run (procId,st)
                                            return res, c 
                                        }
            IsMutatesState = alt.IsMutatesState
        }
    ///alternative for a state operation execution
    let stateOp op =
        {
            run = fun (procId,state) -> async{
                                            let safeOp :StateOp<_,_> =
                                                   fun s -> try 
                                                                match op s with
                                                                    | NotBlocked(s,r) -> NotBlocked(s,Ok(r))
                                                                    | Blocked -> Blocked 
                                                            with exn -> Error(exn)
                                            let! res = state.Apply(procId,safeOp)
                                        //Logger.logf  "State client: recieved response %A" res
                                        match res with
                                            | Result(r) -> //Logger.logf  "State client: recieved response %A" r
                                                           return r, emptyHandler
                                            | Die -> //Logger.log "State client: recieved response Die" 
                                                     return BlockedForever, emptyHandler
                                            | Error(ex) -> failwith ex
                                        }
            IsMutatesState = true
        }
            
        

    
    ///builder for transactional alternatives
    type TransactionBuilder() =
        member this.Bind(m, f) = bind(m,f)
        member this.Return(x) = always x
        member this.ReturnFrom(x) = x
        //(unit -> bool) * M<'a> -> M<'a>
        member this.While(guard, body) = whileLoop guard body
        member this.Zero() = always()
        //M<'T> * (exn -> M<'T>) -> M<'T>
        member this.TryWith(body, handler) = tryWith body handler
        // M<'T> * (unit -> unit) -> M<'T>
        member this.TryFinally(body, compensation) = tryFinally body compensation
        //'T * ('T -> M<'U>) -> M<'U> when 'U :> IDisposable
        member this.Using(disposable:#System.IDisposable, body) =
            let body' = body disposable
            this.TryFinally(body', fun () -> 
                match disposable with 
                    | null -> () 
                    | disp -> //Logger.log "Using" "disposing"
                              disp.Dispose())
        //(unit -> M<'T>) -> M<'T>
        member this.Delay(f) = bind(unit(), f)
        member this.Run(f) = f
        //seq<'a> * ('a -> M<'b>) -> M<'b>
        member this.For(sequence:seq<_>, body) =
           //Logger.log "For" "executing"
           this.Using(sequence.GetEnumerator(),fun enum -> 
                //Logger.log "ForUsing" "executing"
                this.While(enum.MoveNext, 
                    this.Delay(fun () -> body enum.Current)))

    let tranB = TransactionBuilder() 
    ///nice syntax for choose
    type ChooseBuilder() =
        [<CustomOperation("case")>]
        member this.Case(x,y) = choose(y,x)
        member this.Yield(()) = never()
    let chooseB = ChooseBuilder() 
    ///nice syntax for merge
    type MergeBuilder() =
        [<CustomOperation("case")>]
        member this.Case(x,y) = merge(y,x)
        member this.Yield(()) = always()
    let mergeB = MergeBuilder() 
    ///imitating joinads
    type AltQueryBuilder() =
        member this.Bind(m, f) = bind(m,f)
        member t.Zip(xs,ys) = merge(xs,ys)
        member t.For(xs,f) =  bind(xs,f)
        member t.For((x,y),f) =  bind(merge(x,y),f)
        member t.For((x,y,z),f) = let tmp1 = merge(x,y)
                                  let tmp2 = map(merge(tmp1,z), fun ((x,y),z) -> x,y,z)
                                  bind(tmp2,f)
        member t.For(x,f) =  bind(mergeXs(x),f)
        member t.Yield(x) = always(x)
        member t.Zero() = always()
        [<CustomOperation("where", MaintainsVariableSpace=true)>]
        member x.Where
            ( source:Alt<'s,'r>, 
              [<ProjectionParameter>] f:'r -> bool ) : Alt<'s,'r> = where(source,f)

        [<CustomOperation("select")>]
        member x.Select
            ( source:Alt<'s,'r>, 
              [<ProjectionParameter>] f:'r -> 'r2) : Alt<'s,'r2> = map(source,f)
 
    let queryB = new AltQueryBuilder()
    
