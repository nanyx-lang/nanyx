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
    /// A doc comment, its text running from its `--|` to the end of its last line, line breaks included. It is on a
    /// line of its own.
    | DocLineComment of text: string * span: SourceSpan
    /// A run of this many consecutive blank lines (nothing but spaces and tabs), outside string literals and block
    /// comments, spanning from the start of the first to the end of the last.
    | BlankLines of count: int * span: SourceSpan

    member this.Span =
        match this with
        | LineComment(_, _, span)
        | DocLineComment(_, span)
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
let internal collectTrivia (filePath: string) (source: string) (comments: NotedComment list) (stringTexts: (int * int) list) : Trivia list =
    let lines = LineTable source
    let isBlank line =
        source.AsSpan(lines.Start line, lines.End line - lines.Start line).Trim(" \t".AsSpan()).IsEmpty
    // A line whose preceding line break is inside a string literal or a block comment is part of it.
    let inStringOrComment line =
        line > 0
        && stringTexts @ (comments |> List.map (fun comment -> comment.Start, comment.End)) |> List.exists (fun (start, finish) -> start <= lines.Start line - 1 && lines.Start line - 1 < finish)

    let commentTrivia =
        comments
        // FParsec restores the state when it backtracks, so a comment is noted once; this is only a safety net.
        |> List.distinct
        |> List.map (fun comment ->
            let text = source.Substring(comment.Start, comment.End - comment.Start)
            let span = lines.Span filePath comment.Start comment.End
            if comment.IsDoc then
                DocLineComment(text, span)
            else
                let lineStart = lines.Start(lines.LineOf comment.Start)
                let before = source.AsSpan(lineStart, comment.Start - lineStart)
                LineComment(text, not (before.Trim(" \t".AsSpan()).IsEmpty), span))

    let blankTrivia =
        [ if source.Length > 0 then yield! [ 0 .. lines.Count - 1 ] ]
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
            BlankLines(run.Length, lines.Span filePath (lines.Start first) (lines.End last)))

    commentTrivia @ blankTrivia |> List.sortBy (fun (trivia: Trivia) -> trivia.Span.Start)
