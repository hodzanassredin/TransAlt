module TransAlt.Tests

open TransAlt
open NUnit.Framework
open Alt
open System

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

let add_ack (alt:Alt<'s,'r>)  = 
        let promise = Promise.create()
        withAck (fun (ack : Alt<'s, bool>) -> 
                    let ack = map(ack, fun x -> promise.signal(x) |> ignore
                                                Unchecked.defaultof<'r>)
                    asyncReturn <| choose [|alt;ack|]
                    ), promise.future

module Assert =
    let IsOk (v:obj, (res,_)) = 
        match res with
            | Ok(res) -> Assert.AreEqual(v,res)
            | _ -> Assert.Fail()

    let IsBlockForever (res,_) = 
        match res with
            | BlockedForever -> Assert.Pass()
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
let ``promiase should not be able to be set two times`` () =
    let promise = Promise.create()

    Assert.IsTrue(promise.signal())
    Assert.IsFalse(promise.signal())

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
   let alt, ack = always(2) |> add_ack
   let _ =  alt |> pick () |> test
   let isNacked = ack |> Async.RunSynchronously |> not
   Assert.IsFalse(isNacked)

[<Test>]
let ``choose should return only one result`` () =
    Assert.IsOk(1, [|always(1); always(1)|] |>  choose |> pick () |> test)                                  

[<Test>]
let ``chooseXs should return first result`` () =
    Assert.IsOk("200 wins", [|after 300 "300 wins";after 200 "200 wins"|] |> choose |> pick () |> test)                                      

let error ms = async{
                    do! Async.Sleep(ms)
                    failwith "problem"
                } |> fromAsync

[<Test>]
let ``alt should return error on exception`` () =
    Assert.Throws<Exception> (fun () -> error 100 |> pick () |> test |> ignore)                                      
[<Test>]
let ``choose should return first not error result`` () =
    Assert.IsOk((), choose [|always();error 100|]|> pick () |> test)  
[<Test>]
let ``choose should return second result if first fails`` () =
    Assert.IsOk((), choose [|after 300 ();error 100|]|> pick () |> test)    
[<Test>]
let ``choose should return error if both fail`` () =
    Assert.Throws<AggregateException> (fun () -> choose[|error 300; error 100|]|> pick () |> test |> ignore)  

[<Test>]
let ``choose should return error when tasks return error and blocked result`` () =
    Assert.Throws<Exception> (fun () -> choose[|never();error 100|]|> pick () |> test |> ignore)  

[<Test>]
let ``choose should return first result and nack second`` () =
    let first, first_ack = after 200 200 |> add_ack; 
    let second, second_ack = after 300 300 |> add_ack; 
    let res = choose[|first;second|] |> pick () |> test
    Assert.IsOk(200, res)
    let first_ack = first_ack |> Async.RunSynchronously
    Assert.IsTrue(first_ack)
    let second_ack = second_ack |> Async.RunSynchronously
    Assert.IsFalse(second_ack)

let toAlways a alt  = bind(alt,fun  _ -> always(a))
let wrapToPromise name alt  = 
    let p = Promise.create()
    wrap(alt,fun x -> printfn "wrapToPromise signal from %s" name 
                      Logger.logf "wrapToPromise" "wrap signal from %s" name 
                      p.signal(x) |> ignore
                      async.Return ()), p

open Lens
open Channel

let St : Channel<int> = EmptyBounded(1) "channel"
let badLens = {get = fun _ ->failwith "lens bug";
               set = fun (r,v) -> v} 
let id_lens = Lens.idTyped<Channel<int>>()

[<Test>]
let ``alt should return error when something goes wrong on state update`` () =
    Assert.Throws<Exception> (fun () -> badLens.enq 1 |> pickWithResultState St |> test |> ignore)  

[<Test>]
let ``alt should change state`` () =
    let res, state = id_lens.enq(1) |> pickWithResultState St |> test
    Assert.IsOk((), res)
    Assert.IsTrue(state.Count = 1)
    let v = state.Get()
    match v with
        | State.NotBlocked(s,inpEl) -> Assert.AreEqual(inpEl,1) 
        | _ -> Assert.Fail()

[<Test>]
let ``bind should use the same state`` () =
    let res, _ = bind(id_lens.enq(1), fun _ -> id_lens.deq()) |> pickWithResultState St |> test
    Assert.IsOk(1, res)
 
[<Test>]
let ``wrap should be invoked only on success`` () =
    let first, first_wrap = after 200 200 |> wrapToPromise "first"; 
    let second, second_wrap = after 300 300 |> wrapToPromise "second"; 
    let res = choose[|first;second|] |> pick () |> test
    Assert.IsOk(200, res)
    let first_res = first_wrap.future |> Async.RunSynchronously
    Assert.AreEqual(first_res, Ok(200))
    //let snd_res = second_wrap.future |> Async.RunSynchronously
    let second_wrap = second_wrap.signal(true)
    Assert.IsTrue(second_wrap)

[<Test>]
let ``alt should return blocked result when proccess is blocked`` () =
    let res, _ = id_lens.deq() |> pickWithResultState St |> test
    Assert.IsBlockForever(res)

[<Test>]
let ``alt bind  should return blocked result when proccess is blocked`` () =
    let res, _ = bind(id_lens.deq(), fun _ -> id_lens.enq(1)) |> pickWithResultState St |> test
    Assert.IsBlockForever(res)

[<Test>]
let ``merge should resolve blocked sub task if second sub task could help`` () =
    let res, _ = Alt.mergeTpl(id_lens.deq(), id_lens.enq(1)) |> pickWithResultState St |> test
    Assert.IsOk((1,()),res)

[<Test>]
let ``merge should return results form all sub tasks`` () =
    let res = Alt.mergeTpl(always(1), always(2)) |> pick () |> test
    Assert.IsOk((1,2),res)

[<Test>]
let ``choose should return not blocked result`` () =
    let res = Alt.choose[|id_lens.enq(1); bind(id_lens.deq(), fun _ -> always())|] |> pick St |> test
    Assert.IsOk((),res)

