namespace NyxCompiler

open System.Text.RegularExpressions
open Parser.Program

/// "Did you mean" for a name that is not in scope: the names in scope closest to it by edit distance, close enough
/// that they are likely what was meant. A name the compiler reserves, as `__for` or a compiler intrinsic
/// (`__nanyx_exit`), is never suggested, since it cannot be written in user code.
module NameSuggestions =
    /// A name as user code can write it: identifiers, each starting with a letter, joined by dots.
    let private writable = Regex(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)*$")

    /// The number of single-character insertions, deletions, substitutions and swaps of adjacent characters that
    /// turn `a` into `b` (the optimal string alignment distance).
    let distance (a: string) (b: string) : int =
        let d = Array2D.zeroCreate (a.Length + 1) (b.Length + 1)
        for i in 0 .. a.Length do d.[i, 0] <- i
        for j in 0 .. b.Length do d.[0, j] <- j
        for i in 1 .. a.Length do
            for j in 1 .. b.Length do
                let cost = if a.[i - 1] = b.[j - 1] then 0 else 1
                let best = min (min (d.[i - 1, j] + 1) (d.[i, j - 1] + 1)) (d.[i - 1, j - 1] + cost)
                d.[i, j] <-
                    if i > 1 && j > 1 && a.[i - 1] = b.[j - 2] && a.[i - 2] = b.[j - 1] then min best (d.[i - 2, j - 2] + 1)
                    else best
        d.[a.Length, b.Length]

    /// The most edits a name of `length` characters can be from a suggestion for it: one for every three characters,
    /// and at least one.
    let private threshold (length: int) = max 1 (length / 3)

    /// The part of `name` that is compared: what follows its last dot. A dotted name is compared only with names that
    /// have the same part before the last dot, so `string.trimm` is offered `string.trim`, not `list.trim`.
    let private split (name: string) =
        match name.LastIndexOf '.' with
        | -1 -> "", name
        | dot -> name.Substring(0, dot), name.Substring(dot + 1)

    /// The names of `candidates` closest to `name`, at most three, in order of name: those at the smallest distance
    /// from it, if that is within the threshold for its length.
    let closest (candidates: string seq) (name: string) : string list =
        let qualifier, last = split name
        let scored =
            candidates
            |> Seq.filter (fun candidate -> candidate <> name && writable.IsMatch candidate)
            |> Seq.choose (fun candidate ->
                let candidateQualifier, candidateLast = split candidate
                if candidateQualifier <> qualifier then None
                else
                    let d = distance last candidateLast
                    if d <= threshold last.Length then Some (candidate, d) else None)
            |> Seq.distinct
            |> List.ofSeq
        match scored with
        | [] -> []
        | _ ->
            let best = scored |> List.map snd |> List.min
            scored |> List.filter (fun (_, d) -> d = best) |> List.map fst |> List.sort |> List.truncate 3

    /// `suggestions` as the end of a message: ": did you mean 'a'?", ": did you mean 'a' or 'b'?"; empty for none.
    let private question (suggestions: string list) =
        match suggestions with
        | [] -> ""
        | _ ->
            let quoted = suggestions |> List.map (fun name -> $"'{name}'")
            let joined =
                match quoted with
                | [ only ] -> only
                | _ -> String.concat ", " (List.truncate (quoted.Length - 1) quoted) + " or " + List.last quoted
            $": did you mean {joined}?"

    /// The error `code` for `name`, written at `span`, which is not in scope, with `message`, then the names of
    /// `candidates` closest to it as a question at the end and a fix that writes each of them instead.
    let unknown (code: DiagnosticCode) (span: SourceSpan) (message: string) (candidates: string seq) (name: string) : Diagnostic =
        let suggestions = closest candidates name
        { Diagnostics.at code span (message + question suggestions) with
            Fixes = suggestions |> List.map (fun suggestion -> { Title = $"Write `{suggestion}`"; Edits = [ { Span = span; NewText = suggestion } ] }) }
