/// A signature written for a reader (`name: type`), laid out as the formatter lays out a def's: for a tool that shows
/// one in a narrow space, such as the info view.
module Nanyx.Formatter.SignatureLayout

open System

/// The text with no whitespace, and no `|` before a union's first case, which a union broken one case per line has:
/// what two layouts of one signature have in common.
let private withoutSpace (text: string) =
    String(text |> Seq.filter (Char.IsWhiteSpace >> not) |> Array.ofSeq).Replace("->|", "->").Replace(":|", ":")

/// `name: typeWritten` laid out to fit `width` characters as the formatter lays out `def name: type = …`: on one line
/// if it fits, else the name on its own line and the type on the next, after `: `, indented, and broken further where
/// it still doesn't fit (see `TypePrinter.brokenSignature`). `None` when the formatter
/// can't read it as a def's signature (a name or type it does not parse, such as a made-up type variable's), or would
/// write it differently but for its whitespace and a union's leading `|`.
let layOut (width: int) (name: string) (typeWritten: string) : string option =
    let options = { Formatter.Options.defaults with LineWidth = max width 1 }
    match Formatter.formatSource options "" $"def {name}: {typeWritten} = 0\n" with
    | Result.Error _ -> None
    | Result.Ok formatted ->
        let formatted = formatted.TrimEnd('\n')
        let lines = formatted.Split('\n') |> List.ofArray
        let signature =
            match List.rev lines with
            | last :: before when last.Trim() = "= 0" -> Some (String.Join("\n", List.rev before))
            | [ only ] when only.EndsWith " = 0" -> Some (only.Substring(0, only.Length - " = 0".Length))
            | _ -> None
        signature
        |> Option.filter (fun text -> text.StartsWith "def ")
        |> Option.map (fun text -> text.Substring "def ".Length)
        |> Option.filter (fun text -> withoutSpace text = withoutSpace $"{name}: {typeWritten}")
