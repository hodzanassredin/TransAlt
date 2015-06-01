module TransAlt.Tests

open TransAlt
open NUnit.Framework
open Alt

let test x = let res = x |> Async.RunSynchronously
             Logger.logf "test result" "result is %A" res
             res
let testAsync x = async{let! x = x
                        Logger.logf "testAsync" "resut is %A" x} |> Async.Start 
let ignoreAsyncRes w = 
    async{
        let! _ = w
        return ()
    }

let add_nack (alt:Alt<'s,'r>)  = 
        let promise = Promise.create()
        withAck (fun (nack : Alt<'s, bool>) -> 
                    let nack = map(nack, fun x -> if not x then promise.signal(x) |> ignore
                                                  Unchecked.defaultof<'r>)
                    asyncReturn <| choose(alt,nack)
                    ), promise.future

module Assert =
    let IsOk (v:obj, res) = 
        match res with
            | Ok(res) -> Assert.AreEqual(v,res)
            | _ -> Assert.Fail()

    let IsBlockForever res = 
        match res with
            | BlockedForever -> Assert.Pass()
            | _ -> Assert.Fail()
    let IsError (msg,res) = 
        match res with
            | Error(exn) -> Assert.Equals(msg, exn.Message)
            | _ -> Assert.Fail()
[<Test>]
let ``query builder should select final result`` () =
   let result = queryB{
                    for x,y in (always(1),always(1))do
                        where (x = 1)
                        select (x + y)
                } |> pick () |> test

   Assert.IsOk(2,result)

[<Test>]
let ``query builder should block on when where is false`` () =
   let result = queryB{
                    for x,y in (always(1),always(1))do
                        where (x = 2)
                        select (x + y)
                } |> pick () |> test

   Assert.IsBlockForever(result)

[<Test>]
let ``always should return ok with specified value`` () =
   Assert.IsOk(1, always(1) |> pick () |> test)

[<Test>]
let ``winner whould not throw nack`` () =
   let alt, nack = always(2) |> add_nack
   let _ =  alt |> pick () |> test
   let isNacked = nack |> Async.RunSynchronously
   Assert.IsFalse(isNacked)

[<Test>]
let ``choose should return only one result`` () =
    Assert.IsOk(1, (always(1), always(1)) |>  choose |> pick () |> test)                                      

[<Test>]
let ``chooseXs should return only one result`` () =
    Assert.IsOk(1, [always(1); always(1)] |>  chooseXs |> pick () |> test)                                      

[<Test>]
let ``chooseXs should return first result`` () =
    Assert.IsOk("200 wins", [after 300 "300 wins";after 200 "200 wins"] |> chooseXs |> pick () |> test)                                      

let error ms = async{
                    do! Async.Sleep(ms)
                    failwith "problem"
                } |> fromAsync

[<Test>]
let ``alt should return error on exception`` () =
    Assert.IsError("problem", error 100 |> pick () |> test)                                      
[<Test>]
let ``choose should return first not error result`` () =
    Assert.IsOk((), choose(always(),error 100)|> pick () |> test)  
[<Test>]
let ``choose should return second result if first fails`` () =
    Assert.IsOk((), choose(after 300 (),error 100)|> pick () |> test)    
[<Test>]
let ``choose should return error if both fail`` () =
    Assert.IsError("problem", choose(error 300 ,error 100)|> pick () |> test) 

[<Test>]
let ``choose should return blocked when tasks return error and blocked result`` () =
    Assert.IsBlockForever(choose(never(),error 100)|> pick () |> test) 

[<Test>]
let ``choose should return first result and nack second`` () =
    let first, first_nack = after 200 200 |> add_nack; 
    let second, second_nack = after 300 300 |> add_nack; 
    let res = choose(first,second) |> pick () |> test
    Assert.IsOk(200, res)
    let first_nack = first_nack |> Async.RunSynchronously
    Assert.IsFalse(first_nack)
    let second_nack = second_nack |> Async.RunSynchronously
    Assert.IsFalse(second_nack)

let toAlways a alt  = bind(alt,fun  _ -> always(a))
let wrapToPromise alt = 
    let promise = Promise.create()
    wrap(alt,fun x -> promise.signal(x) |> ignore), promise.future

open Lens
open Channel

let St : Channel<int> = EmptyBounded(1) "channel"
let badLens = {get = fun _ ->failwith "lens bug";
               set = fun (r,v) -> v} 
let id_lens = Lens.idTyped<Channel<int>>()

[<Test>]
let ``alt should return error when something goes wrong on state update`` () =
    let res, _ = badLens.put 1 |> pickWithResultState St |> test
    Assert.IsError("lens bug", res) 

[<Test>]
let ``alt should change state`` () =
    let res, state = id_lens.put(1) |> pickWithResultState St |> test
    Assert.IsOk((), res)
    Assert.IsTrue(state.Count = 1)
    let v, _ = state.Get()
    match v with
        | NotBlocked(v) -> Assert.IsTrue(v = 1)
        | _ -> Assert.Fail()
    

//ChEx.AltAdd(id_lens, 1) |> pickWithResultState St |> test
//bind(ChEx.AltAdd(id_lens, 1), fun _ -> ChEx.altGet id_lens) |> pickWithResultState St |> test
//bind(ChEx.AltAdd(id_lens, 1), fun _ -> ChEx.AltAdd(id_lens, 1)) |> pickWithResultState St |> test
//ChEx.AltAdd (id_lens, 0) |> wrapPrint |> pickWithResultState St |> test
//ChEx.altGet id_lens |> wrapPrint |> pickWithResultState St |> test
//
//
//bind(ChEx.altGet id_lens, fun _ -> ChEx.AltAdd(id_lens, 1)) |> pickWithResultState St |> test
//
//merge(ChEx.altGet id_lens, ChEx.AltAdd (id_lens, 1))|> pickWithResultState St |> test
//merge(always(1), always(2))|> pick () |> test
//
//[ChEx.AltAdd(id_lens,1)  |> toAlways -1; ChEx.altGet id_lens] |> chooseXs |> pickWithResultState St |> test
//
////joinads samples
//type St2 =
//    { putStringC: Ch.Channel<string>; 
//      putIntC: Ch.Channel<int>; 
//      echoC: Ch.Channel<string>}
//
//    static member putString =
//        { get = fun r -> r.putStringC; 
//          set = fun (r,v) -> { r with putStringC = v }}
//
//    static member putInt =
//        { get = fun r -> r.putIntC; 
//          set = fun (r,v) -> { r with putIntC = v }}
//
//    static member echo =
//        { get = fun r -> r.echoC; 
//          set = fun (r,v) -> { r with echoC = v }}
//
//let state = {putStringC = Ch.EmptyUnbounded "putStringC"
//             putIntC = Ch.EmptyUnbounded "putIntC"
//             echoC = Ch.EmptyUnbounded "echoC"} 
//
//let rec whileOk alt = tranB{
//                         do! alt 
//                         return! whileOk alt
//                    } 
//
//
//let getPutString = tranB{
//    let! v = ChEx.altGet St2.putString
//    do! ChEx.AltAdd (St2.echo, sprintf "Echo %s" v)
//}
//
//let getPutInt = tranB{
//    let! v = ChEx.altGet St2.putInt
//    do! ChEx.AltAdd (St2.echo, sprintf "Echo %d" v) 
//}
//
//let getPut = choose(getPutString, getPutInt)
//
//let getEcho = tranB{
//    let! s = ChEx.altGet St2.echo
//    Logger.logf "getEcho" "GOT: %A" s
//}
//// Put 5 values to 'putString' and 5 values to 'putInt'
//let put5 =tranB { 
//            for i in [1 .. 5] do
//                Logger.logf "put5" "iter %d" i
//                do! ChEx.AltAdd (St2.putString ,sprintf "Hello %d!" i) 
//                do! ChEx.AltAdd (St2.putInt,i)} 
//put5 |> pickWithResultState state |> test
//isMutatesState (getPut)
//merge(whileOk getPut, put5)|> pickWithResultState state |> test
//merge(whileOk getEcho ,merge(put5,whileOk getPut))|> pickWithResultState state |> test
//mergeXs[whileOk getEcho; put5;whileOk getPut]|> pickWithResultState state |> test
//mergeB{
//    case put5
//    case (whileOk getPut)
//    case (whileOk getEcho)
//} |> pickWithResultState state |> test
////async cancellation
//let asyncWitchCancellation wrkfl =
//    withAck(fun nack -> async{
//        let cts = new CancellationTokenSource()
//        let wrkfl, res = Promise.wrapWrkfl(wrkfl)
//        Async.Start(wrkfl, cts.Token)
//        let nack = map(nack, fun commited ->  
//                                    if not commited then printfn "async cancelled"
//                                                         cts.Cancel())
//        async{
//            let! _ = pick () nack
//            return () 
//        } |> Async.Start
//        return fromAsync res
//    })
//let wrkfl = async{
//    do! Async.Sleep(1000)
//    return "async finished"
//}
//(asyncWitchCancellation wrkfl, always "always finished") |> choose |> pick ()|> test
//(asyncWitchCancellation wrkfl, never()) |> choose |> pick () |> test
//
////fetcher
//open Microsoft.FSharp.Control.WebExtensions
//open System.Net
//
//let fetchAsync (name, url:string) = async { 
//  let uri = new System.Uri(url)
//  let webClient = new WebClient()
//  let! html = webClient.AsyncDownloadString(uri)
//  return sprintf "Read %d characters for %s" html.Length name
//}
//
//let fetchAlt (name, url) : Alt<'s,string> =
//  fetchAsync (name, url) |> asyncWitchCancellation
//
//let urlList = [ "Microsoft.com", "http://www.microsoft.com/" 
//                "MSDN", "http://msdn.microsoft.com/" 
//                "Bing", "http://www.bing.com" ]
//
//let runFastest () =
//  urlList
//  |> Seq.map fetchAlt
//  |> chooseXs
//  |> pick ()
//  |> test
//
//let runAll () =
//  urlList
//  |> Seq.map fetchAlt
//  |> mergeXs
//  |> pick ()
//  |> test
//
//runFastest()
//runAll()
//
////one place buffer
//type St3 =
//    { putC: Ch.Channel<string>; 
//      getC: Ch.Channel<string>; 
//      emptyC: Ch.Channel<unit>; 
//      containsC: Ch.Channel<string>}
//
//    static member put =
//        { get = fun r -> r.putC; 
//          set = fun (r,v) -> { r with putC = v }}
//
//    static member get =
//        { get = fun r -> r.getC; 
//          set = fun (r,v) -> { r with getC = v }}
//
//    static member empty =
//        { get = fun r -> r.emptyC; 
//          set = fun (r,v) -> { r with emptyC = v }}
//
//    static member contains =
//        { get = fun r -> r.containsC; 
//          set = fun (r,v) -> { r with containsC = v }}
//
//let stateSt3 = { putC = Ch.EmptyUnbounded "putC"
//                 getC = Ch.EmptyUnbounded "getC"
//                 emptyC = Ch.EmptyUnbounded "emptyC"
//                 containsC = Ch.EmptyUnbounded "containsC"}
//let add_empty = ChEx.AltAdd (St3.empty, ())
//let alts = chooseB{
//    case (tranB{
//        do! ChEx.altGet St3.empty
//        let! x = ChEx.altGet St3.put
//        do! ChEx.AltAdd (St3.contains,x) 
//    })
//    case (tranB{
//        let! v = ChEx.altGet St3.contains
//        do! ChEx.AltAdd (St3.get,v) 
//        do! ChEx.AltAdd (St3.empty,()) 
//    })} 
//
//let put = tranB { 
//        do! fromAsync <| Async.Sleep 1000
//        for i in 0 .. 10 do
//          Logger.logf "put" "putting: %d" i
//          do! ChEx.AltAdd (St3.put,string i) 
//          do! fromAsync <| Async.Sleep 500 }
//
//let got = tranB { 
//            do! fromAsync <| Async.Sleep 250
//            let! v = ChEx.altGet St3.get
//            Logger.logf "got" "got: %s" v 
//        }
//mergeXs [whileOk got; put; whileOk alts; add_empty] |> pick stateSt3 |> test
//
//// Dinning philosophers
//let n = 5
//let mapReplace k v map =
//    let r = Map.remove k map
//    Map.add k v r
//
//type St4 =
//    { chopsticksCs: Map<int,Ch.Channel<unit>>; 
//      hungryC: Map<int,Ch.Channel<unit>>;}
//
//    static member chopsticks i =
//        { get = fun r -> Logger.logf "philosophers" "getting chopsticksCs %d " i
//                         r.chopsticksCs.[i]; 
//          set = fun (r,v) -> {r with chopsticksCs = mapReplace i v r.chopsticksCs}}
//                             
//    static member hungry i =
//        { get = fun r -> Logger.logf "philosophers" "getting hungry %d " i
//                         r.hungryC.[i]; 
//          set = fun (r,v) -> {r with hungryC = mapReplace i v r.hungryC}}
//
//let phioSt = {chopsticksCs = [ for i = 1 to n do yield i, Ch.EmptyUnbounded("chopsticksCs")] |> Map.ofList
//              hungryC = [ for i = 1 to n do yield i, Ch.EmptyBounded 1 "hungryC" ] |> Map.ofList}
//
//let philosophers = [| "Plato"; "Konfuzius"; "Socrates"; "Voltaire"; "Descartes" |]
//
//let randomDelay (r : Random) = Async.Sleep(r.Next(1, 3) * 1000) |> fromAsync
//
//let queries = Array.ofSeq (seq{
//                            for i = 1 to n do
//                                Logger.logf "philosophers" "left %d " i
//                                let left = St4.chopsticks i
//                                Logger.logf "philosophers" "left %d "(i % n + 1)
//                                let right = St4.chopsticks (i % n + 1)
//                                let random = new Random()
//                                //yield merge(always(i,random,left,right),merge(ChEx.altGet (St4.hungry i), merge(ChEx.altGet left, ChEx.altGet right)))
//                                yield queryB{
//                                    for _,_,_ in (ChEx.altGet (St4.hungry i), ChEx.altGet left, ChEx.altGet right) do
//                                    select(i,random,left,right)
//                                }
//                                }) 
//let findAndDo = tranB{
//                    let! i,random,left,right = chooseXs(queries)
//                    Logger.logf "philosophers" "%d wins " i
//                    Logger.logf "philosophers" "%s is eating" philosophers.[i-1] 
//                    do! randomDelay random
//                    do! ChEx.AltAdd (left,())  
//                    do! ChEx.AltAdd (right, ())  
//                    Logger.logf "philosophers" "%s is thinking" philosophers.[i-1] 
//                    return ()
//                }
//    
//let add_chopsticks = tranB{
//    for i in 1..n do
//        //Logger.logf "philosophers" "adding chopstick %d" i 
//        do! ChEx.AltAdd(St4.chopsticks i, ())
//    }
//let random = new Random()  
//let hungrySet = tranB{  
//        let i = random.Next(1, n)
//        Logger.logf "philosophers" "set hungry %s"  philosophers.[i]
//        do! ChEx.AltAdd  (St4.hungry(i),())
//        do! randomDelay random
//}
//
//mergeXs [whileOk findAndDo;whileOk hungrySet;add_chopsticks] |> pickWithResultState phioSt |> test

