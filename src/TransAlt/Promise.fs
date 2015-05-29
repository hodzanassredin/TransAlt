namespace TransAlt
module Promise =
    open System.Threading.Tasks
    type Promise<'a> = {signal: 'a -> bool; 
                        future : Async<'a>; 
                        cancel : unit -> bool}
    let create<'a> () =
        let tcs = new TaskCompletionSource<'a>()
        let ta: Async<'a> = Async.AwaitTask tcs.Task
        {signal = tcs.TrySetResult; 
         future = ta; 
         cancel = tcs.TrySetCanceled}
    let never<'a> () = {create<'a> () with signal = (fun _ -> false)}

    let wrapWrkfl wrkfl = 
        let res = create()
        async{
            let! r = wrkfl
            res.signal(r) |> ignore
        }, res.future

