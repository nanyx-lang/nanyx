namespace Parser.Program

open System

/// Where each line of `source` starts, so that the line an offset is on, and a span's line and column, can be found
/// without walking the text again. Lines are separated by `\n`, and are numbered from 0.
type LineTable(source: string) =
    let starts =
        [| yield 0
           for i in 0 .. source.Length - 1 do
               if source[i] = '\n' then yield i + 1 |]

    /// How many lines the text has: one more than its line breaks.
    member _.Count = starts.Length

    /// The offset `line` starts at.
    member _.Start(line: int) = starts[line]

    /// The offset `line` ends at, before its line break.
    member _.End(line: int) =
        if line + 1 < starts.Length then starts[line + 1] - 1 else source.Length

    /// The line `offset` is on.
    member _.LineOf(offset: int) =
        match Array.BinarySearch(starts, offset) with
        | found when found >= 0 -> found
        | notFound -> ~~~notFound - 1

    /// The span from `start` to `finish` in the file `filePath`, which starts on the line `start` is on.
    member this.Span (filePath: string) (start: int) (finish: int) : SourceSpan =
        let line = this.LineOf start
        { File = filePath; Start = start; End = finish; StartLine = line; StartColumn = start - starts[line] }
