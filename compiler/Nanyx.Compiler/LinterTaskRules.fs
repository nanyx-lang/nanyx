namespace NyxCompiler

open Parser.Program

/// The official linter's `unawaitedTask` rule: a task that is thrown away, though what it gives back isn't `()`, as
/// `start! compute()` on a line of its own. A task nobody awaits still runs to the end, since its def waits for it, so
/// one started for what it does, `start! log(line)`, is fine; one whose value is never read was more likely meant to be
/// awaited. The statements are those `unusedResults` finds, which leaves every task to this rule, and those that are a
/// workflow keyword, as `start!` is, which it leaves alone.
module UnawaitedTasks =
    let rule =
        { Id = "unawaitedTask"
          Title = "Unawaited task"
          DefaultSeverity = Severity.Warning
          Unnecessary = false
          Description =
            "A task that is thrown away though what it gives back isn't `()`: its value is never read. Await it, or start it for what it does, giving back `()`." }

    /// The statements of a block, other than its last, that are a workflow keyword, `start! compute()`, each with the
    /// type of the call desugaring made of it, which `unusedResults` leaves alone.
    let private keywordStatements (model: SemanticModel) : (Expression * Ty) list =
        model.Syntax
        |> List.collect SyntaxTree.ofItem
        |> List.collect SyntaxTree.blocksWithin
        |> List.collect (fun statements ->
            statements
            |> List.take (statements.Length - 1)
            |> List.choose (fun statement ->
                match statement.Kind with
                | ExprStatement({ Kind = WorkflowBindExpr _ } as value) ->
                    model.Lowered
                    |> Map.tryFind value.Id
                    |> Option.defaultValue []
                    |> List.tryPick (fun made -> model.Types |> Map.tryFind made)
                    |> Option.map (fun ty -> value, ty)
                | _ -> None))

    let lint (model: SemanticModel) : Report list =
        (UnusedResults.discarded model |> List.map (fun (_, value, ty, _) -> value, ty)) @ keywordStatements model
        |> List.sortBy (fun (value, _) -> value.Span.Start)
        |> List.choose (fun (value, ty) ->
            match LintedTasks.resultOf ty |> Option.map Unifier.unalias with
            | Some(TyPrimitive "unit")
            | Some(TyVar _)
            | None -> None
            | Some result ->
                Some
                    { Rule = rule.Id
                      Subject = None
                      Span = value.Span
                      Message =
                        $"This task gives back a '{Unifier.tyToDisplayString Ascii result}', which is never read: await it, or start it for what it does, giving back `()`"
                      Related = []
                      Fixes = [] })
