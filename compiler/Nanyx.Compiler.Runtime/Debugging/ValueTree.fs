namespace Nanyx.Runtime.Debugging

open System
open System.Text

/// A value as the debugger shows it: its text, as `dbg` writes it, and its parts, each with the name the Variables view
/// gives it, which it expands into. A value with no parts is a leaf.
type ValueTree =
    { Text: string
      Parts: (string * ValueTree) list }

/// The text a `dbg` printer writes (see `Transpiler.Wasm.CoreIrLowerDbg`) read back as a tree, so that a record, a
/// tuple, a tag's payload, a list, a map and a set expand in the Variables view: `(age = 36, name = "Ada")` into `age`
/// and `name`, `(1, "a")` into `0` and `1`, `#some((x = 1))` into its payload's parts, `[1, 2]` into `[0]` and `[1]`,
/// `Map["tea" => 3]` into `"tea"`, `Set[1, 2]` into `[0]` and `[1]`, and a nominal type, `Person(name = "Ada")`, into
/// its shape's parts. A number, a string, a char, a bool, `()`, a function, `<function int -> int>`, and anything it
/// can't read is a leaf.
module ValueTree =
    let private leaf (text: string) = { Text = text; Parts = [] }

    /// The parts of `text` separated by `separator` where nothing is open around it: brackets, a string, a char, or a
    /// value written by its type, `<function int, int -> int>`, whose `->` closes nothing, as a map's `=>` doesn't. None
    /// when something opened
    /// is never closed, or something closes what wasn't opened.
    let internal split (separator: string) (text: string) : string list option =
        let parts = ResizeArray<string>()
        let current = StringBuilder()
        let mutable depth = 0
        let mutable i = 0
        let mutable broken = false
        while i < text.Length && not broken do
            let c = text.[i]
            match c with
            | '"'
            | '\'' ->
                // A string or a char, with its escapes, to its closing quote.
                current.Append c |> ignore
                i <- i + 1
                while i < text.Length && text.[i] <> c do
                    if text.[i] = '\\' && i + 1 < text.Length then
                        current.Append(text.[i]).Append(text.[i + 1]) |> ignore
                        i <- i + 2
                    else
                        current.Append(text.[i]) |> ignore
                        i <- i + 1
                if i < text.Length then
                    current.Append c |> ignore
                    i <- i + 1
                else
                    broken <- true
            | '('
            | '['
            | '<' ->
                depth <- depth + 1
                current.Append c |> ignore
                i <- i + 1
            | '>' when i > 0 && (text.[i - 1] = '-' || text.[i - 1] = '=') ->
                current.Append c |> ignore
                i <- i + 1
            | ')'
            | ']'
            | '>' ->
                depth <- depth - 1
                if depth < 0 then broken <- true
                current.Append c |> ignore
                i <- i + 1
            | _ when depth = 0 && String.CompareOrdinal(text, i, separator, 0, separator.Length) = 0 ->
                parts.Add(current.ToString())
                current.Clear() |> ignore
                i <- i + separator.Length
            | _ ->
                current.Append c |> ignore
                i <- i + 1
        if broken || depth <> 0 then None
        else
            parts.Add(current.ToString())
            Some(List.ofSeq parts)

    /// Whether `text` is wholly one bracketed group, opened by `opening` and closed by `closing`: `(1, 2)`, not
    /// `(1) + (2)`.
    let private isGroup (opening: char) (closing: char) (text: string) =
        text.Length >= 2
        && text.[0] = opening
        && text.[text.Length - 1] = closing
        && split "\u0000" (text.Substring(1, text.Length - 2)) |> Option.isSome

    /// The name a part written `name = value` gives, and its value, if it is one: a record's field.
    let private field (part: string) =
        match split " = " part with
        | Some [ name; value ] when name.Trim() <> "" && name.Trim() |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '_' || c = '`' || c = ' ') ->
            Some(name.Trim(), value.Trim())
        | _ -> None

    /// The identifier `text` starts with (a nominal type's name, `Map`, `Set`), and what follows it.
    let private leadingName (text: string) =
        let length = text |> Seq.takeWhile (fun c -> Char.IsLetterOrDigit c || c = '_' || c = '.') |> Seq.length
        if length > 0 && Char.IsLetter text.[0] then Some(text.Substring(0, length), text.Substring length) else None

    /// `text`, as a `dbg` printer writes a value, as a tree (see `ValueTree`).
    let rec parse (text: string) : ValueTree =
        let text = text.Trim()
        let parts (inner: string) = split ", " inner |> Option.defaultValue [ inner ] |> List.filter (fun part -> part.Trim() <> "")
        if text = "()" || text = "" then leaf text
        elif isGroup '(' ')' text && (match parts (text.Substring(1, text.Length - 2)) with [ one ] -> (field one).IsSome | _ -> true) then
            // A tuple's parts by their places, a record's by their names. One value in brackets is that value.
            let parts = parts (text.Substring(1, text.Length - 2))
            { Text = text
              Parts =
                parts
                |> List.mapi (fun index part ->
                    match field part with
                    | Some(name, value) -> name, parse value
                    | None -> string index, parse part) }
        elif isGroup '[' ']' text then
            { Text = text; Parts = parts (text.Substring(1, text.Length - 2)) |> List.mapi (fun index part -> $"[{index}]", parse part) }
        elif text.StartsWith '#' then
            // A tag, with its payload's parts: one whose payload is a number or a string is a leaf, which says it all.
            match text.IndexOf '(' with
            | open' when open' > 0 && isGroup '(' ')' (text.Substring open') ->
                let payload = parse (text.Substring open')
                let payload = if payload.Parts.IsEmpty then parse (text.Substring(open' + 1, text.Length - open' - 2)) else payload
                { Text = text; Parts = payload.Parts }
            | _ -> leaf text
        else
            match leadingName text with
            | Some(_, rest) when isGroup '[' ']' rest ->
                // A map's entries by their keys, a set's or an array's elements by their places.
                let entries = parts (rest.Substring(1, rest.Length - 2))
                { Text = text
                  Parts =
                    entries
                    |> List.mapi (fun index entry ->
                        match split " => " entry with
                        | Some [ key; value ] -> key.Trim(), parse value
                        | _ -> $"[{index}]", parse entry) }
            | Some(_, rest) when isGroup '(' ')' rest ->
                // A nominal type is written by its name around its shape, whose parts it has.
                { Text = text; Parts = (parse rest).Parts }
            | _ -> leaf text
