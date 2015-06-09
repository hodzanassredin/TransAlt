namespace TransAlt
///first class cancellable async operations    
module Transaction =
    open System

    type CommitReciever = bool -> Async<unit>
    let emptyReciever : CommitReciever= fun _ -> async.Return ()
    let mergeRecievers (xs: CommitReciever seq) =
        fun res -> async{
                for x in xs do
                    do! x(res)
            }
    type IsReader = bool
    type Transaction<'s,'r> = Tran of ('s -> Async<Result<'s,'r>>)
    and Result<'s,'r> = | Finished of 's * 'r * CommitReciever
                        | Sync of 's * Transaction<'s,'r> * CommitReciever * IsReader
    
    let fromAsync wrkfl = 
        Tran(fun state -> async{
            let! res = wrkfl
            return Finished(state, res, emptyReciever)
        })

    let always x = async.Return x |> fromAsync
    let withRcvr x rcvr = 
        Tran(fun state -> async{
            return Finished(state, x, rcvr)
        })

    let rec run state (Tran(f)) = 
         async{
            let! res = f(state)
            match res with
                 | Finished(s,r,c) -> return Some(s,r,c) 
                 | Sync(s,t,c,isReader) -> if isReader then do! c(false)
                                                            return None
                                           else return! run s t
        }
    let step (state:'s) (tr: Transaction<'s,'r>) = 
        match tr with Tran(f) -> f(state)

    let rec never<'s,'r> () : Transaction<'s,'r> = 
        Tran(fun state -> async{
                return Sync(state, never<'s,'r> (), emptyReciever, true)
            })

    let getState res = 
        match res with 
            | Finished (s,_,_) -> s
            | Sync (s,_,_,_) -> s    

    let choose xs =
        let rnd = new Random()
        Tran(fun state -> async{
            let run = run state
            let! res = xs |> Seq.map run |> Async.Parallel
            let res = Array.choose (fun x->x) res
            if res.Length = 0 then return Sync(state, never(), emptyReciever, true)
            else let winner = rnd.Next(res.Length - 1)
                 let s, r, c = res.[winner]
                 for i in 0.. res.Length do
                     if i <> winner then
                         let _,_,c = res.[i]
                         do! c(false)
                 return Finished(s,r,c)
        })

    let rec bind (t:Transaction<'s,'r>,f:'r->Transaction<'s,'r2>) =
        Tran(fun (state:'s) -> async{
            let! res = step state t
            match res with
                | Finished (s,r,c) -> let next = f(r)
                                      let! res = step s next
                                      match res with 
                                        | Finished (s2,r2,c2) -> return Finished(s2,r2, mergeRecievers [c;c2])
                                        | Sync (s2,t2,c2, isReader) -> return Sync(s2,t2, mergeRecievers [c;c2],isReader)
                | Sync (s,t,c,isReader) -> return Sync(s,bind (t,f),c,isReader) 
        })

    let rec inline mergeInt(xs:Transaction<'s,'r> seq) (finished: 'r[]) (finishedRcvr: CommitReciever): Transaction<'s,'r[]> =
        if Seq.length xs = 0 then withRcvr finished finishedRcvr
        else Tran(fun (state:'s) -> async{
                let step x = step state x
                let! res = xs |> Seq.map step |> Async.Parallel
                let states = res |> Array.map getState
                let inline folder s s2 = s +++ s2
                let state = Array.fold folder state states 
                let finished2, finishedRcvrs2 = res |> Array.choose (fun x -> match x with 
                                                                                | Finished (_,r,c) ->Some(r,c)
                                                                                | _ -> None) 
                                                    |> Array.unzip
                let synced, syncRcvrs, readers= res |> Array.choose (fun x -> match x with 
                                                                | Sync (_,t,c, isReader) ->Some(t,c,isReader)
                                                                | _ -> None) 
                                                            |> Array.unzip3
                let finishedRcvr = Array.collect id [|[|finishedRcvr|]; finishedRcvrs2|] |> mergeRecievers
                let allRcvrs =   Array.collect id [|[|finishedRcvr|]; syncRcvrs|] |> mergeRecievers
                let tran = mergeInt synced (Array.append finished finished2) finishedRcvr
                let IsReader = Array.TrueForAll(readers, fun x-> x)
                return Sync(state, tran, allRcvrs, IsReader)
             })

    let inline merge xs = mergeInt xs [||] emptyReciever

    let rec wrap tran c =
        Tran(fun state -> async{
            let! res = step state tran
            match res with 
                | Finished (s2,r2,c2) -> return Finished(s2,r2, mergeRecievers [c;c2])
                | Sync (s2,t2,c2, isReader) -> return Sync(s2,wrap t2 c, mergeRecievers [c;c2],isReader)
        })

    let withAck builder = async{
            let! tran, c = builder()
            return wrap tran c
        }
    ///maps a result of an alternative into other alternative
    let map (alt,f) = bind(alt, fun x -> always(f(x)))
        ///whileLoop alternaive for builder
    let rec whileLoop guard body =
        if guard() then bind(body, fun _ -> whileLoop guard body)
                        else always () 

    ///try with alternative for builder
    let tryWith body compensation = 
        Tran(fun state -> async{
            try
                let! res = step state body
                return res
            with ex ->  return compensation(ex)
        })

    ///try finally alternative for builder
    let tryFinally body compensation = 
       Tran(fun state -> async{
            try
                let! res = step state body
                return res
            finally
                compensation()
        })

    //if else expression for alternatives
    let ife (pred,thenAlt, elseAlt) = 
        bind(pred, fun x ->
                    if x then thenAlt
                    else elseAlt)
    let where (alt,f) = bind(alt, fun x ->
                            if f(x) then always x
                            else never ())

    let unit () = always ()

    let inline mergeTpl (t1,t2) =
        Tran(fun state -> async{
                            let t = merge[|map(t1, fun x -> Choice1Of2(x)); 
                                             map(t2, fun x -> Choice2Of2(x))|]
                            let maper res  = match res with
                                                | [| Choice1Of2(x); Choice2Of2(y)|] -> (x,y)
                                                | [| Choice2Of2(y); Choice1Of2(x)|] -> (x,y)
                            let res = map(t,maper)
                            return! step state res
                        })

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
        member this.Case(x,y) = choose[|y;x|]
        member this.Yield(()) = never()
    let chooseB = ChooseBuilder() 
    ///nice syntax for merge
    type MergeBuilder() =
        [<CustomOperation("case")>]
        member inline this.Case(x,y) = mergeTpl(y,x)
        member this.Yield(()) = always()
    let mergeB = MergeBuilder() 
    ///imitating joinads
    type AltQueryBuilder() =
        member this.Bind(m, f) = bind(m,f)
        member inline t.Zip(xs,ys) = mergeTpl(xs,ys)
        member t.For(xs,f) =  bind(xs,f)
        member inline t.For((x,y),f) =  bind(mergeTpl(x,y),f)
        member inline t.For((x,y,z),f) = 
                                  let tmp1 = mergeTpl(x,y)
                                  let tmp2 = map(mergeTpl(tmp1,z), fun ((x,y),z) -> x,y,z)
                                  bind(tmp2,f)
        member inline t.For(x,f) =  bind(merge (Array.ofSeq x),f)
        member t.Yield(x) = always(x)
        member t.Zero() = always()
        [<CustomOperation("where", MaintainsVariableSpace=true)>]
        member inlinex.Where
            ( source:Transaction<'s,'r>, 
              [<ProjectionParameter>] f:'r -> bool ) : Transaction<'s,'r> = where(source,f)

        [<CustomOperation("select")>]
        member x.Select
            ( source:Transaction<'s,'r>, 
              [<ProjectionParameter>] f:'r -> 'r2) : Transaction<'s,'r2> = map(source,f)
 
    let queryB = new AltQueryBuilder()


module Crdt =
    //https://github.com/aphyr/meangirls#lww-element-set
    type GSet<'a when 'a : comparison>() =
        let set = Set.empty
        with member x.add v = set.Add v
             member x.toSet  () = set 
             member x.difference (set2:GSet<'a>) = Set.difference set (set2.toSet())
             member x.intersect (set2:GSet<'a>) = Set.intersect set (set2.toSet())
             member x.union (set2:GSet<'a>) = Set.union set (set2.toSet())
             member x.isEmpty with get() = Set.isEmpty
             static member Empty with get() = new GSet<_>()
             static member (+++)  (set1:GSet<'a>,set2:GSet<'a>) = set1.union(set2) 
