namespace TransAlt
module Logger = 
    open System
    let skip = [
                "choose";
                "merge";
                "SingleStateKeeper";
                "MailboxStateKeeper";
                "AltGet"; 
                "AltAdd"
                ]
    let agent = MailboxProcessor<string * string>.Start(fun inbox ->
                            let rec loop n =
                                async {
                                        let! who, msg = inbox.Receive();
                                        if List.exists(fun x -> who.StartsWith(x)) skip then ()
                                        else printfn "%s %s: %s" (DateTime.Now.ToString()) who msg
                                        return! loop ()
                                }
                            loop ())
    let log who msg = agent.Post(who,msg)
    let logf who fmt msg = agent.Post(who,sprintf fmt msg)
