/// The text of the module the formatter prints, and what it reads there that the syntax tree does not keep.
module internal Nanyx.Formatter.SourceText

open System
open System.Text.RegularExpressions
open Parser.Program

/// Queries on `source`, the text of the module being formatted.
type SourceText(source: string) =
    let lines = LineTable(source)

    let valid (span: SourceSpan) =
        not (SourceSpan.isNone span) && span.Start <= span.End && span.End <= source.Length

    let startsWith (span: SourceSpan) (s: string) =
        valid span && span.Start + s.Length <= source.Length && String.CompareOrdinal(source, span.Start, s, 0, s.Length) = 0

    let pipedMatchAt (e: Expression) (value: Expression) =
        if not (valid e.Span && valid value.Span) || startsWith e.Span "match" then
            None
        else
            let mutable i = value.Span.End
            while i < e.Span.End && (Char.IsWhiteSpace source[i] || source[i] = ')') do
                i <- i + 1
            if i + 6 <= source.Length && String.CompareOrdinal(source, i, "\\match", 0, 6) = 0 then Some i else None

    /// The line `offset` is on, counted from 0.
    member _.LineOf offset = lines.LineOf offset

    /// The offset of the end of the line `offset` is on.
    member _.LineEnd offset = lines.End(lines.LineOf offset)

    /// Whether `span` is a span of the source.
    member _.Valid (span: SourceSpan) = valid span

    /// Whether the text at `span` starts with `s`.
    member _.StartsWith (span: SourceSpan) (s: string) = startsWith span s

    /// The first character at or after `offset` that is not whitespace.
    member _.NextChar offset =
        let mutable i = offset
        while i < source.Length && Char.IsWhiteSpace source[i] do
            i <- i + 1
        if i >= 0 && i < source.Length then source[i] else '\000'

    /// The last character before `offset` that is not whitespace.
    member _.PreviousChar offset =
        let mutable i = offset - 1
        while i >= 0 && i < source.Length && Char.IsWhiteSpace source[i] do
            i <- i - 1
        if i >= 0 && i < source.Length then source[i] else '\000'

    /// The text of a double-quoted string that starts at `start` in the source, as it is written there, escapes and all:
    /// up to the next `"` or `{` that is not escaped. The parser decodes escapes, so where that is not found, or does
    /// not stand for `decoded`, the text is written with escapes of its own.
    member _.WrittenStringText (start: int option) (decoded: string) =
        let written =
            start
            |> Option.bind (fun start ->
                let mutable i = start
                while i < source.Length && source[i] <> '"' && source[i] <> '{' do
                    i <-
                        if source[i] <> '\\' then i + 1
                        // `\u{...}` runs to its `}`.
                        elif String.CompareOrdinal(source, i + 1, "u{", 0, 2) = 0 && source.IndexOf('}', i) > i then source.IndexOf('}', i) + 1
                        else i + 2
                let raw = source.Substring(start, min i source.Length - start)
                if StringEscapes.decode raw = Some decoded then Some raw else None)
        Doc.textOrVerbatim (written |> Option.defaultWith (fun () -> StringEscapes.escape decoded))

    /// Where the text after the interpolation hole `value` starts: past the `}` that closes the hole.
    member _.AfterHole (value: Expression) =
        let mutable i = value.Span.End
        while valid value.Span && i < source.Length && Char.IsWhiteSpace source[i] do
            i <- i + 1
        if valid value.Span && i < source.Length && source[i] = '}' then Some (i + 1) else None

    /// Where the `\match` is, if the match `e` on `value` was written `value \match | ...`, rather than `match value | ...`.
    member _.PipedMatchAt (e: Expression) (value: Expression) = pipedMatchAt e value

    member _.IsPipedMatch (e: Expression) (value: Expression) = (pipedMatchAt e value).IsSome

    /// The pattern of a destructuring def as written, from after `def` to the `=` outside any brackets, with its
    /// spacing made regular. It keeps the form it was written in: `def (x, y) =` or `def x, y =`.
    member _.WrittenPattern (span: SourceSpan) =
        if not (valid span) then
            None
        else
            let defAt = source.IndexOf("def", span.Start, StringComparison.Ordinal)
            if defAt < 0 || defAt >= span.End then
                None
            else
                let startAt = defAt + 3
                let mutable depth = 0
                let mutable equalsAt = -1
                let mutable i = startAt
                while equalsAt < 0 && i < span.End do
                    match source[i] with
                    | '(' -> depth <- depth + 1
                    | ')' -> depth <- depth - 1
                    | '=' when depth = 0 -> equalsAt <- i
                    | _ -> ()
                    i <- i + 1
                if equalsAt < 0 then
                    None
                else
                    Some(Regex.Replace(source.Substring(startAt, equalsAt - startAt), @"\s+", "").Replace(",", ", "))
