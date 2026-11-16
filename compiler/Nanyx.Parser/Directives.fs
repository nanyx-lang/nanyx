/// The lines at the top of a script that are not code: its `#!` line and `::` directives.
[<AutoOpen>]
module Parser.Program.Header

open System
open Parser.Program

/// A word of a directive, and where it is written.
type DirectiveWord = { Text: string; Span: SourceSpan }

/// A `::` directive at the top of a script, which says what one entry of a package's manifest would: `::host
/// nanyx.console`, `::dependency acme.util ../util`, `::lint unusedBindings #error`. It is a line of words separated by
/// spaces: the directive's name, then what it takes.
type Directive =
    { /// The directive's name, then the words after it: `dependency`, `acme.util`, `../util`. Empty for a line with
      /// nothing after `::`. A string in quotes, `"a b"`, is one word.
      Words: DirectiveWord list
      /// The line, from `::` to its end.
      Span: SourceSpan
      /// The line as it is written, from `::` to its end.
      Text: string }

    /// `dependency`, or empty for a line with nothing after `::`.
    member this.Name = match this.Words with first :: _ -> first.Text | [] -> ""

    /// The words after the directive's name.
    member this.Arguments = match this.Words with _ :: rest -> rest | [] -> []

    /// Whether it is written as directives were before they were lines of words: a manifest's dotted key, with `=`
    /// and a TOML value to set one, `::host.nanyx.console = "0.28.0"`.
    member this.IsOldForm =
        this.Name.Contains '.' || this.Words |> List.exists (fun word -> not (word.Text.StartsWith "\"") && word.Text.Contains '=')

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

    /// The words of `text`, a directive's line without its `::`, which starts at `start` in the file `filePath` on the
    /// line `line`: runs of characters other than spaces, or a string in quotes, which may hold spaces. A `--` comment
    /// ends them.
    let private words (filePath: string) (line: int) (start: int) (text: string) : DirectiveWord list =
        let isSpace (c: char) = c = ' ' || c = '\t'
        let rec from (index: int) (found: DirectiveWord list) =
            if index >= text.Length || String.CompareOrdinal(text, index, "--", 0, 2) = 0 then
                List.rev found
            elif isSpace text[index] then
                from (index + 1) found
            else
                let finish =
                    if text[index] = '"' then
                        let rec closing (at: int) =
                            if at >= text.Length then text.Length
                            elif text[at] = '\\' then closing (at + 2)
                            elif text[at] = '"' then at + 1
                            else closing (at + 1)
                        closing (index + 1) |> min text.Length
                    else
                        let mutable at = index
                        while at < text.Length && not (isSpace text[at]) do
                            at <- at + 1
                        at
                let span = { File = filePath; Start = start + index; End = start + finish; StartLine = line; StartColumn = 2 + index }
                from finish ({ Text = text.Substring(index, finish - index); Span = span } :: found)
        from 0 []

    /// The directives at the top of the file at `filePath`, whose text is `source`.
    let read (filePath: string) (source: string) : Directive list =
        let source = normalize source
        headerLines source
        |> List.filter (fun (_, (start, _)) -> String.CompareOrdinal(source, start, "::", 0, 2) = 0)
        |> List.map (fun (line, (start, finish)) ->
            let text = source.Substring(start, finish - start).TrimEnd()
            { Words = words filePath line (start + 2) (text.Substring 2)
              Span = { File = filePath; Start = start; End = start + text.Length; StartLine = line; StartColumn = 0 }
              Text = text })

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
