namespace NyxCompiler

open Parser.Program

/// A script: a module without an `@entry` whose top-level statements are the program. They run as its entry point,
/// passed to the console host's `run` as an `@entry` def is (see `Attributes`), so they read and write through the
/// `Console` the host provides. A value of the script that needs a context (`def line = readLine()`) runs in the entry
/// point too, as a local def, in the order written, so it is computed once; one that needs none stays at the top level
/// (see `InferState.ScriptEntryValues`).
module Scripts =
    /// `module'` with its statements made the body of a lambda passed to the host's `run`, followed by `()`, so that the
    /// lambda's type stays as it is when a value is moved in after the last statement (see `withEntryValues`). A
    /// statement in a module block (`inBlock`) is left where it is, to be reported. With the entry point and the lambda's
    /// node, if there are any statements.
    let asEntry (inBlock: SourceSpan -> bool) (module': Module) : Module * (EntryPoint * NodeId) option =
        let isStatement (item: TopLevelItem) =
            match item.Kind with
            | Expr expr -> not (inBlock expr.Span)
            | _ -> false
        match module' |> List.filter isStatement with
        | [] -> module', None
        | statements ->
            let host = Hosts.console
            let body =
                (statements |> List.choose (function Kind (Expr expr) -> Some (Statement.from expr (ExprStatement expr)) | _ -> None))
                @ [ Statement.synthetic (ExprStatement (Expression.synthetic UnitExpr)) ]
            let lambda = Expression.synthetic (Lambda([], Expression.synthetic (Block body), false))
            let call = Expression.synthetic (FunctionCall($"{Hosts.importAlias}.{Hosts.runFunction}", None, [ lambda ]))
            let entry =
                { Attribute = call.Id
                  Name = "the script"
                  Host = host
                  Argument = SourceSpan.none
                  Call = [ call.Id ] }
            let items = module' |> List.filter (isStatement >> not)
            // A script imports what it uses itself, `nanyx/console` among them: only the host is imported for it.
            Attributes.hostImport host :: items @ [ TopLevelItem.synthetic (Expr call) ], Some (entry, lambda.Id)

    /// `module'`, a script typed as `asEntry` made it, with each of its values in `entryValues` moved from the top level
    /// into the entry point `entry`, as a local def among the statements in the order they are written.
    let withEntryValues (entry: EntryPoint) (entryValues: Set<string>) (module': Module) : Module =
        let moved =
            module'
            |> List.choose (function
                | Kind (Def (ValueDef(_, name, typeOpt, value))) when entryValues.Contains name ->
                    Some (Statement.from value (DefStatement(false, name, typeOpt, value)))
                | _ -> None)
        if moved.IsEmpty then
            module'
        else
            module'
            |> List.choose (fun item ->
                match item.Kind with
                | Def (ValueDef(_, name, _, _)) when entryValues.Contains name -> None
                | Expr ({ Kind = FunctionCall(run, span, [ { Kind = Lambda(parameters, ({ Kind = Block statements } as block), takesUnit) } as lambda ]) } as call)
                    when call.Id = entry.Attribute ->
                    // The `()` that ends the body stays last.
                    let written, final = List.splitAt (statements.Length - 1) statements
                    let block = { block with Kind = Block ((written @ moved |> List.sortBy _.Span.Start) @ final) }
                    let lambda = { lambda with Kind = Lambda(parameters, block, takesUnit) }
                    Some { item with Kind = Expr { call with Kind = FunctionCall(run, span, [ lambda ]) } }
                | _ -> Some item)
