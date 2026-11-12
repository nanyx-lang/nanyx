/// What a parse reports when it fails, and the comments and blank lines it skips, which the formatter puts back.
[<AutoOpen>]
module Parser.Program.SourceTrivia

open System

/// Why a module could not be parsed, and where.
type ParseError = { Message: string; Span: SourceSpan }

/// What the parser skips that the formatter has to put back, and where it is. Neither span includes the final line
/// break.
type Trivia =
    /// A `--` comment, its text starting with `--`, and spanning to the end of its line. `trailing` when code comes
    /// before it on its line. A block comment (`---` on a line of its own) is one too, whose text runs from its opening
    /// line of dashes to the end of its closing one, line breaks included.
    | LineComment of text: string * trailing: bool * span: SourceSpan
    /// A run of this many consecutive blank lines (nothing but spaces and tabs), outside string literals and block
    /// comments, spanning from the start of the first to the end of the last.
    | BlankLines of count: int * span: SourceSpan

    member this.Span =
        match this with
        | LineComment(_, _, span)
        | BlankLines(_, span) -> span

/// A module as written, for tools that print it back, such as the formatter.
type SourceModule =
    { /// The top-level items as parsed: unlike `parseModuleFile`, `module name =` blocks are kept as they are, not
      /// expanded into qualified definitions.
      Items: TopLevelItem list
      /// The comments and runs of blank lines, in source order.
      Trivia: Trivia list
      /// The source the spans index into: line endings normalised to `\n`, trailing whitespace removed.
      Source: string }

/// The comments and runs of blank lines in `source`, in source order, given the comments the parser skipped and
/// where the text of its string literals is (each as start and end offsets).
let internal collectTrivia (filePath: string) (source: string) (comments: (int * int) list) (stringTexts: (int * int) list) : Trivia list =
    // The offset each line starts at.
    let lineStarts =
        [| yield 0
           for i in 0 .. source.Length - 1 do
               if source[i] = '\n' then yield i + 1 |]
    let lineEnd line =
        if line + 1 < lineStarts.Length then lineStarts[line + 1] - 1 else source.Length
    let spanAt line start finish =
        { File = filePath; Start = start; End = finish; StartLine = line; StartColumn = start - lineStarts[line] }
    let lineOf offset =
        match Array.BinarySearch(lineStarts, offset) with
        | found when found >= 0 -> found
        | notFound -> ~~~notFound - 1
    let isBlank line =
        source.AsSpan(lineStarts[line], lineEnd line - lineStarts[line]).Trim(" \t".AsSpan()).IsEmpty
    // A line whose preceding line break is inside a string literal or a block comment is part of it.
    let inStringOrComment line =
        line > 0
        && stringTexts @ comments |> List.exists (fun (start, finish) -> start <= lineStarts[line] - 1 && lineStarts[line] - 1 < finish)

    let commentTrivia =
        comments
        // FParsec restores the state when it backtracks, so a comment is noted once; this is only a safety net.
        |> List.distinct
        |> List.map (fun (start, finish) ->
            let line = lineOf start
            let before = source.AsSpan(lineStarts[line], start - lineStarts[line])
            LineComment(source.Substring(start, finish - start), not (before.Trim(" \t".AsSpan()).IsEmpty), spanAt line start finish))

    let blankTrivia =
        [ if source.Length > 0 then yield! [ 0 .. lineStarts.Length - 1 ] ]
        |> List.filter (fun line -> isBlank line && not (inStringOrComment line))
        // Consecutive blank lines make one run: each run is a list of its lines, last first.
        |> List.fold
            (fun runs line ->
                match runs with
                | (previous :: _ as run) :: rest when previous = line - 1 -> (line :: run) :: rest
                | _ -> [ line ] :: runs)
            []
        |> List.map (fun run ->
            let first = List.last run
            let last = List.head run
            BlankLines(run.Length, spanAt first lineStarts[first] (lineEnd last)))

    commentTrivia @ blankTrivia |> List.sortBy (fun (trivia: Trivia) -> trivia.Span.Start)
