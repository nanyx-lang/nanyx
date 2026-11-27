namespace NyxCompiler

open Parser.Program

/// The official linter's `redundantTypeName` rule: a pipe to a definition written with the name of the type it is
/// attached to, `people \list.map { ... }`, where the piped value's type already says it: `people \map { ... }` calls the
/// same definition (#3159). The type checker decides it where it resolves the pipe, as the name without the type's
/// would be resolved, so it is reported only where removing the name calls the same definition (see
/// `InferState.RedundantTypeNames`). Drawn faint, as unused code is.
module RedundantTypeNames =
    let rule =
        { Id = "redundantTypeName"
          Title = "Redundant type name"
          DefaultSeverity = Severity.Hint
          Unnecessary = true
          Description =
            "A pipe names the type a definition is attached to, as in `people \\list.map`, where the piped value's type already finds it: `people \\map` calls the same definition." }

    let lint (model: SemanticModel) : Report list =
        if model.RedundantTypeNames.IsEmpty then []
        else
        model.Syntax
        |> List.collect SyntaxTree.ofItem
        |> List.collect SyntaxTree.expressionsWithin
        |> List.choose (fun expr ->
            match expr.Kind with
            | Pipe(_, name, Some nameSpan, _) when model.RedundantTypeNames.Contains expr.Id && nameSpan.File = model.File ->
                let typeName = name.Substring(0, name.LastIndexOf '.')
                let written = model.Source.Substring(nameSpan.Start, nameSpan.End - nameSpan.Start)
                // The type's name and its dot, as written: `list.` of `list.map`.
                if not (written.StartsWith(typeName + ".")) then None
                else
                let span = TextEdit.spanBetween model.Source nameSpan nameSpan.Start (nameSpan.Start + typeName.Length + 1)
                let unqualified = name.Substring(typeName.Length + 1)
                Some
                    { Rule = rule.Id
                      Subject = Some name
                      Span = span
                      Message = $"`{unqualified}` is found from the piped value's type, which is a `{typeName}`: `{typeName}.` can be left out"
                      Related = []
                      Fixes = [ { Title = $"Remove `{typeName}.`"; Edits = [ { Span = span; NewText = "" } ] } ] }
            | _ -> None)
        |> List.distinctBy (fun report -> report.Span.Start)
        |> List.sortBy (fun report -> report.Span.Start)
