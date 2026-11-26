namespace NyxCompiler

open Parser.Program

/// The checks on a module's tests, the defs marked `@test` (see `Attributes.testAttribute`): what a test is, what code
/// may refer to one, and which contexts it may require.
module TestChecks =
    let private hostContexts = Set.ofList Hosts.testContexts

    /// The name a context of a def's requirements is written by: `Console`, `Async`.
    let private contextName (context: Ty) =
        match context with
        | TyNominal({ Name = name }, _, _, _)
        | TyAlias(name, _, _)
        | TyApply(name, _) -> Some name
        | _ -> None

    /// Whether `ty`, a test's, is a test's: `()`, the value of a block, or a function of no arguments that gives `()`.
    let private isTestType (ty: Ty) =
        match Unifier.unalias ty with
        | TyPrimitive "unit" -> true
        | TyFunc(input, output, _) ->
            match Unifier.unalias input, Unifier.unalias output with
            | TyPrimitive "unit", TyPrimitive "unit" -> true
            | _ -> false
        | _ -> false

    /// The errors about the module's tests, `tests`, each by name with where it is written: a test whose type `typeOf`
    /// gives is not a test's (`NYX0310`); one that requires a context, as `contextsOf` gives them, that the test host
    /// doesn't provide (`NYX0312`); and each reference to a test, by the module's `references` to its `symbols`, from
    /// code that isn't a test, among the module's `items` (`NYX0311`). A test may refer to another.
    let errors
        (tests: (string * SourceSpan) list)
        (typeOf: string -> Ty option)
        (contextsOf: string -> Ty list)
        (references: Map<NodeId, SymbolId>)
        (symbols: Map<SymbolId, Symbol>)
        (items: TopLevelItem list)
        : Diagnostic list =
        let testNames = tests |> List.map fst |> Set.ofList
        let shapeErrors =
            tests
            |> List.choose (fun (name, span) ->
                match typeOf name with
                | Some ty when not (isTestType ty) ->
                    Some (
                        Diagnostics.at DiagnosticCodes.NotATest span
                            $"A test takes no arguments and gives back `()`, as `{{ assert(...) }}` does, but '{name}' has the type {Unifier.tyToDisplayString Ascii ty}"
                        |> Diagnostics.about name)
                | _ -> None)
        let contextErrors =
            tests
            |> List.collect (fun (name, span) ->
                contextsOf name
                |> List.choose contextName
                |> List.distinct
                |> List.filter (hostContexts.Contains >> not)
                |> List.map (fun context ->
                    Diagnostics.at DiagnosticCodes.TestContextNotProvided span
                        $"The test '{name}' requires the context '{context}', which the test host does not provide: load an instance of it, such as a fake, with `use` in the test"
                    |> Diagnostics.about name))
        // The code that isn't a test: the top-level expressions, and the defs not marked `@test`, a block's among them,
        // but the dispatcher a test build gives the module to run them (see `Attributes.testDispatcher`).
        let rec notTests (items: TopLevelItem list) =
            items
            |> List.collect (fun item ->
                match item.Kind with
                | Def (ValueDef(_, name, _, _)) when testNames.Contains name || name = Attributes.testDispatcher -> []
                | Def _
                | Expr _ -> [ item ]
                | ModuleBlock(_, inner) -> notTests inner
                | _ -> [])
        let isTest (symbol: SymbolId) =
            match symbols |> Map.tryFind symbol with
            | Some symbol -> symbol.Kind = TopLevelValue && testNames.Contains symbol.Name
            | None -> false
        let referenceErrors =
            notTests items
            |> List.collect SyntaxTree.ofItem
            |> List.collect SyntaxTree.expressionsWithin
            |> List.choose (fun expr ->
                match references |> Map.tryFind expr.Id with
                | Some symbol when isTest symbol ->
                    let name = symbols.[symbol].Name
                    Some (
                        Diagnostics.at DiagnosticCodes.ReferenceToTest expr.Span
                            $"'{name}' is a test, which only a test build has, so only another test can refer to it"
                        |> Diagnostics.about name)
                | _ -> None)
        shapeErrors @ contextErrors @ referenceErrors
