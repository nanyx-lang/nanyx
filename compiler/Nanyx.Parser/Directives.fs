/// The lines at the top of a script that are not code: its `#!` line and `::` directives.
[<AutoOpen>]
module Parser.Program.Header

open System
open Parser.Program

/// A `::` directive at the top of a script, which says what a package manifest would, as the manifest's own dotted key
/// would: `::linters.default` is the table `[linters.default]`, and `::formatter.lineWidth = 80` sets `lineWidth = 80`
/// in `[formatter]`.
type Directive =
    { /// The parts of the dotted key after `::`: `[ "linters"; "default" ]`. Empty when the key is missing or is not
      /// a dotted key of names.
      Key: string list
      /// What comes after `=`: the TOML value it is, such as `80L` or `"error"`, or why it is not one. None for a
      /// table, which has no `=`.
      Value: Result<obj, string> option
      /// The line, from `::` to its end.
      Span: SourceSpan }

    /// The key as written, `linters.default`.
    member this.Name = String.Join(".", this.Key)

/// The lines at the top of a file that are not code: an optional `#!` line, then `::` directives, among blank lines
/// and `--` comments. They end at the first line of code, so they are read without parsing the file, and the parser
/// skips them. `::` only ever follows a name inside brackets, so no line of code starts with it.
module Directives =
    /// Each line of `source` (line breaks `\n`), as the offsets it starts and ends at, without its line break.
    let private lines (source: string) =
        let mutable start = 0
        [ while start <= source.Length do
              let finish = match source.IndexOf('\n', start) with -1 -> source.Length | i -> i
              yield start, finish
              start <- finish + 1 ]

    /// The `#!` line and the directive lines at the top of `source`, by line number, with their offsets.
    let private headerLines (source: string) =
        lines source
        |> List.indexed
        |> List.takeWhile (fun (line, (start, finish)) ->
            let text = source.Substring(start, finish - start)
            let trimmed = text.Trim()
            text.StartsWith "::" || (line = 0 && text.StartsWith "#!") || trimmed = "" || trimmed.StartsWith "--")
        |> List.filter (fun (line, (start, _)) ->
            String.CompareOrdinal(source, start, "::", 0, 2) = 0 || (line = 0 && String.CompareOrdinal(source, start, "#!", 0, 2) = 0))

    let private normalize (source: string) = source.Replace("\r\n", "\n").Replace("\r", "\n")

    /// The parts of a TOML dotted key, bare or quoted.
    let private keyPath (key: Tomlyn.Syntax.KeySyntax) =
        let part (syntax: Tomlyn.Syntax.BareKeyOrStringValueSyntax) =
            match syntax with
            | :? Tomlyn.Syntax.BareKeySyntax as bare when not (isNull bare.Key) -> Some bare.Key.Text
            | :? Tomlyn.Syntax.StringValueSyntax as quoted -> Some quoted.Value
            | _ -> None
        if isNull key || isNull key.Key then
            []
        else
            let parts = part key.Key :: [ for dotted in key.DotKeys -> if isNull dotted.Key then None else part dotted.Key ]
            if List.forall Option.isSome parts then List.choose id parts else []

    /// The value at `path` in `table`.
    let rec private valueAt (path: string list) (table: Tomlyn.Model.TomlTable) : obj option =
        match path with
        | [] -> None
        | [ last ] -> match table.TryGetValue last with | true, value -> Some value | false, _ -> None
        | first :: rest ->
            match table.TryGetValue first with
            | true, (:? Tomlyn.Model.TomlTable as inner) -> valueAt rest inner
            | _ -> None

    /// The key and value of a directive, the text after its `::`: a table's dotted key, `linters.default`, or a TOML
    /// key and value, `formatter.lineWidth = 80`. The key is empty when it is missing or is not a TOML key.
    let private parse (body: string) : string list * Result<obj, string> option =
        let table = Tomlyn.Parsing.SyntaxParser.Parse($"[{body}]", "", false)
        match List.ofSeq table.Tables with
        | [ header ] when not table.HasErrors && header.Items.ChildrenCount = 0 -> keyPath header.Name, None
        | _ ->
            let document = Tomlyn.Parsing.SyntaxParser.Parse(body, "", false)
            match List.ofSeq document.KeyValues with
            // The key is read even when what follows the value is not TOML, so the directive is still known by it.
            | keyValue :: rest when not (isNull keyValue.EqualToken) ->
                let path = keyPath keyValue.Key
                let text = body.Substring(min body.Length (keyValue.EqualToken.Span.End.Offset + 1)).Trim()
                let notAValue () = Result.Error $"'{text}' is not a TOML value; a string needs quotes, as in a manifest: \"{text}\""
                let value =
                    if document.HasErrors || path.IsEmpty || not rest.IsEmpty || not (Seq.isEmpty document.Tables) then
                        notAValue ()
                    else
                        try
                            match valueAt path (Tomlyn.TomlSerializer.Deserialize<Tomlyn.Model.TomlTable>(body, Tomlyn.TomlSerializerOptions())) with
                            | Some value -> Result.Ok value
                            | None -> notAValue ()
                        with :? Tomlyn.TomlException ->
                            notAValue ()
                path, Some value
            | _ -> [], None

    /// The directives at the top of the file at `filePath`, whose text is `source`.
    let read (filePath: string) (source: string) : Directive list =
        let source = normalize source
        headerLines source
        |> List.filter (fun (_, (start, _)) -> String.CompareOrdinal(source, start, "::", 0, 2) = 0)
        |> List.map (fun (line, (start, finish)) ->
            let text = source.Substring(start, finish - start).TrimEnd()
            let key, value = parse (text.Substring 2)
            { Key = key
              Value = value
              Span = { File = filePath; Start = start; End = start + text.Length; StartLine = line; StartColumn = 0 } })

    /// Where the `#!` line and directives at the top of `source` (line breaks `\n`) end: the end of the last of them,
    /// without its line break, or 0 if it has none.
    let headerEnd (source: string) : int =
        match List.tryLast (headerLines source) with
        | Some (_, (_, finish)) -> finish
        | None -> 0

    /// `source` (line breaks `\n`) with its `#!` line and directives made blank, for the parser, which skips them.
    let blank (source: string) : string =
        match headerLines source with
        | [] -> source
        | header ->
            let chars = source.ToCharArray()
            for _, (start, finish) in header do
                for i in start .. finish - 1 do
                    chars[i] <- ' '
            String chars
