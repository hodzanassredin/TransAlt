namespace TransAlt

module Lens =
    type Lens<'r,'a> = {get:'r->'a; set:'r * 'a -> 'r}
    let id() = {get = id;set = fun (r,v) -> v}
    let idTyped<'s>() : Lens<'s,'s> = id()
    let zip a b = { get = fun r -> (a.get(r),b.get(r)); 
                    set = fun (r,(av,bv)) -> b.set(a.set(r,av), bv)}
    let merge (l1: Lens<'s,'r>) (l2: Lens<'r,'v>) = 
        { get = l1.get >> l2.get 
          set = fun (s,v) -> let r = l1.get(s)
                             let r2 = l2.set(r,v)
                             let s2 = l1.set(s,r2)  
                             s2}
module State =
    open System.Collections.Generic
    open System
    open System.Threading
    open Lens

    type IsMutatesState = bool

    type StateResp<'a> = | Result of 'a
                         | Die
    type OpResp<'a> = | NotBlocked of 'a
                      | Blocked
    type StateOp<'s,'r> = 's -> OpResp<'s *'r>
    type ProcessId = int
    type StateKeeper<'s when 's : not struct> =
       abstract member Apply<'r> : ProcessId * StateOp<'s,'r> -> Async<StateResp<'r>>
       abstract member Merge : StateKeeper<'s> -> Async<StateResp<unit>>
       abstract member Value : 's with get
       abstract member InitValue : 's with get
       abstract member IsNotChanged : bool with get
       abstract member Stop : unit -> unit
       abstract member MergeLens : Lens<'s,'r> -> StateKeeper<'r> -> Async<StateResp<unit>>
       abstract member IsThreadSafe : bool with get
       abstract member GetProcessId : IsMutatesState -> Async<ProcessId>
       abstract member ReleaseProcessId : ProcessId -> unit
       abstract member RunningProcsCountExcludeMe : ProcessId -> Async<int>

    type SingleStateKeeper<'s when 's : not struct>(value : 's,name:string) =
        let name = "SingleStateKeeper " + name + " " + Guid.NewGuid().ToString()
        do Logger.log name "starting" 
        let refCell = ref value
        interface StateKeeper<'s> with 
                member this.Apply (_,f) = 
                    let res = f !refCell
                    match res with
                        | NotBlocked(s,r) -> refCell := s
                                             async{return Result(r)}
                        | Blocked -> async{return Die}

                member this.Merge keeper = 
                    if obj.ReferenceEquals(keeper.InitValue,!refCell) 
                    then refCell := keeper.Value
                         async{return Result()}
                    else async{return Die}
        
                member this.Value with get() : 's = !refCell
                member this.InitValue with get() : 's = value
                member this.IsNotChanged with get() : bool = obj.ReferenceEquals(value, !refCell)
                member this.Stop () =  ()
                member this.MergeLens lens keeper = 
                    if obj.ReferenceEquals(keeper.InitValue, lens.get(!refCell))
                    then refCell := lens.set(!refCell, keeper.Value)
                         async{return Result()}
                    else async{return Die}
                member this.IsThreadSafe with get() : bool = false
                member this.GetProcessId mutator = Async.FromContinuations(fun (cont,_,_ )->cont(0))
                member this.ReleaseProcessId _ = ()
                member this.RunningProcsCountExcludeMe _ = Async.FromContinuations(fun (cont,_,_)-> cont(0)) 
    //not finished obj reference problems
    type MapStateKeeper<'s, 's2 when 's : not struct and 's2 : not struct>(state:StateKeeper<'s>, lens : Lens<'s,'s2>) =
        let initValue = lens.get(state.InitValue)
        interface StateKeeper<'s2> with 
                member this.Apply<'r> (procId,f:StateOp<'s2,'r>) :  Async<StateResp<'r>>= 
                    let fmap :StateOp<'s,'r> = 
                        fun s -> let res = f(lens.get(s))
                                 match res with
                                     | NotBlocked(s2,r) -> NotBlocked(lens.set(s, s2),r)
                                     | Blocked -> Blocked
                    state.Apply(procId,fmap)

                member this.Merge keeper = state.MergeLens lens keeper
                member this.Value with get() : 's2 = lens.get(state.Value)
                member this.InitValue with get() : 's2 = initValue
                member this.IsNotChanged with get() : bool = state.IsNotChanged
                member this.Stop () =  state.Stop()
                member this.MergeLens lens2 keeper = state.MergeLens (Lens.merge lens lens2) keeper
                member this.IsThreadSafe with get() : bool = state.IsThreadSafe
                member this.GetProcessId mutator = state.GetProcessId mutator
                member this.ReleaseProcessId procId = state.ReleaseProcessId procId
                member this.RunningProcsCountExcludeMe procId = state.RunningProcsCountExcludeMe procId

    type internal StateMessage<'s when 's : not struct> = 
            | Apply of (ProcessId * (StateResp<'s> -> OpResp<'s>)) 
            | Merge of (unit-> bool) * (unit -> unit) * (StateResp<unit> -> unit)
            | Stop
            | GetProcessId of AsyncReplyChannel<ProcessId>
            | ReleaseProcess of ProcessId
            | RunningProcsCountExcludeMe of ProcessId * AsyncReplyChannel<int>
    and MailboxStateKeeper<'s when 's : not struct>(value : 's,name:string) =
            let name = "MailboxStateKeeper " + name + " " + Guid.NewGuid().ToString()
            do Logger.log name "starting" 
            let procIdGen = ref 0
            let runningProcs = ref Set.empty
            let retryLimit = 1000
            let idle = new Queue<ProcessId * _>()
            let refCell = ref value
            let rec apply (f,count) =
                if count > retryLimit 
                    then //Logger.log name "State: sending die"
                         f(Die) |> ignore
                         None
                    else match f(Result(!refCell)) with
                            | NotBlocked(state) -> 
                                            //Logger.log name "State: updating state"
                                            refCell := state
                                            None
                            | Blocked -> //Logger.log name "State: blocked on apply adding to a waiting list"
                                         Some(f,count + 1)
            let rec checkIdle () =
                //Logger.log name "proccessing idle queue"
                let mutable resolved = false
                for i in 1..idle.Count do
                    let procId,onHold = idle.Dequeue()
                    match apply(onHold) with
                    | Some(bad) ->  idle.Enqueue(procId,bad)
                    | _ -> //Logger.log name "State: not bloked item from idle queue"
                           resolved <- true
                if resolved then checkIdle ()
                           

            let killIdle () =
                //Logger.log name "State: killing idle procs"
                while idle.Count > 0 do
                    let _,(f,_) = idle.Dequeue()
                    f(Die) |> ignore

            let rec notBlockedRunningCount excludeProcId =
                let mutable count = runningProcs.Value.Count
                for (procId,_) in idle.ToArray() do
                    if runningProcs.Value.Contains(procId) then count <- count - 1
                match excludeProcId with
                    | None -> ()
                    | Some(procId) -> if runningProcs.Value.Contains(procId) then count <- count - 1
                count
                
            let createProc (inbox:MailboxProcessor<StateMessage<'s>>) =
                let rec loop () =
                    async { let! msg = inbox.Receive()                           
                            match msg with 
                                | Apply(procId,f) ->
                                    //Logger.log name "State: applying msg"
                                    let bad = apply (f,0)
                                    match bad with
                                        | Some(bad) -> //Logger.log name "State: locked adding to idle"
                                                       idle.Enqueue(procId,bad)
                                                       if notBlockedRunningCount None = 0 then killIdle()
                                        | None -> checkIdle () 
                                    return! loop() 
                                | Merge(precondition, doAct, reply) ->
                                    if precondition() then doAct()
                                                           reply(Result())
                                                           checkIdle ()
                                                      else reply(Die)
                                    return! loop()
                                | Stop -> killIdle()
                                          //Logger.logf name "stopping with final state %A" !refCell
                                          return ()
                                | GetProcessId(reply) -> Logger.logf name "GetProcessId %A" !procIdGen
                                                         reply.Reply(!procIdGen)
                                                         runningProcs := runningProcs.Value.Add(!procIdGen)
                                                         procIdGen := !procIdGen + 1
                                                         return! loop()
                                | ReleaseProcess(procId) -> runningProcs := runningProcs.Value.Remove(procId)
                                                            if notBlockedRunningCount None = 0 then killIdle()
                                                            return! loop()
                                | RunningProcsCountExcludeMe(procId,reply) -> reply.Reply(notBlockedRunningCount (Some(procId)))
                                                                              return! loop()}

                loop ()
            let cts = new CancellationTokenSource()
            let agent = MailboxProcessor.Start(createProc)

            let fToMsg (f:StateOp<'s,'r>) (holder:Promise.Promise<StateResp<'r>>) = 
                function
                    |Result(state:'s) -> 
                        let res = f(state)
                        match res with
                            | NotBlocked(state, res) -> holder.signal(Result(res)) |> ignore
                                                        NotBlocked(state)
                            | Blocked -> Blocked
                    | Die ->  holder.signal(Die) |> ignore
                              Blocked

            interface StateKeeper<'s> with 
                member this.Apply (procId,f) = 
                    let holder = Promise.create<StateResp<'r>>()
                    if cts.Token.IsCancellationRequested then holder.signal(Die) |> ignore 
                    else //Logger.log name "State: recieving apply f"
                         agent.Post(Apply(procId, fToMsg f holder)) 
                    holder.future

                member this.Merge keeper = 
                    //Logger.log name "State: recieving Merge"
                    let holder = Promise.create<StateResp<unit>>()
                    if cts.Token.IsCancellationRequested then holder.signal(Die) |> ignore
                    else let check () = obj.ReferenceEquals(keeper.InitValue,!refCell)
                         let doAct () = refCell := keeper.Value
                         agent.Post(Merge(check, doAct, fun x -> holder.signal(x) |> ignore)) 
                    holder.future
        
                member this.Value with get() : 's = !refCell
                member this.InitValue with get() : 's = value
                member this.IsNotChanged with get() : bool = obj.ReferenceEquals(value, !refCell)
                member this.Stop () = 
                    //Logger.log name "State: recieving Stop"
                    cts.Cancel()
                    agent.Post(Stop)

                member this.MergeLens lens keeper = 
                    //Logger.log name "State: recieving MergeLens"
                    let holder = Promise.create<StateResp<unit>>()
                    if cts.Token.IsCancellationRequested then holder.signal(Die) |> ignore
                    else let check () = obj.ReferenceEquals(keeper.InitValue,lens.get(!refCell))
                         let doAct () = refCell := lens.set(!refCell, keeper.Value)
                         agent.Post(Merge(check, doAct, fun x -> holder.signal(x) |> ignore)) 
                    holder.future
                member this.IsThreadSafe with get() : bool = true
                member this.GetProcessId mutator = 
                    if mutator then agent.PostAndAsyncReply(fun replyChannel  -> GetProcessId(replyChannel))
                    else Async.FromContinuations(fun (cont,_,_) -> cont(-1))
                member this.ReleaseProcessId procId = 
                        if procId <> -1 then agent.Post(ReleaseProcess(procId))
                member this.RunningProcsCountExcludeMe procId = agent.PostAndAsyncReply(fun reply ->  RunningProcsCountExcludeMe(procId,reply))


