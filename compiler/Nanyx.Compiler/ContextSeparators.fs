/// The contexts in a list are separated by `,`: `[Eq(a), Hash(a)]`. Earlier versions of the language joined them with
/// `&`, and before that with `+`, which the parser still reads so that each can be reported here, with a fix that
/// writes `,` (#1531). A module that joins contexts so is reported before anything is inferred from it.
module NyxCompiler.ContextSeparators

open Parser.Program

/// The error for each `&` or `+` that joins two of `contexts`, as written in `source`, with the fix that writes `,`.
let private joinsBetween (source: string) (contexts: TypeExpr list) : Diagnostic list =
    contexts
    |> List.pairwise
    |> List.choose (fun (left, right) ->
        if SourceSpan.isNone left.Span || SourceSpan.isNone right.Span || right.Span.Start > source.Length then
            None
        else
            let between = source.Substring(left.Span.End, right.Span.Start - left.Span.End)
            match between.Trim() with
            | "&"
            | "+" as joiner ->
                let at = left.Span.End + between.IndexOf joiner
                let span = TextEdit.spanBetween source left.Span at (at + 1)
                // `,` goes straight after the context before it: `Eq(a) & Hash(a)` becomes `Eq(a), Hash(a)`.
                let edit = { Span = TextEdit.spanBetween source left.Span left.Span.End (at + 1); NewText = "," }
                let diagnostic =
                    Diagnostics.at DiagnosticCodes.ContextsJoined span $"Separate contexts with `,`, not `{joiner}`: `[Eq(a), Hash(a)]`"
                Some { diagnostic with Fixes = [ { Title = $"Replace `{joiner}` with `,`"; Edits = [ edit ] } ] }
            | _ -> None)

/// The error for each `&` or `+` between two contexts in `module'`, whose text is `source`.
let check (source: string) (module': Module) : Diagnostic list =
    let expressions = module' |> List.collect SyntaxTree.ofItem |> List.collect SyntaxTree.expressionsWithin
    (module' |> List.collect SyntaxTree.topLevelTypes) @ (expressions |> List.collect SyntaxTree.expressionTypes)
    |> List.collect TypeExpr.within
    |> List.collect (fun typeExpr ->
        match typeExpr.Kind with
        | TypeContext contexts
        | TypeWithContext(contexts, _) -> joinsBetween source contexts
        | _ -> [])
    |> List.distinctBy _.Span
    |> List.sortBy (fun diagnostic -> diagnostic.Span |> Option.map _.Start |> Option.defaultValue 0)
