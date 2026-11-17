/// A character that looks like one Nanyx reads but isn't, where a parse fails at it (see `Confusables`): it is named,
/// with a fix that writes what it looks like, in place of the parser's generic error, and the source is parsed again as
/// if it had been written so, to find the next.
module NyxCompiler.LookAlikes

open Parser.Program

/// The most look-alikes one parse is retried for.
let private limit = 100

/// The span of the text from `start` to `finish` in `source`, the file `filePath`.
let private spanIn (filePath: string) (source: string) (start: int) (finish: int) =
    let before = source.Substring(0, start)
    { File = filePath
      Start = start
      End = finish
      StartLine = before |> Seq.filter ((=) '\n') |> Seq.length
      StartColumn = start - (before.LastIndexOf '\n' + 1) }

/// The look-alike at `offset` in `text` with the others of its pair: a closing curly quote after an opening one on
/// its line, which is the string's end once the first is written as a quote.
let private withPair (text: string) (offset: int) (found: Confusables.Confusable) =
    let isQuote = found.Ascii = "\"" || found.Ascii = "'"
    let lineEnd = match text.IndexOf('\n', offset) with -1 -> text.Length | newline -> newline
    let closing =
        if not isQuote then None
        else
            seq { offset + 1 .. lineEnd - 1 }
            |> Seq.tryFind (fun at -> Confusables.at text at |> Option.exists (fun other -> other.Ascii = found.Ascii))
    offset :: Option.toList closing

/// The diagnostic for the look-alike `found`, at each of `offsets` of `original`, which are the same character or its
/// pair, with the fix that writes what each looks like.
let private diagnostic (filePath: string) (original: string) (found: Confusables.Confusable) (offsets: int list) =
    let edits =
        offsets
        |> List.map (fun at ->
            let written = Confusables.at original at |> Option.map _.Ascii |> Option.defaultValue found.Ascii
            { Span = spanIn filePath original at (at + 1); NewText = written })
    let message, title =
        match found.Ascii with
        | "" -> $"{Confusables.written found} can't be seen, and isn't whitespace Nanyx reads: remove it", $"Remove {Confusables.written found}"
        | " " -> $"{Confusables.written found} looks like a space, but it isn't one: write a space", $"Replace {Confusables.written found} with a space"
        | ascii ->
            let pair = if offsets.Length > 1 then $" (and its pair, '{original.[List.last offsets]}')" else ""
            $"{Confusables.written found}{pair} looks like `{ascii}` ({found.AsciiName}), but it isn't one: write `{ascii}`",
            $"Replace '{found.Character}' with '{ascii}'"
    { Diagnostics.at DiagnosticCodes.LookAlikeCharacter (spanIn filePath original offsets.Head (offsets.Head + 1)) message with
        Fixes = [ { Title = title; Edits = edits } ] }

/// The diagnostics for the look-alike characters that the parse of `source`, the text of the file `filePath`, fails at,
/// beginning with `error`, its first failure: each is named, and the text parsed again with it written as the
/// character it looks like, until it parses or fails somewhere else. That failure is given back, at its place in
/// `source`, to be reported as the syntax error it is. With no look-alike, the diagnostics are none and the failure is
/// `error`.
let diagnose (filePath: string) (source: string) (error: ParseError) : Diagnostic list * ParseError option =
    let original = source.Replace("\r\n", "\n").Replace("\r", "\n")
    // `origins.[i]` is the offset in `original` of the character at `i` in the text parsed, which differs from it once a
    // look-alike is written with more or fewer characters.
    let rec retry (text: string) (origins: int[]) (error: ParseError) (found: Diagnostic list) =
        let at = error.Span.Start
        match Confusables.at text at with
        | Some confusable when found.Length < limit ->
            let offsets = withPair text at confusable
            let diagnostic = diagnostic filePath original confusable (offsets |> List.map (fun offset -> origins.[offset]))
            let written, writtenOrigins =
                offsets
                |> List.rev
                |> List.fold
                    (fun (text: string, origins: int[]) offset ->
                        let ascii = (Confusables.at text offset).Value.Ascii
                        text.Substring(0, offset) + ascii + text.Substring(offset + 1),
                        Array.concat [ origins.[.. offset - 1]; Array.replicate ascii.Length origins.[offset]; origins.[offset + 1 ..] ])
                    (text, origins)
            match parseModuleFileWithNames filePath written with
            | Ok _ -> List.rev (diagnostic :: found), None
            | Error next -> retry written writtenOrigins next (diagnostic :: found)
        | _ when found.IsEmpty -> [], Some error
        | _ ->
            let start = origins.[min at (origins.Length - 1)]
            let finish = max start (origins.[min error.Span.End (origins.Length - 1)])
            List.rev found, Some { error with Span = spanIn filePath original start finish }
    retry original (Array.init (original.Length + 1) id) error []
