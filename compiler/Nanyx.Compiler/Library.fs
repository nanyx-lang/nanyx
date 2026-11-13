namespace NyxCompiler

open System
open System.IO
open System.Text.RegularExpressions
open Parser.Program
open NyxCompiler.ExpressionRewrites

module Compiler =
    let private normalizeSource (source: string) =
        source.Replace("\r\n", "\n").Replace("\r", "\n")

    /// The diagnostic for a failed parse. FParsec's report starts by giving the file and position ("Error in path:
    /// Ln: 1 Col: 5"), which the diagnostic's span already gives. FParsec wraps that line when the path is long, so
    /// it is cut off through the position, not at the first line break.
    let syntaxError (error: ParseError) =
        let column = error.Message.IndexOf(" Col: ", System.StringComparison.Ordinal)
        let lineEnd = if column < 0 then -1 else error.Message.IndexOf('\n', column)
        let message =
            if error.Message.StartsWith "Error in " && lineEnd >= 0 then error.Message.Substring(lineEnd + 1)
            else error.Message
        Diagnostics.at DiagnosticCodes.SyntaxError error.Span message
    let private parseWithDiagnostics (filePath: string) (source: string) : Result<Module * DeclaredNames * (ModuleName * SourceSpan) list, Diagnostic list> =
        match parseModuleFileWithNames filePath source with
        | Result.Ok parsed -> Ok parsed
        | Result.Error err -> Error [ syntaxError err ]

    /// Applies the substitution found by unification to everything inference produced.
    /// A definition's type is reported with the rest variables that only stand for tags it gives out closed (see
    /// `Unifier.outputOnlyRestVars`): a union widened by a call, as a `proc` block's is by each `try`, is left
    /// open for more tags, but no more can come once the definition is done. A rest a context the definition requires
    /// is applied to is left open (`contexts` has each one's contexts): an instance can add tags to it, as
    /// `FromString(int, #invalidInt)` does to the `e` of `[FromString(a, e)] string -> #some(a) | e`.
    let private resolveTypes (subst: Subst) (types: Map<string, Ty>) (contexts: Map<string, TypeExpr list>) (typeDefs: Map<string, TypeDefInfo>) (items: TypedTopLevelItem list) =
        let closeOutputRests name ty =
            let contextVars = contexts |> Map.tryFind name |> Option.defaultValue [] |> List.collect NyxCompiler.InferModule.annotationVarNames |> Set.ofList
            let closed =
                Unifier.outputOnlyRestVars ty
                |> List.filter (fun v -> not (v.Name |> Option.exists contextVars.Contains))
                |> List.map (fun v -> v.Id, TyUnion [])
                |> Map.ofList
            if closed.IsEmpty then ty else Unifier.apply closed ty
        let resolvedTypes = types |> Map.map (fun name ty -> Unifier.apply subst ty |> closeOutputRests name)
        let resolvedTypeDefs =
            typeDefs
            |> Map.map (fun _ info ->
                // A definition's parameters stand for the types it is applied to, whatever the module found for a
                // variable of the same id: an imported definition's are numbered by the module it came from.
                let ownSubst = info.Parameters |> List.fold (fun acc parameter -> acc |> Map.remove parameter.Id) subst
                { info with Underlying = Unifier.apply ownSubst info.Underlying })
        let rec applyTypedExpr (typedExpr: TypedExpr) =
            { typedExpr with
                Type = Unifier.apply subst typedExpr.Type
                Body = typedExpr.Body |> Option.map applyTypedExpr
                Statements = typedExpr.Statements |> Option.map (List.map applyTypedStatement)
                MatchArms = typedExpr.MatchArms |> Option.map (List.map applyTypedMatchArm)
                Arguments = typedExpr.Arguments |> Option.map (List.map applyTypedExpr) }
        and applyTypedStatement statement =
            match statement with
            | TypedDefStatement(isExport, name, typeOpt, expr) ->
                TypedDefStatement(isExport, name, typeOpt, applyTypedExpr expr)
            | TypedExprStatement expr -> TypedExprStatement (applyTypedExpr expr)
            | TypedUseStatement expr -> TypedUseStatement (applyTypedExpr expr)
            | TypedImportStatement _
            | TypedTypeDefStatement _ -> statement
        and applyTypedMatchArm (patterns, expr) =
            let mappedPatterns =
                patterns |> List.map (fun pattern -> { pattern with Type = Unifier.apply subst pattern.Type })
            (mappedPatterns, applyTypedExpr expr)
        let resolvedItems =
            items
            |> List.map (fun item ->
                match item with
                | TypedExprItem expr -> TypedExprItem (applyTypedExpr expr)
                | TypedDef (TypedValueDef(isExport, name, typeOpt, expr)) ->
                    TypedDef (TypedValueDef(isExport, name, typeOpt, applyTypedExpr expr))
                | TypedDef (TypedTypeDef _)
                | TypedImport _
                | TypedModuleDecl _ -> item)
        resolvedTypes, resolvedTypeDefs, resolvedItems

    /// `expr` with each builder block that uses no workflow keyword (`blocks`, by the node of the call it is written
    /// as) replaced by its block, which runs where it is written.
    let private inlineWorkflowBlocks (blocks: Set<NodeId>) (expr: Expression) : Expression =
        if blocks.IsEmpty then
            expr
        else
            expr
            |> mapExpressionKinds (fun current kind ->
                match kind with
                | FunctionCall(_, _, [ Kind (Lambda([], body, _)) ]) when blocks.Contains current.Id -> body.Kind
                | other -> other)

    /// The module as the backend will see it: the typed items turned back into syntax, with each pipe to an
    /// attached function, and each hole that calls a derived definition, written with the name it resolved to (`pipeTargets`), each operator that calls a definition as
    /// that call (`operatorCalls`, through the instance `operatorInstances` gives where it has one), each expression inference read as another as that one (`readAs`),
    /// each read of a built-in property as a call (`properties`), each builder block that uses no keyword as its block
    /// (`workflowBlocks`), and without import statements. `defer` compiles the module's errors to traps first, for a
    /// debug build.
    let private moduleFromTypedItems (defer: Expression -> Expression) (pipeTargets: Map<NodeId, Identifier>) (operatorCalls: Map<NodeId, Identifier>) (operatorInstances: Map<NodeId, TypeExpr>) (readAs: Map<NodeId, Expression>) (properties: Map<NodeId, BuiltInProperty>) (workflowBlocks: Set<NodeId>) (items: TypedTopLevelItem list) : Module =
        let lower (expr: Expression) =
            expr |> defer |> retargetPipes pipeTargets |> callOperators operatorCalls operatorInstances |> writeAsRead readAs |> callBuiltInProperties properties |> inlineWorkflowBlocks workflowBlocks
        items
        |> List.choose (function
            | TypedModuleDecl name -> Some (ModuleDecl name)
            | TypedImport _ -> None
            | TypedDef (TypedValueDef(isExport, name, typeOpt, expr)) ->
                Some (Def (ValueDef(isExport, name, typeOpt, lower expr.Expr)))
            | TypedDef (TypedTypeDef(name, modifiers, parameters, body)) ->
                Some (Def (TypeDef(name, modifiers, parameters, body)))
            | TypedExprItem expr -> Some (Expr (lower expr.Expr)))
        |> List.map TopLevelItem.synthetic

    let internal originKey (origin: ModuleOrigin) =
        match origin with
        | SourceFile fullPath -> fullPath
        | StandardLibraryModule name -> StandardLibrary.importPrefix + name
        | InMemory -> "<source>"

    /// The file a module's syntax nodes are reported in.
    let private sourceFileName (origin: ModuleOrigin) =
        match origin with
        | SourceFile fullPath -> fullPath
        | StandardLibraryModule name -> StandardLibrary.importPrefix + name + ".nyx"
        | InMemory -> ""

    /// The modules one compilation has checked, by `originKey`, so that a module imported along several paths is
    /// checked once and every importer sees the same symbols; with the package whose files' imports are resolved
    /// through its module table, if the compilation is of a package's file.
    type internal CheckedModules(package: PackageModules option) =
        inherit System.Collections.Generic.Dictionary<string, Result<CheckedModule, CompilationPhase * Diagnostic list>>()
        member _.Package = package

    /// The standard-library modules this process has checked, by `originKey` and source, each with the modules it
    /// imports, which a compilation that imports it needs too (`compileFrom` resolves inlined definitions' types by
    /// them). A user's own modules are checked again in each compilation. A checked module is never changed, and
    /// sharing one is safe: its type variables are numbered by the module itself, as they would be if it were checked
    /// again, and its syntax nodes and symbols keep ids no other module has.
    let private checkedStandardLibrary =
        System.Collections.Concurrent.ConcurrentDictionary<string * string, Lazy<Result<CheckedModule, CompilationPhase * Diagnostic list> * (string * Result<CheckedModule, CompilationPhase * Diagnostic list>) list>>()

    /// Parses, desugars and type-checks one module, resolving and checking the modules it imports first. An
    /// error says which phase it came from.
    let rec internal typecheckModule (checkedModules: CheckedModules) (origin: ModuleOrigin) (source: string) (visited: Set<string>) : Result<CheckedModule, CompilationPhase * Diagnostic list> =
        let key = originKey origin
        let resolved =
            parseWithDiagnostics (sourceFileName origin) source
            // A module split across files is checked as one: its other files' items join this one's.
            |> Result.bind (fun parsed ->
                PartialModules.assemble parseWithDiagnostics (PartialModules.otherFragments checkedModules.Package origin) parsed
                |> Result.map (fun assembled -> (let ast, _, _ = parsed in ast), assembled))
            |> Result.mapError (fun diagnostics -> Parsed, diagnostics)
            |> Result.bind (fun (ast, (items, declared, moduleBlocks)) ->
                match WorkflowDesugaring.desugarWithDiagnostics items with
                | _, (_ :: _ as errors), _, _ -> Error (Desugared, errors)
                | desugaredAst, [], memberPlaces, lowered ->
                    Attributes.applyAttributes desugaredAst
                    |> Result.map (fun (desugared, entryCalls) ->
                        let desugared, nameErrors = BuiltinTypeNames.replace declared desugared
                        let lowered = entryCalls |> Map.fold (fun acc attribute calls -> Map.add attribute calls acc) lowered
                        ast, declared, moduleBlocks, desugared, memberPlaces, lowered, nameErrors)
                    |> Result.mapError (fun diagnostics -> Desugared, diagnostics))
        match resolved with
        | Result.Error failure -> Error failure
        | Result.Ok (ast, declared, moduleBlocks, desugared, memberPlaces, lowered, nameErrors) ->
            let preludeKey = originKey (StandardLibraryModule StandardLibrary.preludeModuleName)
            // Every module but the prelude imports it first, so that anything it imports or defines itself wins.
            // An import of a `module name =` block of this file is not of another file: inference resolves it.
            let blockImported = NyxCompiler.Infer.blockImports moduleBlocks desugared |> List.map _.Import
            let fileImports = SyntaxTree.collectImports desugared |> List.filter (fun item -> not (List.contains item blockImported))
            let imports =
                if key = preludeKey then
                    fileImports
                else
                    ImportItem.synthetic (ModuleImport (StandardLibrary.importPrefix + StandardLibrary.preludeModuleName)) None
                    :: fileImports
            let mutable env =
                match origin with
                | StandardLibraryModule _ -> Intrinsics.typeEnv
                | SourceFile _
                | InMemory -> TypeEnv.empty
            let mutable state =
                { NyxCompiler.Infer.emptyState with
                    Declared = declared
                    ModuleBlocks = moduleBlocks
                    WorkflowMemberPlaces = memberPlaces
                    Home = origin
                    StandardLibraryExports = ImportFixes.standardLibraryExportPaths }
            let mutable errors: Diagnostic list = []
            let mutable inlined: TopLevelItem list = []
            // Kept apart and inlined last, so that where an explicit import defines the same name, its definition is kept.
            let mutable preludeInlined: TopLevelItem list = []
            let mutable preludeTypeNames: Set<string> = Set.empty
            let mutable inlinedTypes: Map<string, Ty> = Map.empty
            // Where the calls in the imported modules' definitions, which are inlined, find their instances.
            let mutable inlinedInstances: Map<NodeId, ContextInstance option list> = Map.empty
            // The instances the `use`s in the imported modules' definitions load.
            let mutable inlinedUseContexts: Map<NodeId, TypeExpr> = Map.empty
            // The types of the defs the imports bring in, with the contexts they require (see `ImportedContextTypes`).
            let mutable importedContextTypes: Map<SymbolId, Ty * Ty list> = Map.empty
            // The modules that bring each name in unqualified, by the module's key, with the name of the module.
            // The prelude is not one of them: a name it shares with an import is the import's, not ambiguous.
            let mutable unqualifiedSources: Map<string, (string * string) list> = Map.empty
            let mutable importedFiles: Map<SymbolId, ImportedFile> = Map.empty
            // The module's re-exports, and each as it is merged, with the module it re-exports from.
            let reExportItems = ModuleLinking.reExportsOf desugared
            let mutable reExports: ReExported list = []
            // Merges the import `item` of `imported`, and gives back the import's symbol, if it has one. A made-up import
            // has none; what it brings in is used through `through`, if given, as a re-export's names are used through
            // the import of the module that re-exports them.
            let mergeImport (importedOrigin: ModuleOrigin) (item: ImportItem) (through: SymbolId option) (imported: CheckedModule) : SymbolId option =
                let importedKey = originKey importedOrigin
                let qualifier = ModuleLinking.importQualifier item
                let exportedValues = ModuleLinking.exportedValueNames imported.Own
                // An import brings in every export, or only those it selects.
                let selected = item.Selection |> Option.map (ModuleLinking.selectedExports imported)
                let isSelected (name: string) = selected |> Option.forall (fun selected -> selected.Contains name)
                if qualifier.IsNone && importedKey <> preludeKey then
                    let moduleName =
                        match imported.Name, item.Source with
                        | Some name, _ -> name
                        | None, ModuleImport name -> name
                        | None, PathImport path -> path
                    for name in exportedValues |> Set.filter isSelected do
                        let sources = unqualifiedSources |> Map.tryFind name |> Option.defaultValue []
                        if not (sources |> List.exists (fun (key, _) -> key = importedKey)) then
                            unqualifiedSources <- unqualifiedSources |> Map.add name (sources @ [ importedKey, moduleName ])
                let exportedTypes = ModuleLinking.exportedTypeNames imported.Own
                let exportedValueTypes =
                    imported.Typed.Types |> Map.filter (fun name _ -> exportedValues.Contains name)
                let exportedTypeDefs =
                    imported.Typed.TypeDefs |> Map.filter (fun name _ -> exportedTypes.Contains name)
                // The exported definitions, declared in the imported module's file.
                let exportedSymbols =
                    imported.Exports
                    |> Map.filter (fun name _ -> exportedValues.Contains name)
                    |> Map.values
                    |> Seq.map (fun symbol -> symbol.Id, symbol)
                    |> Map.ofSeq
                // The exported types and their contexts' members, declared in the imported module's file too.
                let exportedTypeSymbols =
                    exportedTypeDefs
                    |> Map.values
                    |> Seq.collect (fun info -> Option.toList info.Symbol @ (info.Members |> Map.values |> List.ofSeq))
                    |> Seq.choose (fun id -> imported.Typed.Symbols |> Map.tryFind id)
                    |> Seq.map (fun symbol -> symbol.Id, symbol)
                    |> Map.ofSeq
                env <- ModuleLinking.extendEnvWithImport env qualifier (exportedValueTypes |> Map.filter (fun name _ -> isSelected name)) (imported.Exports |> Map.map (fun _ symbol -> symbol.Id))
                // The import is a symbol of its own, unless the compiler made it up (for `@entry`).
                let importedThroughSymbol (importId: SymbolId) =
                    Seq.append exportedSymbols exportedTypeSymbols
                    |> Seq.fold (fun acc (KeyValue(id, _)) ->
                        acc |> Map.add id ((acc |> Map.tryFind id |> Option.defaultValue []) @ [ importId ])) state.ImportedThrough
                let importSymbols, importedThrough =
                    if SourceSpan.isNone item.Span then
                        Map.empty, (through |> Option.map importedThroughSymbol |> Option.defaultValue state.ImportedThrough)
                    else
                        let name =
                            match item.Alias, item.Source with
                            | Some alias, _ -> alias
                            | None, ModuleImport moduleName -> moduleName
                            | None, PathImport path -> path
                        let import = { Id = SymbolId.next (); Name = name; Kind = ImportedModule; Declaration = item.Span; IsExported = false }
                        importedFiles <- importedFiles |> Map.add import.Id { File = sourceFileName importedOrigin; Name = imported.Name }
                        // Only an alias is written as a name: an import without one has no name to write.
                        if item.Alias.IsSome then
                            state <- { state with Scopes = state.Scopes |> Map.add import.Id (Scope.wholeModule item.Span.File) }
                        Map.ofList [ import.Id, import ], importedThroughSymbol import.Id
                // A type an explicit import exports replaces the prelude's type of the same name.
                let typeDefs =
                    if importedKey = preludeKey then
                        preludeTypeNames <- Set.ofSeq exportedTypeDefs.Keys
                        state.TypeDefs
                    else
                        state.TypeDefs |> Map.filter (fun name _ -> not (preludeTypeNames.Contains name && exportedTypeDefs.ContainsKey name))
                // An aliased import's types are written with the alias (`m.$Email`). Their own names stay known for
                // the signatures that name them, but cannot be written unless something else brings them in. An
                // unaliased import's types can, and replace a type only an aliased import brought in. One that selects
                // names brings in only the types it selects; the rest are as an aliased import's.
                let typeDefs, hiddenTypeNames =
                    match qualifier with
                    | Some qualifier ->
                        let qualified =
                            exportedTypeDefs
                            |> Map.filter (fun name _ -> isSelected name)
                            |> Map.fold (fun acc name info -> acc |> Map.add $"{qualifier}.{name}" info) typeDefs
                        let hidden = exportedTypeDefs.Keys |> Seq.filter (fun name -> not (typeDefs.ContainsKey name)) |> Set.ofSeq
                        qualified, Set.union state.HiddenTypeNames hidden
                    | None ->
                        let visible, unselected = exportedTypeDefs |> Map.partition (fun name _ -> isSelected name)
                        let hidden = unselected.Keys |> Seq.filter (fun name -> not (typeDefs.ContainsKey name)) |> Set.ofSeq
                        typeDefs |> Map.filter (fun name _ -> not (state.HiddenTypeNames.Contains name && visible.ContainsKey name)),
                        Set.union (state.HiddenTypeNames - Set.ofSeq visible.Keys) hidden
                // A function that requires a context requires it wherever it is called, under the name it is imported as.
                let contextRequirements =
                    imported.ContextRequirements
                    |> Map.filter (fun name _ -> isSelected name)
                    |> Map.fold (fun acc name contexts ->
                        let importedName =
                            match qualifier with
                            | Some qualifier -> $"{qualifier}.{name}"
                            | None -> name
                        acc |> Map.add importedName contexts) state.ContextRequirements
                let contextSignatures =
                    imported.ContextSignatures
                    |> Map.filter (fun name _ -> isSelected name)
                    |> Map.fold (fun acc name signature ->
                        let importedName =
                            match qualifier with
                            | Some qualifier -> $"{qualifier}.{name}"
                            | None -> name
                        acc |> Map.add importedName signature) state.ContextSignatures
                let typeDefs = ModuleLinking.mergeTypeDefs typeDefs exportedTypeDefs
                // The contexts the imported functions require that this module cannot name are carried in with their
                // definitions, hidden, so a call to one is checked as a call needing the context is here: an ambient
                // one, and a parameterised one, whose instance each call finds (an attached `Point.show` for a
                // `Show(Point)`, say).
                let carried, carriedParameterised =
                    let rec contextNames (ctx: TypeExpr) =
                        match ctx.Kind with
                        | TypeContext contexts -> contexts |> List.collect contextNames
                        | TypeName name
                        | TypeApply(name, _) -> [ name ]
                        | _ -> []
                    let unnamed =
                        imported.ContextRequirements
                        |> Map.values
                        |> Seq.collect (List.collect contextNames)
                        |> Seq.distinct
                        |> Seq.filter (fun name -> not (typeDefs.ContainsKey name) && not (state.ContextDefs.ContainsKey name))
                        |> List.ofSeq
                    unnamed |> List.choose (fun name -> imported.AmbientContexts |> Map.tryFind name |> Option.map (fun info -> name, info)),
                    unnamed
                    |> List.choose (fun name ->
                        imported.Typed.TypeDefs
                        |> Map.tryFind name
                        |> Option.filter (fun info -> info.IsContext && not info.Parameters.IsEmpty && not (imported.AmbientContexts.ContainsKey name))
                        |> Option.map (fun info -> name, info))
                state <-
                    { state with
                        TypeDefs = carried @ carriedParameterised |> List.fold (fun acc (name, info) -> acc |> Map.add name info) typeDefs
                        HiddenTypeNames = carried @ carriedParameterised |> List.fold (fun acc (name, _) -> acc |> Set.add name) hiddenTypeNames
                        CarriedAmbientContexts = carried |> List.fold (fun acc (name, _) -> acc |> Set.add name) state.CarriedAmbientContexts
                        Symbols = Seq.fold (fun acc (KeyValue(id, symbol)) -> acc |> Map.add id symbol) state.Symbols (Seq.concat [ exportedSymbols; exportedTypeSymbols; importSymbols ])
                        ImportedThrough = importedThrough
                        // The prelude's contexts are the ones operators are read through (see `OperatorContexts`),
                        // but for one whose name an import after it gives to a type of its own.
                        PreludeContexts =
                            if importedKey = preludeKey then
                                exportedTypeDefs |> Map.filter (fun _ info -> info.IsContext) |> Map.keys |> Set.ofSeq
                            elif qualifier.IsNone then state.PreludeContexts - (exportedTypeDefs.Keys |> Seq.filter isSelected |> Set.ofSeq)
                            else state.PreludeContexts
                        // Where each definition is imported, since a `module name =` block's imports are its own.
                        ImportSites =
                            Seq.append exportedSymbols exportedTypeSymbols
                            |> Seq.fold (fun acc (KeyValue(id, _)) ->
                                acc |> Map.add id (item.Span :: (acc |> Map.tryFind id |> Option.defaultValue []))) state.ImportSites
                        ContextRequirements = contextRequirements
                        ContextSignatures = contextSignatures
                        // A call to an imported function checks what it compares the rests of its unions with as a
                        // call to one of the module's own does.
                        CheckedRests =
                            imported.CheckedRests
                            |> Map.filter (fun symbol _ -> exportedSymbols.ContainsKey symbol)
                            |> Map.fold (fun acc symbol checkedRests -> acc |> Map.add symbol checkedRests) state.CheckedRests }
                if importedKey = preludeKey then
                    preludeInlined <- ModuleLinking.inlinedItemsForImport item imported
                else
                    inlined <- inlined @ ModuleLinking.inlinedItemsForImport item imported
                inlinedTypes <- ModuleLinking.inlinedTypesForImport item imported |> Map.fold (fun acc name ty -> acc |> Map.add name ty) inlinedTypes
                inlinedInstances <- imported.Typed.ContextInstances |> Map.fold (fun acc id found -> acc |> Map.add id found) inlinedInstances
                inlinedUseContexts <- imported.Typed.UseContexts |> Map.fold (fun acc id context -> acc |> Map.add id context) inlinedUseContexts
                importedContextTypes <-
                    RequiredContexts.ofImport imported.Subst imported.Typed.ContextTypes imported.RawSymbolTypes exportedSymbols.Keys
                    |> Map.fold (fun acc id found -> acc |> Map.add id found) importedContextTypes
                importSymbols |> Map.keys |> Seq.tryHead
            // The import `item`, then what `imported` re-exports, as made-up imports of the modules it re-exports from,
            // under the same alias and limited to what `item` selects. Each is used through the import `item` is.
            let rec mergeWithReExports (importedOrigin: ModuleOrigin) (item: ImportItem) (through: SymbolId option) (imported: CheckedModule) =
                let through = mergeImport importedOrigin item through imported |> Option.orElse through
                for reExported in imported.ReExports do
                    let names =
                        reExported.Item.Selection
                        |> Option.defaultValue []
                        |> List.filter (fun (name, _) -> item.Selection |> Option.forall (List.exists (fun (selected, _) -> selected = name)))
                    if not names.IsEmpty then
                        let reImport = { Source = reExported.Item.Source; Alias = item.Alias; Selection = Some names; Span = SourceSpan.none }
                        mergeWithReExports reExported.Origin reImport through reExported.Module |> ignore
                through
            // `as m & (a, b)` is an import under `m` and an import of `a` and `b` alone. The second is made up, so it
            // is not an import symbol of its own. A name the module does not export is an error. A re-export is an
            // import that selects the names it exports again, which the module's importers are given.
            let mergeWritten (importedOrigin: ModuleOrigin) (item: ImportItem) (imported: CheckedModule) =
                match ModuleLinking.unexportedSelections item imported, item.Alias, item.Selection with
                | _ :: _ as unexported, _, _ -> errors <- errors @ unexported
                | [], Some _, Some _ ->
                    let through = mergeWithReExports importedOrigin { item with Selection = None } None imported
                    mergeWithReExports importedOrigin { item with Alias = None; Span = SourceSpan.none } through imported |> ignore
                | [], _, _ ->
                    mergeWithReExports importedOrigin item None imported |> ignore
                    if reExportItems |> List.contains item then
                        reExports <- reExports @ [ { Origin = importedOrigin; Item = item; Module = imported } ]
            for item in imports do
                // An import of a module that is still being checked, because it imports this one, is a cycle.
                let checkImport (importOrigin: ModuleOrigin) (readSource: unit -> string) =
                    let importKey = originKey importOrigin
                    if importKey = key || visited.Contains importKey then
                        Error (Typed, [ Diagnostics.at DiagnosticCodes.ImportCycle item.Span $"Import cycle detected for {importKey}" ])
                    else
                        match typecheckImport checkedModules importOrigin readSource (visited.Add key) with
                        // A module with errors fails each importer with them.
                        | Ok imported when not imported.Errors.IsEmpty -> Error (Typed, imported.Errors)
                        | checkedImport -> checkedImport
                match StandardLibrary.tryModuleName item with
                | Some moduleName ->
                    match StandardLibrary.tryLoadSource moduleName with
                    | None ->
                        let available = String.concat ", " (StandardLibrary.moduleNames |> List.map (fun name -> StandardLibrary.importPrefix + name))
                        errors <- errors @ [ Diagnostics.at DiagnosticCodes.UnknownStandardLibraryModule item.Span $"Unknown standard library module '{StandardLibrary.importPrefix}{moduleName}' (available: {available})" ]
                    | Some moduleSource ->
                        match checkImport (StandardLibraryModule moduleName) (fun () -> moduleSource) with
                        | Error (_, diags) -> errors <- errors @ diags
                        | Ok imported when imported.Name <> Some moduleName ->
                            let declared = imported.Name |> Option.defaultValue "(none)"
                            errors <- errors @ [ Diagnostics.at DiagnosticCodes.InvalidStandardLibraryModule item.Span $"Standard library module '{StandardLibrary.importPrefix}{moduleName}' declares the module name '{declared}'" ]
                        | Ok imported -> mergeWritten (StandardLibraryModule moduleName) item imported
                | None ->
                    match origin with
                    | SourceFile fullPath ->
                        match FileImports.missingAlias item, PackageModules.resolveImport checkedModules.Package fullPath item with
                        | Some diag, _
                        | None, Error diag -> errors <- errors @ [ diag ]
                        | None, Ok importPath ->
                            match checkImport (SourceFile importPath) (fun () -> File.ReadAllText importPath) with
                            | Error (_, diags) -> errors <- errors @ diags
                            | Ok imported ->
                                match FileImports.importedFile item imported.Name imported.Syntax with
                                | [] -> mergeWritten (SourceFile importPath) item imported
                                | diags -> errors <- errors @ diags
                    | StandardLibraryModule name ->
                        errors <- errors @ [ Diagnostics.at DiagnosticCodes.InvalidStandardLibraryModule item.Span $"Standard library module '{name}' may only import other standard library modules" ]
                    | InMemory ->
                        let shown =
                            match item.Source with
                            | PathImport path -> path
                            | ModuleImport name -> name
                        errors <- errors @ [ Diagnostics.at DiagnosticCodes.ImportNotFound item.Span $"Import not found: {shown} (only standard library modules can be imported here)" ]
            // A name two imports bring in is an error only where it is used, and only if nothing nearer binds it.
            let ambiguousImports =
                unqualifiedSources
                |> Map.toSeq
                |> Seq.choose (fun (name, sources) ->
                    match sources, TypeEnv.tryFind name env |> Option.bind (fun scheme -> scheme.Symbol) with
                    | _ :: _ :: _, Some symbol -> Some (name, (symbol, sources |> List.map (fun (_, moduleName) -> $"{moduleName}.{name}")))
                    | _ -> None)
                |> Map.ofSeq
            state <-
                { state with
                    AmbiguousImports = ambiguousImports
                    PreludeContexts = OperatorContexts.inModule (key = preludeKey) desugared state.PreludeContexts
                    NamedAsValues = SyntaxTree.namedAsValues desugared }
            if not errors.IsEmpty then
                // A module imported along several paths fails each importer with the same diagnostics.
                Error (Typed, List.distinct errors)
            else
                let types, items, (state: NyxCompiler.Infer.InferState), inferErrors = NyxCompiler.InferModule.inferModuleWithEnv env state desugared
                let subst, mismatches = Unifier.solveDetailed state.Constraints |> fun (subst, mismatches) -> Unifier.keepWrittenNames types.Values subst, mismatches
                // Every error in the module is reported: the items that failed to check, and each conflict among the
                // constraints of those that did, in the order they are in the source rather than the order solving
                // found them. The module is typed all the same, with what did check.
                let mismatchErrors =
                    mismatches
                    |> List.sortBy (fun (_, span, _) -> span.Start)
                    |> List.map (fun (message, span, kind) ->
                        let code =
                            match kind with
                            | ContextsNotAllowed -> DiagnosticCodes.ContextsNotAllowed
                            | TypesDiffer -> MemoryCells.mismatchCode state span
                        Diagnostics.at code span message)
                let moduleErrors = nameErrors @ inferErrors @ mismatchErrors @ (if key = preludeKey then OperatorContexts.ambiguityErrors state else [])
                // Each error that can be is compiled to a trap where it was found, for a debug build to run.
                let placed = System.Collections.Generic.HashSet<string>()
                let defer =
                    if moduleErrors.IsEmpty then id
                    else
                        let failed = state.FailedExprs |> List.fold (fun acc (id, diagnostics) -> acc |> Map.add id diagnostics) Map.empty
                        let bySpan = mismatchErrors |> List.choose (fun diagnostic -> diagnostic.Span |> Option.map (fun span -> span, diagnostic)) |> Map.ofList
                        let unhandled = state.UnhandledMatches |> List.fold (fun acc (id, diagnostics) -> acc |> Map.add id diagnostics) Map.empty
                        DeferredErrors.deferErrors failed bySpan unhandled (Set.ofList state.UnknownTypeUses) placed
                let resolvedTypes, resolvedTypeDefs, resolvedItems = resolveTypes subst types state.ContextRequirements state.TypeDefs items
                let own = moduleFromTypedItems defer (StructuralInstances.callTargets state) state.OperatorCalls state.OperatorInstances state.ReadAs state.BuiltInProperties state.WorkflowBlocks resolvedItems |> ModuleLinking.writeBlockAliases state |> writeCells state |> nameIgnoredParameters state.ExprTypes subst
                let undeferred = moduleErrors |> List.filter (fun diagnostic -> not (placed.Contains (DeferredErrors.deferredMessage diagnostic)))
                // The module's own definitions win over imported ones of the same name.
                let ownNames = ModuleLinking.exportedValueNames own |> Set.union (own |> List.choose (function Kind (Def (ValueDef(false, name, _, _))) -> Some name | _ -> None) |> Set.ofList)
                let instances = inlinedInstances |> Map.fold (fun acc id found -> acc |> Map.add id found) state.ContextInstances
                let inlined =
                    inlined @ preludeInlined
                    |> List.filter (function
                        | Kind (Def (ValueDef(_, name, _, _))) -> not (ownNames.Contains name)
                        | _ -> true)
                    |> ModuleLinking.distinctDefinitions
                    |> ModuleLinking.reachedItems instances own
                // The names the imports and the prelude bring in, as written (`foo`, or `m.foo` under an
                // alias), each meaning the definition the environment settled on (an explicit import's over
                // the prelude's), less the names the module declares at the top level itself.
                let ownTopLevel =
                    state.Scopes
                    |> Map.keys
                    |> Seq.map (fun id -> state.Symbols.[id])
                    |> Seq.filter (fun symbol -> symbol.Kind = TopLevelValue)
                    |> List.ofSeq
                let declaredHere = ownTopLevel |> List.map (fun symbol -> symbol.Name) |> Set.ofList
                let imported =
                    env
                    |> Map.toList
                    |> List.choose (fun (name, scheme) ->
                        match scheme.Symbol with
                        | Some id when state.Symbols.ContainsKey id && not (declaredHere.Contains name) -> Some (name, id, scheme.Type)
                        | _ -> None)
                // Only the undotted ones are in scope as names: a dotted one is reached through its prefix.
                let scopes =
                    imported
                    |> List.filter (fun (name, _, _) -> not (name.Contains '.'))
                    |> List.fold (fun acc (_, id, _) -> acc |> Map.add id (Scope.wholeModule (sourceFileName origin))) state.Scopes
                // A def a `module name =` block does not export can only be named inside the block.
                let keptToItsBlock (symbol: Symbol) =
                    not symbol.IsExported
                    && state.ModuleBlocks |> List.exists (fun (_, block) -> SourceSpan.contains block symbol.Declaration)
                let moduleNames =
                    ownTopLevel
                    |> List.filter (keptToItsBlock >> not)
                    |> List.fold (fun acc symbol -> acc |> Map.add symbol.Name symbol.Id) (imported |> List.map (fun (name, id, _) -> name, id) |> Map.ofList)
                Ok { Typed =
                        { Module = inlined @ own
                          // The types of the imported definitions inlined for the backend, then the module's own.
                          Types = resolvedTypes |> Map.fold (fun acc name ty -> acc |> Map.add name ty) inlinedTypes
                          TypeDefs = resolvedTypeDefs
                          Items = resolvedItems
                          // Filled in for the root module only, by `compileWith`.
                          ExprTypes = Map.empty
                          CalleeTypes = Map.empty
                          SymbolTypes = Map.empty
                          Symbols = state.Symbols
                          AnnotatedSymbols = state.AnnotatedSymbols
                          References = state.References
                          ImportReferences = state.ImportReferences
                          Scopes = scopes
                          ModuleNames = moduleNames
                          ImportedTypes = imported |> List.map (fun (_, id, ty) -> id, ty) |> Map.ofList
                          ContextInstances = state.ContextInstances |> Map.fold (fun acc id found -> acc |> Map.add id found) inlinedInstances
                          ContextProviders = state.ContextProviders
                          UseContexts = state.UseContexts |> Map.fold (fun acc id context -> acc |> Map.add id context) inlinedUseContexts
                          // Resolved for the root module only, by `compileWith`.
                          InferredContextTypes = state.InferredContextTypes
                          ContextTypes = RequiredContexts.ofDefs state subst ownTopLevel
                          ImportedContextTypes = importedContextTypes
                          AmbientContexts = state.AmbientMembers |> Map.values |> Seq.map snd |> Seq.distinct |> Seq.sort |> List.ofSeq
                          ImportedFiles = importedFiles
                          // Filled in for the root module only, by `compileWith`.
                          InlinedExprTypes = Map.empty
                          InlinedCalleeTypes = Map.empty }
                     Errors = moduleErrors
                     Undeferred = undeferred
                     Own = own
                     Inlined = inlined
                     Name = FileImports.declaredName moduleBlocks desugared
                     Syntax = ast
                     Lowered = lowered
                     Subst = subst
                     RawExprTypes = state.ExprTypes
                     RawCalleeTypes = state.CalleeTypes
                     RawSymbolTypes = state.SymbolTypes
                     // Only declarations the module made itself are in `DeclarationSymbols`.
                     Exports =
                        state.DeclarationSymbols
                        |> Map.values
                        |> Seq.map (fun id -> state.Symbols.[id])
                        |> Seq.filter (fun symbol -> symbol.Kind = TopLevelValue && symbol.IsExported)
                        |> Seq.map (fun symbol -> symbol.Name, symbol)
                        |> Map.ofSeq
                     ContextRequirements =
                        let exported = ModuleLinking.exportedValueNames own
                        state.ContextRequirements |> Map.filter (fun name _ -> exported.Contains name)
                     ContextSignatures =
                        let exported = ModuleLinking.exportedValueNames own
                        state.ContextSignatures |> Map.filter (fun name _ -> exported.Contains name)
                     CheckedRests = state.CheckedRests
                     AmbientContexts =
                        state.AmbientMembers
                        |> Map.values
                        |> Seq.map snd
                        |> Set.ofSeq
                        |> Set.union state.CarriedAmbientContexts
                        |> Seq.choose (fun name -> state.TypeDefs |> Map.tryFind name |> Option.map (fun info -> name, info))
                        |> Map.ofSeq
                     ReExports = reExports }

    /// An imported module, checked the first time this compilation imports it; its source is only read then. A
    /// standard-library module is checked once per process (see `checkedStandardLibrary`), and the modules it imports
    /// come into this compilation with it.
    and private typecheckImport (checkedModules: CheckedModules) (origin: ModuleOrigin) (readSource: unit -> string) (visited: Set<string>) : Result<CheckedModule, CompilationPhase * Diagnostic list> =
        let key = originKey origin
        match checkedModules.TryGetValue key with
        | true, checkedModule -> checkedModule
        | false, _ ->
            let checkedModule =
                match origin with
                | StandardLibraryModule _ ->
                    let source = readSource ()
                    let check _ =
                        lazy
                            (let own = CheckedModules None
                             let checkedModule = typecheckModule own origin source visited
                             checkedModule, [ for KeyValue(importKey, imported) in own -> importKey, imported ])
                    let checkedModule, imports = checkedStandardLibrary.GetOrAdd((key, source), check).Value
                    for importKey, imported in imports do
                        checkedModules.TryAdd(importKey, imported) |> ignore
                    checkedModule
                | SourceFile _
                | InMemory -> typecheckModule checkedModules origin (readSource ()) visited
            checkedModules.[key] <- checkedModule
            checkedModule

    let parse (source: string) : Result<Module, Diagnostic list> =
        match parseModuleFileWithNames "" source with
        | Result.Ok (ast, _, _) -> Ok ast
        | Result.Error err -> Error [ syntaxError err ]

    /// The compile of the module from `origin`, which `typecheckModule` gave as `checked` with `checkedModules`, the linters
    /// `options` names run over it if it type-checked. A module with type or name errors is typed all the same, with what did
    /// check, beside its errors. Only the module being compiled is analyzed: an imported module is analyzed when it is
    /// compiled itself, so its diagnostics are not repeated in every module that imports it, and the standard library
    /// never is (but by tests).
    let internal compileChecked (checkedModules: CheckedModules) (options: LinterOptions) (origin: ModuleOrigin) (source: string) checked : CompileResult =
        match checked with
        | Result.Error (phase, diagnostics) ->
            { Phase = phase
              Diagnostics = diagnostics
              Typed = None
              TypeChecked = false
              ErrorsDeferred = false
              Syntax = None
              Lowered = Map.empty }
        | Result.Ok checkedModule ->
            // Types by node are only read for the root module, so only its are resolved: not those of every
            // imported module (the standard library included) on every compile.
            let resolve = Map.map (fun _ ty -> Unifier.apply checkedModule.Subst ty)
            // Nor are an imported module's resolved but for the nodes of its definitions that are inlined here, each by the
            // substitution of the module it came from.
            let inlinedNodes = System.Collections.Generic.HashSet<NodeId>()
            for item in checkedModule.Inlined do
                match item.Kind with
                | Def (ValueDef(_, _, _, expr)) ->
                    expr |> mapExpressionKinds (fun node kind -> inlinedNodes.Add node.Id |> ignore; kind) |> ignore
                | _ -> ()
            let imported = checkedModules.Values |> Seq.choose (function Result.Ok m -> Some m | Result.Error _ -> None) |> List.ofSeq
            let resolveInlined (raw: CheckedModule -> Map<NodeId, Ty>) =
                inlinedNodes
                |> Seq.choose (fun id ->
                    imported |> List.tryPick (fun m -> raw m |> Map.tryFind id |> Option.map (fun ty -> id, Unifier.apply m.Subst ty)))
                |> Map.ofSeq
            let typed =
                { checkedModule.Typed with
                    ExprTypes = resolve checkedModule.RawExprTypes
                    CalleeTypes = resolve checkedModule.RawCalleeTypes
                    SymbolTypes = resolve checkedModule.RawSymbolTypes
                    InferredContextTypes =
                        checkedModule.Typed.InferredContextTypes |> Map.map (fun _ tys -> tys |> List.map (Unifier.apply checkedModule.Subst))
                    ContextTypes = checkedModule.Typed.ContextTypes |> Map.map (fun _ tys -> tys |> List.map (Unifier.apply checkedModule.Subst))
                    InlinedExprTypes = resolveInlined _.RawExprTypes
                    InlinedCalleeTypes = resolveInlined _.RawCalleeTypes }
            // Callers that stop at errors (`nanyx run`, say) look at the diagnostics, and the typed module is still
            // there for the editor's features. The linters only look at a module that type-checked; a rule
            // configured as an error fails the compile as a compiler error does.
            { Phase = Typed
              Diagnostics =
                if checkedModule.Errors.IsEmpty then Linters.run options (SemanticModel.ofTyped (sourceFileName origin) source checkedModule.Syntax checkedModule.Lowered typed)
                  else ImportFixes.add (normalizeSource source) checkedModule.Syntax checkedModule.Errors |> MatchArmFixes.place (normalizeSource source) checkedModule.Syntax
              Typed = Some typed
              TypeChecked = checkedModule.Errors.IsEmpty
              ErrorsDeferred = checkedModule.Undeferred.IsEmpty
              Syntax = Some checkedModule.Syntax
              Lowered = checkedModule.Lowered }

    /// The module from `origin`, in no package, checked and compiled (see `compileChecked`).
    let compileFrom (options: LinterOptions) (origin: ModuleOrigin) (source: string) : CompileResult =
        let checkedModules = CheckedModules None in Nanyx.LargeStack.run (fun () -> compileChecked checkedModules options origin source (typecheckModule checkedModules origin source Set.empty))

    /// `compileSourceWith` for a source file of the package `package`, whose imports name its modules.
    let compileSourceIn (package: PackageModules) (options: LinterOptions) (filePath: string) (source: string) : CompileResult =
        let origin, checkedModules = SourceFile (Path.GetFullPath filePath), CheckedModules (Some package)
        Nanyx.LargeStack.run (fun () -> compileChecked checkedModules options origin source (typecheckModule checkedModules origin source Set.empty))

    /// `compileFile` with the linters and severities `options` gives.
    let compileFileWith (options: LinterOptions) (filePath: string) : CompileResult =
        if not (File.Exists filePath) then
            { Phase = Parsed
              Diagnostics = [ Diagnostics.general DiagnosticCodes.FileNotFound $"File not found: {filePath}" ]
              Typed = None
              TypeChecked = false
              ErrorsDeferred = false
              Syntax = None
              Lowered = Map.empty }
        else
            compileFrom options (SourceFile (Path.GetFullPath filePath)) (File.ReadAllText filePath)

    /// Compiles the file at `filePath` up to the typed module, resolving its imports (standard-library modules
    /// from the embedded library, everything else relative to the file). It runs no linters: `compileFileWith` runs those it is given.
    let compileFile (filePath: string) : CompileResult =
        compileFileWith Linters.none filePath

    /// `compileSource` with the linters and severities `options` gives.
    let compileSourceWith (options: LinterOptions) (filePath: string) (source: string) : CompileResult =
        compileFrom options (SourceFile (Path.GetFullPath filePath)) source

    /// Compiles `source` as if it were the contents of the file at `filePath` (which need not exist, or may hold
    /// older text: an editor's unsaved buffer, say). Imports are resolved exactly as `compileFile` resolves them.
    let compileSource (filePath: string) (source: string) : CompileResult =
        compileSourceWith Linters.none filePath source

    /// `compile` with the linters and severities `options` gives.
    let compileWith (options: LinterOptions) (source: string) : CompileResult =
        compileFrom options InMemory source

    /// Compiles source given directly, up to the typed module, without linters. Standard-library
    /// imports are resolved; imports by path cannot be, since there is no file for them to be relative to.
    let compile (source: string) : CompileResult =
        compileWith Linters.none source
