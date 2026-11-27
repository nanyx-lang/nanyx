namespace NyxCompiler

open Parser.Program

/// The checks on a module's tests, the defs marked `@test` (see `Attributes.testAttribute`): what a test is, what code
/// may refer to one, and which contexts it may require.
module TestChecks =
    let private hostContexts = Set.ofList Hosts.testContexts

    let private ioContexts = Set.ofList Hosts.ioTestContexts

    /// The name a context of a def's requirements is written by: `Console`, `Async`.
    let internal contextName (context: Ty) =
        match context with
        | TyNominal({ Name = name }, _, _, _)
        | TyAlias(name, _, _)
        | TyApply(name, _) -> Some name
        | _ -> None

    /// Whether `ty`, a test's, is a test's: `()`, the value of a block, or a function of no arguments that gives `()`, or
    /// never gives a value, as one that crashes doesn't. A type that holds an error is left to that error.
    let private isTestType (ty: Ty) =
        let isResult (ty: Ty) = ty = TyPrimitive "unit" || ty = Unifier.bottomType
        Unifier.holdsError ty
        || match Unifier.unalias ty with
           | TyPrimitive "unit" -> true
           | TyFunc(input, output, _) -> Unifier.unalias input = TyPrimitive "unit" && isResult (Unifier.unalias output)
           | _ -> false

    /// The error for `test` requiring `context`, which the test host doesn't provide it (`NYX0312`): one that
    /// `@test(#io)` provides comes with that advice, when every context the test lacks is one, and a fix that marks the
    /// test so, after its attribute.
    let private contextNotProvided (test: Attributes.MarkedTest) (missing: string list) (context: string) =
        let ioWouldDo = not test.Io && missing |> List.forall ioContexts.Contains
        let advice = if ioWouldDo then $", or mark the test `@test(#{Attributes.ioTestArgument})` to use the real one" else ""
        let diagnostic =
            Diagnostics.at DiagnosticCodes.TestContextNotProvided test.Span
                $"The test '{test.Name}' requires the context '{context}', which the test host does not provide: load an instance of it, such as a fake, with `use` in the test{advice}"
            |> Diagnostics.about test.Name
        if ioWouldDo then
            let after = { test.Attribute with Start = test.Attribute.End; StartColumn = test.Attribute.StartColumn + (test.Attribute.End - test.Attribute.Start) }
            { diagnostic with
                Fixes = [ { Title = $"Mark the test `@test(#{Attributes.ioTestArgument})`"; Edits = [ { Span = after; NewText = $"(#{Attributes.ioTestArgument})" } ] } ] }
        else
            diagnostic

    /// The errors about the module's tests, `tests`: a test whose type `typeOf` gives is not a test's (`NYX0310`); one
    /// that requires a context, as `contextsOf` gives them, that the test host doesn't provide it, which for one marked
    /// `@test(#io)` are `Hosts.ioTestContexts` (`NYX0312`); and each reference to a test, by the module's `references` to
    /// its `symbols`, from code that isn't a test, among the module's `items` (`NYX0311`). A test may refer to another.
    let errors
        (tests: Attributes.MarkedTest list)
        (typeOf: string -> Ty option)
        (contextsOf: string -> Ty list)
        (references: Map<NodeId, SymbolId>)
        (symbols: Map<SymbolId, Symbol>)
        (items: TopLevelItem list)
        : Diagnostic list =
        let testNames = tests |> List.map _.Name |> Set.ofList
        let shapeErrors =
            tests
            |> List.choose (fun { Name = name; Span = span } ->
                match typeOf name with
                | Some ty when not (isTestType ty) ->
                    Some (
                        Diagnostics.at DiagnosticCodes.NotATest span
                            $"A test takes no arguments and gives back `()`, as `{{ assert(...) }}` does, but '{name}' has the type {Unifier.tyToDisplayString Ascii ty}"
                        |> Diagnostics.about name)
                | _ -> None)
        let contextErrors =
            tests
            |> List.collect (fun test ->
                let provided = if test.Io then ioContexts else hostContexts
                let missing = contextsOf test.Name |> List.choose contextName |> List.distinct |> List.filter (provided.Contains >> not)
                missing |> List.map (contextNotProvided test missing))
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
