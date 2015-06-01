(*** hide ***)
// This block of code is omitted in the generated HTML documentation. Use 
// it to define helpers that you do not want to show in the documentation.
#I "../../bin"

(**
TransAlt
======================
This project is a proof of concept implementation of async computation workflows composition with non determenistic choice, merge and bind based on immutable state with lock detection. Uses ideas from Stm,Hopac,Joinads.

<div class="row">
  <div class="span1"></div>
  <div class="span6">
    <div class="well well-small" id="nuget">
      The TransAlt library can be <a href="https://nuget.org/packages/TransAlt">installed from NuGet</a>:
      <pre>PM> Install-Package TransAlt -Pre</pre>
    </div>
  </div>
  <div class="span1"></div>
</div>

Example
-------

This example demonstrates using a function defined in this sample library.

*)
#r "TransAlt/TransAlt.dll"
open TransAlt
open Alt
open Channel
open Lens
open System.Threading
type DeadlockSt =
    { get1C: Channel<unit>; 
      get2C: Channel<unit>;}

    static member get1 =
        { get = fun r -> r.get1C; 
          set = fun (r,v) -> { r with get1C = v }}

    static member get2 =
        { get = fun r -> r.get2C; 
          set = fun (r,v) -> { r with get2C = v }}

let deadlockSt = {get1C = EmptyUnbounded "get1C"
                  get2C = EmptyUnbounded "get2C"} 

let task1 =   tranB{
        let! x = DeadlockSt.get1.deq()
        do! after 100 ()
        let! y = DeadlockSt.get2.deq()
        do! DeadlockSt.get1.enq()
        do! DeadlockSt.get2.enq()
        return ()
    }
let task2 =  tranB{
        let! x = DeadlockSt.get2.deq()
        do! after 100 ()
        let! y = DeadlockSt.get1.deq()
        do! DeadlockSt.get1.enq()
        do! DeadlockSt.get2.enq()
        return ()
    }
let task3 = tranB{
        do! DeadlockSt.get1.enq()
        do! DeadlockSt.get2.enq()
        return ()
    }
//lock detection
mergeB{
    case task1
    case task2
    case task3
} |> pickWithResultState deadlockSt |> Async.RunSynchronously |> printfn "%A"
//lock resolution
mergeChooseXs[task1;task2;task3] |> pickWithResultState deadlockSt |> Async.RunSynchronously |> printfn "%A"

(**
Some more info

Samples & documentation
-----------------------

The library comes with comprehensible documentation. 
It can include tutorials automatically generated from `*.fsx` files in [the content folder][content]. 
The API reference is automatically generated from Markdown comments in the library implementation.

 * [Tutorial](tutorial.html) contains a further explanation of this sample library.

 * [API Reference](reference/index.html) contains automatically generated documentation for all types, modules
   and functions in the library. This includes additional brief samples on using most of the
   functions.
 
Contributing and copyright
--------------------------

The project is hosted on [GitHub][gh] where you can [report issues][issues], fork 
the project and submit pull requests. If you're adding a new public API, please also 
consider adding [samples][content] that can be turned into a documentation. You might
also want to read the [library design notes][readme] to understand how it works.

The library is available under Public Domain license, which allows modification and 
redistribution for both commercial and non-commercial purposes. For more information see the 
[License file][license] in the GitHub repository. 

  [content]: https://github.com/hodzanassredin/TransAlt/tree/master/docs/content
  [gh]: https://github.com/hodzanassredin/TransAlt
  [issues]: https://github.com/hodzanassredin/TransAlt/issues
  [readme]: https://github.com/hodzanassredin/TransAlt/blob/master/README.md
  [license]: https://github.com/hodzanassredin/TransAlt/blob/master/LICENSE.txt
*)
