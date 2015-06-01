namespace TransAlt

module Channel =
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

    open System.Runtime.CompilerServices
    open Lens
    open Alt

    [<Extension>]
    type ChEx () =
        [<Extension>]
        static member inline put(qlens: Lens<'s, Channel<'v>>, x) = 
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
        static member inline get(qlens: Lens<'s, Channel<'v>>) = 
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

