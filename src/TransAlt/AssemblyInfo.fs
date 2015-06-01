namespace System
open System.Reflection

[<assembly: AssemblyTitleAttribute("TransAlt")>]
[<assembly: AssemblyProductAttribute("TransAlt")>]
[<assembly: AssemblyDescriptionAttribute("This project is a proof of concept implementation of async computation workflows composition with non determenistic choice, merge and bind based on immutable state with lock detection. Uses ideas from Stm,Hopac,Joinads.")>]
[<assembly: AssemblyVersionAttribute("0.0.1")>]
[<assembly: AssemblyFileVersionAttribute("0.0.1")>]
do ()

module internal AssemblyVersionInformation =
    let [<Literal>] Version = "0.0.1"
