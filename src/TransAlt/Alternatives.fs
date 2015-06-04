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
            run : ProcessId * StateKeeper<'s>->Async<Result<'r>> 
            IsMutatesState : bool
            onCommit : CommitHandler
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
                                    return Ok(res)
                                }
            IsMutatesState = false
            onCommit = emptyHandler
        }
    let private rand = new System.Random(DateTime.Now.Millisecond)
    let shuffle s = Array.sortBy(fun _ -> rand.Next()) s
    ///Creates an alternative that is available when any one of the given alternatives is.
    let choose<'s,'r when 's : not struct> (alts:Alt<'s,'r> seq)  =  
        if Seq.length alts < 1 then never()
        elif Seq.length alts = 1 then Seq.head alts
        else
        let rec loop (procId:ProcessId,state: StateKeeper<'s>) =
                async{
                    let parentInitState = state.Value
                    let rec runSub alt = 
                        let subState = SingleStateKeeper(parentInitState, "choose") :> StateKeeper<'s>
                        async{
                            let! res = alt.run (0, subState)
                            subState.Stop()
                            return res, subState, alt.onCommit
                        }

                    let! res = alts |> Seq.map runSub |> Async.Parallel  
                    let res = res |> shuffle                

                    let ok, cancels = Array.fold (fun s t -> match s, t with
                                                        | (Some(s), cs), (_,_,c) -> Some(s), c(false)::cs
                                                        | (None, cs), (Ok(r),s,c) -> Some(r,s,c), cs
                                                        | (None, cs), (BlockedForever,_,c) ->  None,  c(false)::cs) (None,[]) res
                    for c in cancels do
                        do! c     
                         
                    let swap s = async{
                        let! stateResp =  state.Merge s
                        match stateResp with
                            | Result() -> return true
                            | Die -> return false
                        }      
                    match ok with
                        | Some(r,s,ñ) -> let! ok = swap s
                                         return Ok(r)
                        | None -> let! runProcCount = state.RunningProcsCountExcludeMe(procId)
                                  let canResolveBlock = runProcCount > 0 || (obj.ReferenceEquals(parentInitState, state.Value)|> not)
                                  if canResolveBlock 
                                  then return! loop (procId,state)
                                  else return BlockedForever
                               
                }
        {
            run = loop
            IsMutatesState = anyMutatesState alts
            onCommit = alts |> Seq.map (fun x ->x.onCommit) |> mergeHandlers
        }

    ///bind an alternative to an continuation
    let bind (one:Alt<'s,'a>, f:'a -> Alt<'s,'b>) : Alt<'s,'b> =  
        Alt((fun (procId, tran:Transaction<'s,'b>) ->
            async{
                let commit res =
                    match res with
                        | Ok(v) -> 
                            let subCommited = Promise.create<bool>()
                            let sub = f v
                            let commit v = async{
                                let! succ = tran.commit(v)
                                subCommited.signal(succ) |> ignore
                                return succ
                            }
                            run procId {state = tran.state; commit = commit} sub 
                            subCommited.future
                        | BlockedForever -> tran.commit BlockedForever
                        | Error(exn) -> tran.commit (Error(exn))

                run procId {state = tran.state;commit = commit} one 
            }),true)//tod static checking
    ///always commits success with a given value
    let always v = v |> asyncReturn |> fromAsync
    ///always commits success with unit
    let unit () = always ()
    ///maps a result of an alternative into other alternative
    let map (alt,f) = bind(alt, fun x -> always(f(x)))
    ///never commits success
    let never() = Alt(fun (_,tran) -> async{
        let! _ = tran.commit(TransactionResult.BlockedForever)
        return ()
    }, false)
    ///whileLoop alternaive for builder
    let rec whileLoop guard body =
        if guard() then bind(body, fun x -> whileLoop guard body)
                        else always () 
    ///try with alternative for builder
    let tryWith body compensation = 
        Alt((fun (procId,tran) ->async{
            let commit v =
                let subCommited = Promise.create<bool>()
                let subCommit v = async{
                    let! succ = tran.commit(v)
                    subCommited.signal(succ) |> ignore
                    return succ}
                match v with
                    | BlockedForever -> run procId {tran with commit = subCommit} (compensation(new Exception("BlockedForever")))
                                        subCommited.future
                    | Error(exn) -> run procId {tran with commit = subCommit} (compensation(exn)) 
                                    subCommited.future
                    | _ -> tran.commit v
            run procId {tran with commit = commit} body 
        }),isMutatesState body)
    ///try finally alternative for builder
    let tryFinally body compensation = 
        Alt((fun (procId,tran) ->async{
            let commit v =
                compensation()
                tran.commit(v)
            run procId {tran with commit = commit} body
        }),isMutatesState body)

    ///merges two computations in differnet ways to find a way to execute them without blocking
    let mergeChoose (one:Alt<'s,'r>, two:Alt<'s,'r2>) =  
        choose(bind(one,fun r -> map(two, fun r2 -> r,r2)), 
               bind(two,fun r2 -> map(one, fun r -> r,r2)))
    ///merges two alternatives into single one which returns both results as a tuple
    let merge (one:Alt<'s,'a>, two:Alt<'s,'b>) : Alt<'s, 'a * 'b> =  
        Alt((fun (procId,tran) ->
            async{
                let state = if tran.state.IsThreadSafe 
                                then tran.state
                                else (new MailboxStateKeeper<_>(tran.state.Value, "merge")) :> StateKeeper<_>
                let! subProcId1 = if tran.state.IsThreadSafe then asyncReturn procId
                                  else state.GetProcessId(isMutatesState one)
                let! subProcId2 = state.GetProcessId(isMutatesState two)
                let commit1 = Promise.create()
                let commit2 = Promise.create()
                let bothOk = Promise.create<bool>()
                let commit procId (subCommit:Promise.Promise<_>) v =
                    async{
                        subCommit.signal v |> ignore
                        state.ReleaseProcessId(procId)
                        return! bothOk.future
                    }
 
                let tran1 = {state = state; commit = commit subProcId1 commit1}
                let tran2 = {state = state; commit = commit subProcId2 commit2}
                run subProcId1 tran1 one
                run subProcId2 tran2 two 
                let! res1 = commit1.future
                let! res2 = commit2.future
                if tran.state.IsThreadSafe |> not then state.Stop()
                let commit v =
                    async{
                        let stateChanged = not state.IsNotChanged
                        if tran.state.IsThreadSafe |> not && stateChanged
                            then Logger.log "merge" "merging state with not thread safe parent"
                                 let! resp = tran.state.Merge(state)
                                 Logger.logf "merge" "merging state with not thread safe parent response %A" resp
                                 return () 
                        return! tran.commit v
                    }
                let! isCommited = match res1, res2 with
                                        | Ok(r), Ok(r2) -> commit <| Ok(r, r2)
                                        | Error(exn), Error(exn2) -> commit (Error(AggregateException([exn;exn2])))
                                        | Error(exn), _ -> commit (Error(exn))
                                        | _, Error(exn) -> commit (Error(exn))
                                        | _ -> commit (BlockedForever)
                                   
                bothOk.signal(isCommited) |> ignore
            }),isMutatesState2 (one,two)
        )
    ///uses a alternative builder function for alternative creation with specified handlers on commit or commit failure
    let withAck (builder:Alt<'s, bool> -> Async<Alt<'s,'r>>) =  
        Alt((fun (procId,tran) ->
            async{
                let nack = Promise.create<bool>()
                let commit res = async{
                    let! commited = tran.commit res
                    nack.signal(commited) |> ignore
                    return commited}
                let tran = {commit = commit; state = tran.state}
                let! alt = builder(fromAsync(nack.future))
                run procId tran alt 
            }), true)
    ///attahes an on success handler
    let wrap (alt,f) =  
        Alt((fun (procId,tran) ->
            async{
                let commit v = async{
                    //Logger.logf "wrap intercepting commit %A" v
                    let! commited = tran.commit v
                    if commited then f(v)
                    return commited
                }         
                run procId {commit = commit; state = tran.state} alt 
            }), isMutatesState alt)
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
    ///reduces seq of altrnatives with choose
    let chooseXs xs = Seq.fold (fun x y -> choose (x,y)) (never()) xs
    ///reduces seq of altrnatives with merge
    let mergeXs (xs:Alt<'s,'r> seq) : Alt<'s,'r seq> = 
        Seq.fold (fun (x:Alt<'s,'r seq>) (y:Alt<'s,'r>) -> 
            map(merge (x,y), fun (x,y) -> seq{yield y
                                              yield! x})) (always(Seq.empty)) xs
    ///reduces seq of altrnatives with mergeChoose
    let mergeChooseXs (xs:Alt<'s,'r> seq) : Alt<'s,'r seq> = 
        Seq.fold (fun (x:Alt<'s,'r seq>) (y:Alt<'s,'r>) -> 
            map(mergeChoose (x,y), fun (x,y) -> seq{yield y
                                                    yield! x})) (always(Seq.empty)) xs
    ///run altrnative and return result as an promise which will return result with a resulting state 
    let pickWithResultState state alt  = 
        let res = Promise.create()
        let stateR = SingleStateKeeper(state, "pick") :> StateKeeper<_>
        let tran = {state = stateR; commit = fun v -> 
                                                stateR.Stop()
                                                res.signal(v,stateR.Value) |> ignore
                                                match v with
                                                    | Ok(v) -> true |> asyncReturn
                                                    | _ -> false |> asyncReturn}
        run 0 tran alt
        res.future
    ///run altrnative and return result as an promise
    let pick state alt  =
        async{
            let! r,_ = pickWithResultState state alt 
            return r
        }
    ///map state
    let mapSt lens alt =
        Alt((fun (procId,tran) ->async{
            let state = new MapStateKeeper<_,_>(tran.state,lens)
            run procId {commit = tran.commit;state = state} alt 
            return ()
        }), isMutatesState alt)
    ///alternative for a state operation execution
    let stateOp op =
        Alt((fun (procId,tran) ->async{
            let safeOp :StateOp<_,_> =
                                       fun s -> try 
                                                    match op s with
                                                        | NotBlocked(s,r) -> NotBlocked(s,Ok(r))
                                                        | Blocked -> Blocked 
                                                with exn -> NotBlocked(s,Error(exn))
            let! res = tran.state.Apply(procId,safeOp)
            //Logger.logf  "State client: recieved response %A" res
            match res with
                | Result(r) -> //Logger.logf  "State client: recieved response %A" r
                               let! _ = tran.commit(r) 
                               return ()
                | Die -> //Logger.log "State client: recieved response Die" 
                         let! _ = tran.commit(BlockedForever) 
                         return ()
        }),true)

    
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
    
