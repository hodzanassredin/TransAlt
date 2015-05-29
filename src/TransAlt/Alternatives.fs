



open System.Threading


    
module ALternatives =
    open State
    open System
    open Promise
    type StateChangeOp<'s,'r> = 's -> 's * 'r
    type TransactionResult<'r> = | Ok of 'r
                                 | BlockedForever
                                 | Error of Exception

    type Transaction<'s,'r when 's : not struct> = 
                                {state : StateKeeper<'s>;
                                 commit : TransactionResult<'r> -> Async<bool>}
    
    type Alt<'s,'r when 's : not struct> = 
        Alt of (ProcessId * Transaction<'s,'r> ->  Async<unit>) * IsMutatesState
    let isMutatesState =  function | Alt(_,ismut) -> ismut
    let isMutatesState2 =  function | Alt(_,ismut), Alt(_,ismut2) -> ismut || ismut2
    let asyncReturn x = Async.FromContinuations(fun (cont,_,_) -> cont(x))
    let asyncMap wrkfl f = async{
        let! r = wrkfl
        return f(r)
    }
    let run procId tran = function | Alt(alt,_) -> alt(procId,tran) |> Async.Start
    
    let fromAsync wrkfl =
        Alt((fun (_,tran:Transaction<'s,'r>) ->
            async{
                try
                    let! res = wrkfl
                    let! _ = tran.commit (Ok(res))
                    return ()
                with error -> let! _ = tran.commit (Error(error))
                              return ()
            }),false
        )

    let asyncAliasing = Async.StartChild
    type RestartSignal = | Done
                         | Restart
                         | ThrowError of exn
                         | ResolveStateProblem

    let rec choose<'s,'r when 's : not struct> (one:Alt<'s,'r>, two:Alt<'s,'r>)  =  
        Alt((fun (procId,tran:Transaction<'s,'r>) ->
            async{
                let commitOnce = Promise.create<unit>()
                let failSnd = Promise.create<unit>()
                let readyToRestart1 = Promise.create<RestartSignal>()
                let readyToRestart2 = Promise.create<RestartSignal>()
                let parentInitState = tran.state.Value
                let rec runSub alt (restarter:Promise.Promise<RestartSignal>) = 
                    let state = SingleStateKeeper(parentInitState, "choose") :> StateKeeper<'s>
                    let subCommit (x:'r TransactionResult) = async{
                            state.Stop()
                            match x with
                            | Ok(x) ->  if commitOnce.signal() then 
                                            let! stateReply = if state.IsNotChanged then asyncReturn (Result())
                                                              else tran.state.Merge state
                                            match stateReply with
                                                | Result() -> Logger.log "choose" "merge is ok stopping"
                                                              restarter.signal(Done) |> ignore
                                                              return! tran.commit (Ok(x))
                                                | Die -> Logger.log "choose" "winner merge problem initiating restart"
                                                         restarter.signal(Restart) |> ignore
                                                         return false
                                        else  restarter.signal(Restart)|> ignore
                                              return false
                            | Error(exn) -> Logger.logf "Error" "choose sub commits error %A" exn
                                            restarter.signal(ThrowError(exn))|> ignore
                                            return false
                            | BlockedForever -> restarter.signal(ResolveStateProblem)|> ignore
                                                return false
                        }
                    run 0 {state = state; commit = subCommit} alt 
                    
                
                runSub one readyToRestart1
                runSub two readyToRestart2
                let restarter = async{
                    let! res1 = readyToRestart1.future
                    let! res2 = readyToRestart2.future
                    //Logger.logf "choose" "resolution with %A" (res1,res2)
                    let! runProcCount = tran.state.RunningProcsCountExcludeMe(procId)
                    let canResolveBlock = runProcCount > 0 || (obj.ReferenceEquals(parentInitState, tran.state.Value)|> not)
                    Logger.logf "choose" "parent canResolveBlock = %A"  canResolveBlock
                    let restart() = Logger.logf "choose" "restarting with procId = %A" procId
                                    run procId tran (choose (one,two))
                    match res1, res2 with
                        | Done,_ -> return ()
                        | _,Done -> return ()
                        | Restart,_ -> restart()
                                       return ()
                        | _,Restart -> restart()
                                       return ()
                        | ThrowError(exn1),ThrowError(exn2) -> let! _ = tran.commit(Error(new AggregateException(exn1,exn2)))
                                                               return ()
                         | ThrowError(exn),_ -> let! _ = tran.commit(Error(exn))
                                                return ()
                         | _,ThrowError(exn) -> let! _ = tran.commit(Error(exn))
                                                return ()
                        | _,_ -> if canResolveBlock then restart()
                                                         return ()
                                 else Logger.logf "choose" "blocked stopping = %A"  procId
                                      let! _ = tran.commit(BlockedForever)
                                      return ()
                                      
                }
                restarter |> Async.Start
            }),isMutatesState2 (one,two)
        )

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
    let always v = v |> asyncReturn |> fromAsync
    let unit () = always ()
    let zero () = never () 
    let map (alt,f) = bind(alt, fun x -> always(f(x)))
    let never() = Alt(fun (procId,tran) -> async{
        let! _ = tran.commit(TransactionResult.BlockedForever)
        return ()
    }, false)
    let rec whileLoop guard body =
        if guard() then bind(body, fun x -> whileLoop guard body)
                        else always () 

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

    let tryFinally body compensation = 
        Alt((fun (procId,tran) ->async{
            let commit v =
                compensation()
                tran.commit(v)
            run procId {tran with commit = commit} body
        }),isMutatesState body)
    let mergeChoose (one:Alt<'s,'r>, two:Alt<'s,'r2>) =  
        choose(bind(one,fun r -> map(two, fun r2 -> r,r2)), 
               bind(two,fun r2 -> map(one, fun r -> r,r2)))

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
                let commit procId subCommit v =
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

    let ife (pred,thenAlt, elseAlt) = 
        bind(pred, fun x ->
                    if x then thenAlt
                    else elseAlt)
    let none() = always None
    let some alt = bind(alt, fun x -> always <| Some(x))

    let where (alt,f) = bind(alt, fun x ->
                            if f(x) then always x
                            else never ())
    let after ms v = async{
                        do! Async.Sleep(ms)
                        return v} |> fromAsync

    let chooseXs xs = Seq.fold (fun x y -> choose (x,y)) (never()) xs
    let mergeXs (xs:Alt<'s,'r> seq) : Alt<'s,'r seq> = 
        Seq.fold (fun (x:Alt<'s,'r seq>) (y:Alt<'s,'r>) -> 
            map(merge (x,y), fun (x,y) -> seq{yield y
                                              yield! x})) (always(Seq.empty)) xs
    let mergeChooseXs (xs:Alt<'s,'r> seq) : Alt<'s,'r seq> = 
        Seq.fold (fun (x:Alt<'s,'r seq>) (y:Alt<'s,'r>) -> 
            map(mergeChoose (x,y), fun (x,y) -> seq{yield y
                                                    yield! x})) (always(Seq.empty)) xs
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

    let pick state alt  =
        async{
            let! r,_ = pickWithResultState state alt 
            return r
        }

    let mapSt lens alt =
        Alt((fun (procId,tran) ->async{
            let state = new MapStateKeeper<_,_>(tran.state,lens)
            run procId {commit = tran.commit;state = state} alt 
            return ()
        }), isMutatesState alt)

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

    let private rand = new System.Random(DateTime.Now.Millisecond)
    let shuffle s = Seq.sortBy(fun _ -> rand.Next()) s

open ALternatives
open System
open System.Collections.Generic
open System.Threading

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

type ChooseBuilder() =
    [<CustomOperation("case")>]
    member this.Case(x,y) = ALternatives.choose(y,x)
    member this.Yield(()) = never()
let chooseB = ChooseBuilder() 

type MergeBuilder() =
    [<CustomOperation("case")>]
    member this.Case(x,y) = ALternatives.merge(y,x)
    member this.Yield(()) = always()
let mergeB = MergeBuilder() 

type AltQueryBuilder() =
    member this.Bind(m, f) = bind(m,f)
    member t.Zip(xs,ys) = ALternatives.merge(xs,ys)
    member t.For(xs,f) =  bind(xs,f)
    member t.For((x,y),f) =  bind(ALternatives.merge(x,y),f)
    member t.For((x,y,z),f) = let tmp1 = ALternatives.merge(x,y)
                              let tmp2 = map(ALternatives.merge(tmp1,z), fun ((x,y),z) -> x,y,z)
                              bind(tmp2,f)
    member t.For(x,f) =  bind(ALternatives.mergeXs(x),f)
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
    
module Ch =
    open State
    [<StructuredFormatDisplay("queue({AsString})")>]
    type Channel<'a> = {
                    name :string;
                    maxCount : int option; 
                    xs:'a list;
                    rxs:'a list}
                    with member x.AsString = sprintf "%s %A" x.name (List.append x.rxs (List.rev x.xs))  
                         member x.Count = x.xs.Length + x.rxs.Length
                         member q.IsEmpty = q.Count = 0
                         member q.Put x = 
                                if q.maxCount.IsSome && q.Count = q.maxCount.Value then Blocked 
                                else NotBlocked( {name = q.name; maxCount = q.maxCount; xs = q.xs; rxs = x::q.rxs} )
                         member q.Get () =
                                    if q.IsEmpty then Blocked
                                    else match q.xs with
                                            | [] -> let sub =  {name = q.name; maxCount = q.maxCount; xs = (List.rev q.rxs) ;rxs = []}  
                                                    sub.Get () 
                                            | y::ys -> NotBlocked( {name = q.name; maxCount = q.maxCount; xs = ys ;rxs = q.rxs}, y)


    let create limit name = {name = name; maxCount = limit; xs = [];rxs = []}
    let EmptyUnbounded name = create None name
    let EmptyBounded limit name= create (Some(limit)) name
open Ch
open System.Runtime.CompilerServices

open State
[<Extension>]
type ChEx () =
    [<Extension>]
    static member inline AltAdd(qlens: Lens<'s, Channel<'v>>, x) = 
        let changeF state = 
            let ch : Channel<_> = qlens.get state
            //Logger.logf "AltAdd" "putting to channel %A" (ch,x)
            let ch = ch.Put x
            match ch with
                | NotBlocked(ch) ->
                    Logger.logf "AltAdd" "putting to channel is ok  %A" ch
                    let nState = qlens.set(state,ch)
                    NotBlocked(nState, ())
                | Blocked -> Logger.logf "AltAdd" "putting to channel is blocked %A"  (ch,x)
                             Blocked
        stateOp changeF

    [<Extension>]
    static member inline altGet(qlens: Lens<'s, Channel<'v>>) = 
        let changeF state= 
            let ch : Channel<_> = qlens.get state
            //Logger.logf "altGet" "changeF getting from channel"
            let res = ch.Get()
            match res with
                | NotBlocked(ch,res) ->
                    Logger.logf  "AltGet" "getting from channel is ok %A" (ch,res)
                    let nState = qlens.set(state,ch)
                    NotBlocked(nState, res)
                | Blocked -> Logger.logf  "AltGet" "getting from channel is blocked %A" ch
                             Blocked
        stateOp changeF


let runSync state alt = alt |> pick state |> Async.RunSynchronously
let test x = x |> Async.RunSynchronously |> Logger.logf "test result" "result is %A" 
let testAsync x = async{let! x = x
                        Logger.logf "testAsync" "resut is %A" x} |> Async.Start 

queryB{
    for x,y in (always(1),always(1))do
        where (x = 2)
        select (x + y)
} |> pick () |> test

let ignoreAsyncRes w = 
    async{
        let! _ = w
        return ()
    }
 
let add_nack (alt:Alt<'s,'r>) = 
        withAck (fun (nack : Alt<'s, bool>) -> 
                    let nack = map(nack, fun x -> if not x then Logger.log "nacker" "nacked"
                                                  Unchecked.defaultof<'r>)
                    Logger.log "nacker" "starting nack interception"
                    asyncReturn <| choose(alt,nack)
                    )
                                         
always(2) |> pick () |> test
always(2) |> add_nack |> pick () |> test
(always(1), always(2)) |>  choose |> pick () |> test
[always(1); always(2)] |>  chooseXs |> pick () |> test
[always(1); always(2)] |> shuffle |> chooseXs |> pick () |> test
[after 300 "300 wins";after 200 "200 wins"] |> chooseXs |> pick () |> test
let error ms = async{
                    do! Async.Sleep(ms)
                    failwith "problem"
                } |> fromAsync
error 100 |> pick () |> test
choose(always(),error 100)|> pick () |> test
choose(after 300 (),error 100)|> pick () |> test
choose(error 300 ,error 100)|> pick () |> test
choose(never(),error 100)|> pick () |> test

 
[after 300 300 |> add_nack; 
 after 200 200 |> add_nack] |> chooseXs |> pick () |> test
((after 200 200 |> add_nack, 
  after 300 300 |> add_nack) |> choose,
 (after 400 200 |> add_nack, 
  after 500 300 |> add_nack) |> choose) |> choose |> pick () |> test
 
choose(fromAsync(async{do! Async.Sleep(1000)
                       do! Async.Sleep(1000)
                       return "async wins"}),
       after 3000 "after wins") |> pick () |> test

let toAlways a alt  = bind(alt,fun  _ -> always(a))
let wrapPrint alt = wrap(alt,fun x -> Logger.logf "wrapPrint" "commited succ %A" x)
let St : Ch.Channel<int> = Ch.EmptyBounded(1) "channel"
let badLens = {get = fun _ ->failwith "lens bug";
               set = fun (r,v) -> v} 
let id_lens = Lens.idTyped<Ch.Channel<int>>()
ChEx.AltAdd(badLens, 1) |> pickWithResultState St |> test
ChEx.AltAdd(id_lens, 1) |> pickWithResultState St |> test
bind(ChEx.AltAdd(id_lens, 1), fun _ -> ChEx.altGet id_lens) |> pickWithResultState St |> test
bind(ChEx.AltAdd(id_lens, 1), fun _ -> ChEx.AltAdd(id_lens, 1)) |> pickWithResultState St |> test
ChEx.AltAdd (id_lens, 0) |> wrapPrint |> pickWithResultState St |> test
ChEx.altGet id_lens |> wrapPrint |> pickWithResultState St |> test


bind(ChEx.altGet id_lens, fun _ -> ChEx.AltAdd(id_lens, 1)) |> pickWithResultState St |> test

merge(ChEx.altGet id_lens, ChEx.AltAdd (id_lens, 1))|> pickWithResultState St |> test
merge(always(1), always(2))|> pick () |> test

[ChEx.AltAdd(id_lens,1)  |> toAlways -1; ChEx.altGet id_lens] |> chooseXs |> pickWithResultState St |> test

//joinads samples
type St2 =
    { putStringC: Ch.Channel<string>; 
      putIntC: Ch.Channel<int>; 
      echoC: Ch.Channel<string>}

    static member putString =
        { get = fun r -> r.putStringC; 
          set = fun (r,v) -> { r with putStringC = v }}

    static member putInt =
        { get = fun r -> r.putIntC; 
          set = fun (r,v) -> { r with putIntC = v }}

    static member echo =
        { get = fun r -> r.echoC; 
          set = fun (r,v) -> { r with echoC = v }}

let state = {putStringC = Ch.EmptyUnbounded "putStringC"
             putIntC = Ch.EmptyUnbounded "putIntC"
             echoC = Ch.EmptyUnbounded "echoC"} 

let rec whileOk alt = tranB{
                         do! alt 
                         return! whileOk alt
                    } 


let getPutString = tranB{
    let! v = ChEx.altGet St2.putString
    do! ChEx.AltAdd (St2.echo, sprintf "Echo %s" v)
}

let getPutInt = tranB{
    let! v = ChEx.altGet St2.putInt
    do! ChEx.AltAdd (St2.echo, sprintf "Echo %d" v) 
}

let getPut = choose(getPutString, getPutInt)

let getEcho = tranB{
    let! s = ChEx.altGet St2.echo
    Logger.logf "getEcho" "GOT: %A" s
}
// Put 5 values to 'putString' and 5 values to 'putInt'
let put5 =tranB { 
            for i in [1 .. 5] do
                Logger.logf "put5" "iter %d" i
                do! ChEx.AltAdd (St2.putString ,sprintf "Hello %d!" i) 
                do! ChEx.AltAdd (St2.putInt,i)} 
put5 |> pickWithResultState state |> test
isMutatesState (getPut)
merge(whileOk getPut, put5)|> pickWithResultState state |> test
merge(whileOk getEcho ,merge(put5,whileOk getPut))|> pickWithResultState state |> test
mergeXs[whileOk getEcho; put5;whileOk getPut]|> pickWithResultState state |> test
mergeB{
    case put5
    case (whileOk getPut)
    case (whileOk getEcho)
} |> pickWithResultState state |> test
//async cancellation
let asyncWitchCancellation wrkfl =
    withAck(fun nack -> async{
        let cts = new CancellationTokenSource()
        let wrkfl, res = Promise.wrapWrkfl(wrkfl)
        Async.Start(wrkfl, cts.Token)
        let nack = map(nack, fun commited ->  
                                    if not commited then printfn "async cancelled"
                                                         cts.Cancel())
        async{
            let! _ = pick () nack
            return () 
        } |> Async.Start
        return fromAsync res
    })
let wrkfl = async{
    do! Async.Sleep(1000)
    return "async finished"
}
(asyncWitchCancellation wrkfl, always "always finished") |> choose |> pick ()|> test
(asyncWitchCancellation wrkfl, never()) |> choose |> pick () |> test

//fetcher
open Microsoft.FSharp.Control.WebExtensions
open System.Net

let fetchAsync (name, url:string) = async { 
  let uri = new System.Uri(url)
  let webClient = new WebClient()
  let! html = webClient.AsyncDownloadString(uri)
  return sprintf "Read %d characters for %s" html.Length name
}

let fetchAlt (name, url) : Alt<'s,string> =
  fetchAsync (name, url) |> asyncWitchCancellation

let urlList = [ "Microsoft.com", "http://www.microsoft.com/" 
                "MSDN", "http://msdn.microsoft.com/" 
                "Bing", "http://www.bing.com" ]

let runFastest () =
  urlList
  |> Seq.map fetchAlt
  |> chooseXs
  |> pick ()
  |> test

let runAll () =
  urlList
  |> Seq.map fetchAlt
  |> mergeXs
  |> pick ()
  |> test

runFastest()
runAll()

//one place buffer
type St3 =
    { putC: Ch.Channel<string>; 
      getC: Ch.Channel<string>; 
      emptyC: Ch.Channel<unit>; 
      containsC: Ch.Channel<string>}

    static member put =
        { get = fun r -> r.putC; 
          set = fun (r,v) -> { r with putC = v }}

    static member get =
        { get = fun r -> r.getC; 
          set = fun (r,v) -> { r with getC = v }}

    static member empty =
        { get = fun r -> r.emptyC; 
          set = fun (r,v) -> { r with emptyC = v }}

    static member contains =
        { get = fun r -> r.containsC; 
          set = fun (r,v) -> { r with containsC = v }}

let stateSt3 = { putC = Ch.EmptyUnbounded "putC"
                 getC = Ch.EmptyUnbounded "getC"
                 emptyC = Ch.EmptyUnbounded "emptyC"
                 containsC = Ch.EmptyUnbounded "containsC"}
let add_empty = ChEx.AltAdd (St3.empty, ())
let alts = chooseB{
    case (tranB{
        do! ChEx.altGet St3.empty
        let! x = ChEx.altGet St3.put
        do! ChEx.AltAdd (St3.contains,x) 
    })
    case (tranB{
        let! v = ChEx.altGet St3.contains
        do! ChEx.AltAdd (St3.get,v) 
        do! ChEx.AltAdd (St3.empty,()) 
    })} 

let put = tranB { 
        do! fromAsync <| Async.Sleep 1000
        for i in 0 .. 10 do
          Logger.logf "put" "putting: %d" i
          do! ChEx.AltAdd (St3.put,string i) 
          do! fromAsync <| Async.Sleep 500 }

let got = tranB { 
            do! fromAsync <| Async.Sleep 250
            let! v = ChEx.altGet St3.get
            Logger.logf "got" "got: %s" v 
        }
mergeXs [whileOk got; put; whileOk alts; add_empty] |> pick stateSt3 |> test

// Dinning philosophers
let n = 5
let mapReplace k v map =
    let r = Map.remove k map
    Map.add k v r

type St4 =
    { chopsticksCs: Map<int,Ch.Channel<unit>>; 
      hungryC: Map<int,Ch.Channel<unit>>;}

    static member chopsticks i =
        { get = fun r -> Logger.logf "philosophers" "getting chopsticksCs %d " i
                         r.chopsticksCs.[i]; 
          set = fun (r,v) -> {r with chopsticksCs = mapReplace i v r.chopsticksCs}}
                             
    static member hungry i =
        { get = fun r -> Logger.logf "philosophers" "getting hungry %d " i
                         r.hungryC.[i]; 
          set = fun (r,v) -> {r with hungryC = mapReplace i v r.hungryC}}

let phioSt = {chopsticksCs = [ for i = 1 to n do yield i, Ch.EmptyUnbounded("chopsticksCs")] |> Map.ofList
              hungryC = [ for i = 1 to n do yield i, Ch.EmptyBounded 1 "hungryC" ] |> Map.ofList}

let philosophers = [| "Plato"; "Konfuzius"; "Socrates"; "Voltaire"; "Descartes" |]

let randomDelay (r : Random) = Async.Sleep(r.Next(1, 3) * 1000) |> fromAsync

let queries = Array.ofSeq (seq{
                            for i = 1 to n do
                                Logger.logf "philosophers" "left %d " i
                                let left = St4.chopsticks i
                                Logger.logf "philosophers" "left %d "(i % n + 1)
                                let right = St4.chopsticks (i % n + 1)
                                let random = new Random()
                                //yield merge(always(i,random,left,right),merge(ChEx.altGet (St4.hungry i), merge(ChEx.altGet left, ChEx.altGet right)))
                                yield queryB{
                                    for _,_,_ in (ChEx.altGet (St4.hungry i), ChEx.altGet left, ChEx.altGet right) do
                                    select(i,random,left,right)
                                }
                                }) 
let findAndDo = tranB{
                    let! i,random,left,right = chooseXs(queries)
                    Logger.logf "philosophers" "%d wins " i
                    Logger.logf "philosophers" "%s is eating" philosophers.[i-1] 
                    do! randomDelay random
                    do! ChEx.AltAdd (left,())  
                    do! ChEx.AltAdd (right, ())  
                    Logger.logf "philosophers" "%s is thinking" philosophers.[i-1] 
                    return ()
                }
    
let add_chopsticks = tranB{
    for i in 1..n do
        //Logger.logf "philosophers" "adding chopstick %d" i 
        do! ChEx.AltAdd(St4.chopsticks i, ())
    }
let random = new Random()  
let hungrySet = tranB{  
        let i = random.Next(1, n)
        Logger.logf "philosophers" "set hungry %s"  philosophers.[i]
        do! ChEx.AltAdd  (St4.hungry(i),())
        do! randomDelay random
}

mergeXs [whileOk findAndDo;whileOk hungrySet;add_chopsticks] |> pickWithResultState phioSt |> test
