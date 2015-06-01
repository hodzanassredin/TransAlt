namespace TransAlt
//Promise abstraction
module Promise =
    open System.Threading.Tasks
    ///A promise p on p.signal(v) completes the future(Async<'a>) returned by p.future. 
    type Promise<'a> =
        abstract signal : 'a -> bool
        abstract future : Async<'a> with get
        abstract cancel : unit -> bool
    ///creates an unfinished promise
    let create<'a> () =
        let tcs = new TaskCompletionSource<'a>()
        let ta: Async<'a> = Async.AwaitTask tcs.Task
        { new  Promise<'a> 
          with member this.signal v = tcs.TrySetResult(v)
               member this.future with get() = ta
               member this.cancel () = tcs.TrySetCanceled ()}
    ///starts a workflow and returns a promise(the same as Async.StartChild)
    let wrapWrkfl wrkfl = 
        let res = create()
        async{
            let! r = wrkfl
            res.signal(r) |> ignore
        }, res.future

