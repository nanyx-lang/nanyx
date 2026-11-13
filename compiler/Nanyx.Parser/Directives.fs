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
      /// What comes after `=`, as written: a TOML value such as `80` or `"error"`. None for a table, which has no `=`.
      Value: string option
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

    /// One part of a directive's dotted key: a bare TOML key.
    let private keyPart = System.Text.RegularExpressions.Regex(@"^[A-Za-z0-9_-]+$")

    /// The directives at the top of the file at `filePath`, whose text is `source`.
    let read (filePath: string) (source: string) : Directive list =
        let source = normalize source
        headerLines source
        |> List.filter (fun (_, (start, _)) -> String.CompareOrdinal(source, start, "::", 0, 2) = 0)
        |> List.map (fun (line, (start, finish)) ->
            let text = source.Substring(start, finish - start).TrimEnd()
            let body = text.Substring 2
            let keyText, value =
                match body.IndexOf '=' with
                | -1 -> body, None
                | equals -> body.Substring(0, equals), Some(body.Substring(equals + 1).Trim())
            let key = keyText.Trim().Split '.' |> Array.map _.Trim() |> List.ofArray
            { Key = if key |> List.forall keyPart.IsMatch then key else []
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
