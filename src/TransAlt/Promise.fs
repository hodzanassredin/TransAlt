namespace TransAlt

module Promise =
    open System.Threading.Tasks
    type Promise<'a> =
        abstract signal : 'a -> bool
        abstract future : Async<'a> with get
        abstract cancel : unit -> bool

    let create<'a> () =
        let tcs = new TaskCompletionSource<'a>()
        let ta: Async<'a> = Async.AwaitTask tcs.Task
        { new  Promise<'a> 
          with member this.signal v = tcs.TrySetResult(v)
               member this.future with get() = ta
               member this.cancel () = tcs.TrySetCanceled ()}

    let never<'a> () = 
        let p = create<'a> ()
        { new  Promise<'a> 
              with member this.signal v = false
                   member this.future with get() = p.future
                   member this.cancel () = false}

    let wrapWrkfl wrkfl = 
        let res = create()
        async{
            let! r = wrkfl
            res.signal(r) |> ignore
        }, res.future

