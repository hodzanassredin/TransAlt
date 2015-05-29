namespace System
open System.Reflection

[<assembly: AssemblyTitleAttribute("TransAlt")>]
[<assembly: AssemblyProductAttribute("TransAlt")>]
[<assembly: AssemblyDescriptionAttribute("This project is a proof of concept implementation of async computation workflows composition with non determenistic choice, merge and bind based on immutable state with lock detection. Uses ideas from Stm,Hopac,Joinads.")>]
[<assembly: AssemblyVersionAttribute("1.0")>]
[<assembly: AssemblyFileVersionAttribute("1.0")>]
do ()

module internal AssemblyVersionInformation =
    let [<Literal>] Version = "1.0"
