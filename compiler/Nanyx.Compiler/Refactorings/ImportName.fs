/// Import a name: on a name that is not in scope, each import that would bring it in, as the error's quick fixes offer
/// them (see `ImportFixes`), so that the refactor menu lists them too. Positions are character offsets into the source
/// as the parser saw it, with line endings normalised to `\n`, as for `Hover`.
module NyxCompiler.Refactorings.ImportName

open NyxCompiler

/// The codes of the errors for a name that is not in scope, which `ImportFixes` gives their imports.
let private unknownName =
    [ DiagnosticCodes.UnknownIdentifier; DiagnosticCodes.UnknownFunction; DiagnosticCodes.UnknownType; DiagnosticCodes.UnknownContext ]
    |> List.map _.Code

/// The refactorings at `offset` in the compiled root module `result`: on a name that is not in scope, each import its
/// error offers, a fix that leaves the name as it is and adds the import elsewhere (unlike one that writes a name
/// like it in its place). Empty elsewhere.
let at (result: CompileResult) (offset: int) : Offer list =
    result.Diagnostics
    |> List.filter (fun diagnostic -> List.contains diagnostic.Code unknownName)
    |> List.collect (fun diagnostic ->
        match diagnostic.Span with
        | Some span when span.Start <= offset && offset <= span.End ->
            diagnostic.Fixes
            |> List.filter (fun fix -> fix.Edits |> List.forall (fun edit -> edit.Span.End <= span.Start || span.End <= edit.Span.Start))
            |> List.map (fun fix -> Available { Title = fix.Title; Kind = RefactoringKind.Rewrite; Edits = fix.Edits; Rename = None })
        | _ -> [])
