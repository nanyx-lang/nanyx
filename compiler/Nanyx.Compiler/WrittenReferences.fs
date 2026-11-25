/// The names a module refers to as its source writes them, for what shows them to the user: the linters, and the
/// editor's hover, definitions, references and renames. A typed module's `References` are what its code calls, which
/// the backend reads; they differ from the written ones where the compiler calls a def of its own in place of the one
/// written. A call last in a scope def, of another scope def, calls the def that runs the other's body in its caller's
/// scope (see `TaskScopes`).
module internal NyxCompiler.WrittenReferences

open Parser.Program

/// The references of `typed` as its source writes them: a reference to the def that runs a scope def's body in its
/// caller's scope is to that scope def, whose name is declared where the made-up def's empty declaration is.
let ofModule (typed: TypedModule) : Map<NodeId, SymbolId> =
    let madeUp =
        typed.Symbols
        |> Map.filter (fun _ symbol ->
            match Names.ownPart symbol.Name with
            | TaskScopeSyntax.InCallersScope _ -> symbol.Kind = TopLevelValue && symbol.Declaration.End = symbol.Declaration.Start
            | _ -> false)
    if madeUp.IsEmpty then typed.References
    else
        let written =
            typed.Symbols
            |> Map.values
            |> Seq.filter (fun symbol ->
                symbol.Kind = TopLevelValue && symbol.Declaration.End > symbol.Declaration.Start && not (SourceSpan.isNone symbol.Declaration))
            |> Seq.map (fun symbol -> (symbol.Declaration.File, symbol.Declaration.Start), symbol.Id)
            |> Map.ofSeq
        let scopeDefOf =
            madeUp
            |> Map.toSeq
            |> Seq.choose (fun (id, symbol) ->
                written |> Map.tryFind (symbol.Declaration.File, symbol.Declaration.Start) |> Option.map (fun scopeDef -> id, scopeDef))
            |> Map.ofSeq
        typed.References |> Map.map (fun _ id -> scopeDefOf |> Map.tryFind id |> Option.defaultValue id)
