namespace NyxCompiler

open System
open System.IO
open Parser.Program
open NyxCompiler.ExpressionRewrites
open NyxCompiler.ModuleOrigins

/// Merging the modules a module imports into it, one import at a time: what `Compiler.typecheckModule` checks the
/// module's own definitions against.
module internal ImportMerging =
    /// What a module's imports have brought in so far, as `typecheckModule` merges them one by one.
    type MergedImports =
        { Env: TypeEnv
          State: NyxCompiler.Infer.InferState
          Errors: Diagnostic list
          /// The errors of the modules imported that are each compiled to a trap where it was found (see
          /// `CheckedModule.ImportedErrors`), through the import that brings them in: such a module is imported, and
          /// a debug build runs up to the trap (#2967).
          DeferredImportErrors: Diagnostic list
          Inlined: TopLevelItem list
          /// Kept apart and inlined last, so that where an explicit import defines the same name, its definition is kept.
          PreludeInlined: TopLevelItem list
          PreludeTypeNames: Set<string>
          InlinedTypes: Map<string, Ty>
          /// Where the calls in the imported modules' definitions, which are inlined, find their instances.
          InlinedInstances: Map<NodeId, ContextInstance option list>
          InlinedAssociatedArguments: Map<NodeId, Map<string, Ty>>
          /// The instances the `use`s in the imported modules' definitions load.
          InlinedUseContexts: Map<NodeId, TypeExpr>
          /// The types of the defs the imports bring in, with the contexts they require (see `ImportedContextTypes`).
          ImportedContextTypes: Map<SymbolId, Ty * Ty list>
          /// Whether a module declares contexts the module's host provides, which importing it makes ambient (see
          /// `providesContexts`).
          ProvidesContexts: ModuleOrigin -> bool
          /// The module's host's root module as an import names it (`nanyx.cli.host/cli.host`), if it has a host.
          HostImport: string option
          /// The members of the ambient contexts carried in with imported functions, with the definitions that stand in for them.
          CarriedAmbientMembers: AmbientMember list
          /// The definitions the modules imported export from a component, by the names they are inlined under.
          ComponentExports: ComponentExport list
          /// The tests of the modules imported, by the names they are inlined under.
          Tests: Test list
          /// The modules that bring each name in unqualified, by the module's key, with the name of the module.
          /// The prelude is not one of them: a name it shares with an import is the import's, not ambiguous.
          UnqualifiedSources: Map<string, (string * string) list>
          ImportedFiles: Map<SymbolId, ImportedFile>
          /// The module's re-exports as each is merged, with the module it re-exports from.
          ReExports: ReExported list
          /// The modules merged, by `originKey`.
          Merged: Set<string>
          /// The names the definitions inlined are written by, by the names they are inlined under (see
          /// `TypedModule.WrittenNames`).
          WrittenNames: Map<string, string> }

    /// The modules each host's prelude re-exports from, by the prelude's file, as `originKey`s.
    let private preludeSources = System.Collections.Concurrent.ConcurrentDictionary<string, Set<string>>()

    /// Whether a module, by its origin, declares contexts that `host` provides: those its prelude re-exports from, as
    /// the console host's re-exports `nanyx.cli/console`'s `Console`. Importing one binds the names of its contexts' members,
    /// so that the contexts are ambient. A module with no host, a library's, sees no context as a host's.
    let providesContexts (host: Host option) (hostModules: PackageModules option) : ModuleOrigin -> bool =
        let sources =
            match host, hostModules with
            | Some host, Some modules ->
                match host.Prelude |> Option.bind (fun prelude -> modules.Modules |> Map.tryFind prelude) with
                | Some (file :: _) ->
                    preludeSources.GetOrAdd(file, fun file ->
                        match parseModuleFile file (File.ReadAllText file) with
                        | Result.Ok items ->
                            ModuleLinking.reExportsOf items
                            |> List.choose (fun item ->
                                match StandardLibrary.tryModuleName item, item.Source with
                                | Some name, _ -> Some (originKey (StandardLibraryModule name))
                                | None, ModuleImport name ->
                                    PackageModules.resolveWithin modules item name |> Result.toOption |> Option.map (SourceFile >> originKey)
                                | None, PathImport _ -> None)
                            |> Set.ofList
                        | Result.Error _ -> Set.empty)
                | _ -> Set.empty
            | _ -> Set.empty
        fun origin -> sources.Contains (originKey origin)

    /// The members of the module's ambient contexts, each with the imported definition that stands in for it, as the
    /// type checker found them (see `InferState.AmbientMembers`), then those of the contexts carried in (`carried`).
    let ambientMembers (state: NyxCompiler.Infer.InferState) (carried: AmbientMember list) : AmbientMember list =
        let own =
            state.AmbientMembers
            |> Map.values
            |> Seq.map snd
            |> Seq.distinct
            |> Seq.sort
            |> Seq.collect (fun context ->
                state.TypeDefs.[context].Members
                |> Map.keys
                |> Seq.map (fun memberName -> { Context = context; Member = memberName; Definition = AmbientMember.definitionName context memberName }))
            |> List.ofSeq
        own @ carried |> List.distinct

    /// Merges the import `item` of `imported` into `merged`, and gives back the import's symbol, if it has one. A
    /// made-up import has none; what it brings in is used through `through`, if given, as a re-export's names are
    /// used through the import of the module that re-exports them. `preludeKey` is the prelude's `originKey`.
    let private mergeImport (preludeKey: string) (importedOrigin: ModuleOrigin) (item: ImportItem) (through: SymbolId option) (imported: CheckedModule) (merged: MergedImports) : MergedImports * SymbolId option =
        let state = merged.State
        let importedKey = originKey importedOrigin
        let qualifier = ModuleLinking.importQualifier item
        // An import's definitions are inlined under qualified names, so that none takes the place of the importer's, of
        // the library's or of another module's of its name (#2995, #3221), but the standard library's, which every module
        // shares under one name. That is safe only because the standard library selects nothing but types from its
        // imports (`import nanyx/async as (Async)`), so no program's def can take the place of one of its functions. If
        // it ever selects a function by name, that import has to be qualified like the rest.
        let qualifies =
            match importedOrigin with
            | StandardLibraryModule _ -> false
            | SourceFile _
            | InMemory -> true
        // The members of each context a host provides, `Console`, are brought in as the module's
        // exports would be, each a name of its own, so that the context is an ambient one (see `InferModule`): code at a
        // script's top level, or in a def that infers the context, calls `readLine()`. The host supplies the instance.
        let hostMembers =
            if merged.ProvidesContexts importedOrigin then
                imported.Typed.TypeDefs
                |> Map.filter (fun name info -> info.IsContext && info.Parameters.IsEmpty && (ModuleLinking.exportedTypeNames imported.Own).Contains name)
                |> Map.toSeq
                |> Seq.collect (fun (contextName, info) ->
                    match Unifier.unalias info.Underlying with
                    | TyRecord(members, _) ->
                        members
                        |> Map.toSeq
                        |> Seq.choose (fun (memberName, ty) ->
                            info.Members
                            |> Map.tryFind memberName
                            |> Option.bind (fun memberSymbol -> imported.Typed.Symbols |> Map.tryFind memberSymbol)
                            |> Option.map (fun declared ->
                                contextName, (memberName, ty, { Id = SymbolId.next (); Name = memberName; Kind = TopLevelValue; Declaration = declared.Declaration; IsExported = true })))
                    | _ -> Seq.empty)
                |> List.ofSeq
            else []
        // An import that selects such a context selects its members too.
        let contextOfMember = hostMembers |> List.map (fun (context, (name, _, _)) -> name, context) |> Map.ofList
        let hostMembers = hostMembers |> List.map snd
        let exportedValues = ModuleLinking.exportedValues imported |> Set.union (hostMembers |> List.map (fun (name, _, _) -> name) |> Set.ofList)
        // An import brings in every export, or only those it selects.
        let selected = item.Selection |> Option.map (ModuleLinking.selectedExports imported)
        let isSelected (name: string) =
            selected |> Option.forall (fun selected -> selected.Contains name || contextOfMember |> Map.tryFind name |> Option.exists selected.Contains)
        let unqualifiedSources =
            if qualifier.IsNone && importedKey <> preludeKey then
                let moduleName =
                    match imported.Name, item.Source with
                    | Some name, _ -> name
                    | None, ModuleImport name -> name
                    | None, PathImport path -> path
                exportedValues
                |> Set.filter isSelected
                |> Set.fold (fun acc name ->
                    let sources = acc |> Map.tryFind name |> Option.defaultValue []
                    if sources |> List.exists (fun (key, _) -> key = importedKey) then acc
                    else acc |> Map.add name (sources @ [ importedKey, moduleName ])) merged.UnqualifiedSources
            else
                merged.UnqualifiedSources
        let exportedTypes = ModuleLinking.exportedTypes imported
        let exportedValueTypes =
            hostMembers
            |> List.fold (fun acc (name, ty, _) -> acc |> Map.add name ty) (ModuleLinking.withBlockReExports imported (ModuleLinking.ownTypes imported) |> Map.filter (fun name _ -> exportedValues.Contains name))
        let exportedTypeDefs =
            ModuleLinking.withBlockReExports imported imported.Typed.TypeDefs |> Map.filter (fun name _ -> exportedTypes.Contains name)
        let exports = hostMembers |> List.fold (fun acc (name, _, symbol) -> acc |> Map.add name symbol) (ModuleLinking.withBlockReExports imported imported.Exports)
        // The exported definitions, declared in the imported module's file.
        let exportedSymbols =
            exports
            |> Map.filter (fun name _ -> exportedValues.Contains name)
            |> Map.values
            |> Seq.map (fun symbol -> symbol.Id, symbol)
            |> Map.ofSeq
        // The exported types and their contexts' members, and the exported units, declared in the imported module's file
        // too.
        let exportedTypeSymbols =
            exportedTypeDefs
            |> Map.values
            |> Seq.collect (fun info -> Option.toList info.Symbol @ (info.Members |> Map.values |> List.ofSeq))
            |> Seq.append (ModuleLinking.exportedUnits imported |> Map.filter (fun name _ -> isSelected name) |> Map.values |> Seq.choose _.Symbol)
            |> Seq.choose (fun id -> imported.Typed.Symbols |> Map.tryFind id)
            |> Seq.map (fun symbol -> symbol.Id, symbol)
            |> Map.ofSeq
        let env = ModuleLinking.extendEnvWithImport merged.Env qualifier (exportedValueTypes |> Map.filter (fun name _ -> isSelected name)) (exports |> Map.map (fun _ symbol -> symbol.Id))
        // The import is a symbol of its own, unless the compiler made it up (for `@entry`).
        let importedThroughSymbol (importId: SymbolId) =
            Seq.append exportedSymbols exportedTypeSymbols
            |> Seq.fold (fun acc (KeyValue(id, _)) ->
                acc |> Map.add id ((acc |> Map.tryFind id |> Option.defaultValue []) @ [ importId ])) state.ImportedThrough
        let importSymbols, importedThrough, importedFiles, state =
            if SourceSpan.isNone item.Span then
                Map.empty, (through |> Option.map importedThroughSymbol |> Option.defaultValue state.ImportedThrough), merged.ImportedFiles, state
            else
                let name =
                    match item.Alias, item.Source with
                    | Some alias, _ -> alias
                    | None, ModuleImport moduleName -> moduleName
                    | None, PathImport path -> path
                let import = { Id = SymbolId.next (); Name = name; Kind = ImportedModule; Declaration = item.Span; IsExported = false }
                let importedFiles = merged.ImportedFiles |> Map.add import.Id { File = sourceFileName importedOrigin; Name = imported.Name }
                // Only an alias is written as a name: an import without one has no name to write.
                let state =
                    if item.Alias.IsSome then { state with Scopes = state.Scopes |> Map.add import.Id (Scope.wholeModule item.Span.File) }
                    else state
                Map.ofList [ import.Id, import ], importedThroughSymbol import.Id, importedFiles, state
        // A type an explicit import exports replaces the prelude's type of the same name.
        let preludeTypeNames =
            if importedKey = preludeKey then Set.ofSeq exportedTypeDefs.Keys else merged.PreludeTypeNames
        let typeDefs =
            if importedKey = preludeKey then
                state.TypeDefs
            else
                state.TypeDefs |> Map.filter (fun name _ -> not (preludeTypeNames.Contains name && exportedTypeDefs.ContainsKey name))
        // An aliased import's types are written with the alias (`m.Email`). Their own names stay known for
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
        // A function that requires a context requires it wherever it is called, under the name it is imported as. A
        // value the import brings in replaces one of its name brought in before, the prelude's as the module's imports
        // replace it, and so do the contexts it requires, or that it requires none (#3173).
        let replaced =
            exportedValueTypes.Keys |> Seq.filter isSelected |> Seq.map (ModuleLinking.importedName qualifier) |> Set.ofSeq
        let contextRequirements =
            ModuleLinking.withBlockReExports imported imported.ContextRequirements
            |> Map.filter (fun name _ -> isSelected name)
            |> Map.fold
                (fun acc name contexts -> acc |> Map.add (ModuleLinking.importedName qualifier name) contexts)
                (state.ContextRequirements |> Map.filter (fun name _ -> not (replaced.Contains name)))
        let contextSignatures =
            ModuleLinking.withBlockReExports imported imported.ContextSignatures
            |> Map.filter (fun name _ -> isSelected name)
            |> Map.fold
                (fun acc name signature -> acc |> Map.add (ModuleLinking.importedName qualifier name) signature)
                (state.ContextSignatures |> Map.filter (fun name _ -> not (replaced.Contains name)))
        let typeDefs = ModuleLinking.mergeTypeDefs typeDefs exportedTypeDefs
        // A unit is written unqualified, as in `500ms`, under an alias too: an import brings in the units its module
        // exports, or those it selects. Two units of one name are ambiguous where the name is written (see `Units`), but
        // for the prelude's, which another import's replaces.
        let units, ambiguousUnits =
            ModuleLinking.exportedUnits imported
            |> Map.filter (fun name _ -> isSelected name)
            // A module re-exports no unit it imports.
            |> Map.map (fun _ info -> { info with IsExport = false })
            |> Map.fold (fun (units, ambiguous) name info ->
                match units |> Map.tryFind name with
                // An import's unit replaces the prelude's of its name, as an import's value does.
                | Some existing when existing <> info && existing.Home <> StandardLibraryModule StandardLibrary.preludeModuleName ->
                    let each = ambiguous |> Map.tryFind name |> Option.defaultValue [ existing ]
                    units, ambiguous |> Map.add name (List.distinct (each @ [ info ]))
                | _ -> units |> Map.add name info, ambiguous) (state.Units, state.AmbiguousUnits)
        // The contexts the imported functions require that this module cannot name are carried in with their
        // definitions, hidden, so a call to one is checked as a call needing the context is here: an ambient
        // one, and a parameterised one, whose instance each call finds (an attached `Point.show` for a
        // `Show(Point)`, say).
        // Under a host, one carried in neither way can only come from the host, and the host does not provide it: a call
        // to a function that requires it is an error (see `InferState.UnprovidedContexts`). `HostRuntime`, which only a
        // host may require, has an error of its own (see `Hosts`).
        let carried, carriedParameterised, unprovided =
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
            let carried = unnamed |> List.choose (fun name -> imported.AmbientContexts |> Map.tryFind name |> Option.map (fun info -> name, info))
            let carriedParameterised =
                unnamed
                |> List.choose (fun name ->
                    imported.Typed.TypeDefs
                    |> Map.tryFind name
                    |> Option.filter (fun info -> info.IsContext && not info.Parameters.IsEmpty && not (imported.AmbientContexts.ContainsKey name))
                    |> Option.map (fun info -> name, info))
            let unprovided =
                match merged.HostImport with
                | Some hostImport ->
                    let carriedNames = carried @ carriedParameterised |> List.map fst |> Set.ofList
                    unnamed
                    |> List.filter (fun name -> not (carriedNames.Contains name) && name <> Hosts.runtimeContext)
                    |> List.map (fun name -> name, hostImport)
                | None -> []
            carried, carriedParameterised, unprovided
        let state =
            { state with
                TypeDefs = carried @ carriedParameterised |> List.fold (fun acc (name, info) -> acc |> Map.add name info) typeDefs
                HiddenTypeNames = carried @ carriedParameterised |> List.fold (fun acc (name, _) -> acc |> Set.add name) hiddenTypeNames
                CarriedAmbientContexts = carried |> List.fold (fun acc (name, _) -> acc |> Set.add name) state.CarriedAmbientContexts
                UnprovidedContexts = unprovided |> List.fold (fun acc (name, hostImport) -> acc |> Map.add name hostImport) state.UnprovidedContexts
                HostContexts =
                    if merged.ProvidesContexts importedOrigin then
                        exportedTypeDefs
                        |> Map.filter (fun name info -> info.IsContext && info.Parameters.IsEmpty && isSelected name)
                        |> Map.keys
                        |> Seq.fold (fun acc name -> acc |> Set.add (ModuleLinking.importedName qualifier name)) state.HostContexts
                    else state.HostContexts
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
                Units = units
                AmbiguousUnits = ambiguousUnits
                ImportedBlockNames =
                    imported.BlockReExports
                    |> List.filter (fun (name, _) -> isSelected name)
                    |> List.fold (fun acc (name, target) ->
                        let written = ModuleLinking.importedName qualifier name
                        match imported.Exports |> Map.tryFind target, imported.Typed.TypeDefs |> Map.tryFind target |> Option.bind _.Symbol with
                        | Some symbol, _ -> acc |> Map.add written (symbol.Id, ModuleLinking.inlinedName qualifies item imported target)
                        // A type is inlined under its own name.
                        | None, Some symbol -> acc |> Map.add written (symbol, target)
                        | None, None -> acc) state.ImportedBlockNames
                    // A name an import brings in unqualified is written as the name its definition is inlined under,
                    // which is qualified, but for the standard library's.
                    |> fun names ->
                        if qualifies && qualifier.IsNone then
                            exportedValues
                            |> Set.filter isSelected
                            |> Set.fold (fun acc name ->
                                match exports |> Map.tryFind name with
                                | Some symbol when not (acc.ContainsKey name) ->
                                    let inlined = ModuleLinking.inlinedName true item imported name
                                    if inlined = name then acc else acc |> Map.add name (symbol.Id, inlined)
                                | _ -> acc) names
                        else
                            names
                // A call to an imported function checks what it compares the rests of its unions with as a
                // call to one of the module's own does.
                CheckedRests =
                    imported.CheckedRests
                    |> Map.filter (fun symbol _ -> exportedSymbols.ContainsKey symbol)
                    |> Map.fold (fun acc symbol checkedRests -> acc |> Map.add symbol checkedRests) state.CheckedRests
                // A call to an imported function points back at the types it writes for its parameters, in its file.
                ParameterTypes =
                    imported.ParameterTypes
                    |> Map.filter (fun symbol _ -> exportedSymbols.ContainsKey symbol)
                    |> Map.fold (fun acc symbol written -> acc |> Map.add symbol written) state.ParameterTypes }
        let inlinedItems = ModuleLinking.inlinedItemsForImport qualifies item imported
        { merged with
            Env = env
            State = state
            Inlined = if importedKey = preludeKey then merged.Inlined else merged.Inlined @ inlinedItems
            PreludeInlined = if importedKey = preludeKey then inlinedItems else merged.PreludeInlined
            PreludeTypeNames = preludeTypeNames
            InlinedTypes = ModuleLinking.inlinedTypesForImport qualifies item imported |> Map.fold (fun acc name ty -> acc |> Map.add name ty) merged.InlinedTypes
            InlinedInstances = ModuleLinking.inlinedInstancesForImport qualifies item imported |> Map.fold (fun acc id found -> acc |> Map.add id found) merged.InlinedInstances
            InlinedUseContexts = imported.Typed.UseContexts |> Map.fold (fun acc id context -> acc |> Map.add id context) merged.InlinedUseContexts
            InlinedAssociatedArguments = imported.Typed.AssociatedArguments |> Map.fold (fun acc id named -> acc |> Map.add id named) merged.InlinedAssociatedArguments
            ImportedContextTypes =
                RequiredContexts.ofImport imported.Subst imported.Typed.ContextTypes imported.RawSymbolTypes exportedSymbols.Keys
                |> Map.fold (fun acc id found -> acc |> Map.add id found) merged.ImportedContextTypes
            CarriedAmbientMembers =
                merged.CarriedAmbientMembers @ ModuleLinking.ambientMembersForImport qualifies item imported (carried |> List.map fst |> Set.ofList)
            ComponentExports = merged.ComponentExports @ ModuleLinking.componentExportsForImport qualifies item imported
            Tests = merged.Tests @ ModuleLinking.testsForImport qualifies item imported
            WrittenNames = ModuleLinking.writtenNamesForImport qualifies item imported |> Map.fold (fun acc name written -> acc |> Map.add name written) merged.WrittenNames
            UnqualifiedSources = unqualifiedSources
            ImportedFiles = importedFiles
            Merged = merged.Merged.Add importedKey },
        importSymbols |> Map.keys |> Seq.tryHead

    /// Merges the import `item`, then what `imported` re-exports, as made-up imports of the modules it re-exports
    /// from, under the same alias and limited to what `item` selects. Each is used through the import `item` is,
    /// which is given back.
    let rec private mergeWithReExports (preludeKey: string) (importedOrigin: ModuleOrigin) (item: ImportItem) (through: SymbolId option) (imported: CheckedModule) (merged: MergedImports) : MergedImports * SymbolId option =
        let merged, importSymbol = mergeImport preludeKey importedOrigin item through imported merged
        let through = importSymbol |> Option.orElse through
        let merged =
            imported.ReExports
            |> List.fold (fun merged reExported ->
                // A whole-module re-export gives everything its module gives, or what `item` selects of it.
                let names =
                    match reExported.Item.Selection with
                    | Some names -> Some (names |> List.filter (fun (name, _) -> item.Selection |> Option.forall (List.exists (fun (selected, _) -> selected = name))))
                    | None ->
                        let given = ModuleLinking.reExportSources reExported
                        item.Selection |> Option.map (List.filter (fun (name, _) -> given.ContainsKey name))
                match names with
                | Some [] -> merged
                | names ->
                    let reImport = { Source = reExported.Item.Source; Alias = item.Alias; Selection = names; Span = SourceSpan.none }
                    mergeWithReExports preludeKey reExported.Origin reImport through reExported.Module merged |> fst) merged
        merged, through

    /// Merges the import `item` as written. `as m & (a, b)` is an import under `m` and an import of `a` and `b`
    /// alone. The second is made up, so it is not an import symbol of its own. A name the module does not export is
    /// an error. A re-export (one of `reExportItems`) is an import that selects the names it exports again, which
    /// the module's importers are given.
    let mergeWritten (preludeKey: string) (reExportItems: ImportItem list) (importedOrigin: ModuleOrigin) (item: ImportItem) (imported: CheckedModule) (merged: MergedImports) : MergedImports =
        match ModuleLinking.unexportedSelections item imported, item.Alias, item.Selection with
        | _ :: _ as unexported, _, _ -> { merged with Errors = merged.Errors @ unexported }
        | [], Some _, Some _ ->
            let merged, through = mergeWithReExports preludeKey importedOrigin { item with Selection = None } None imported merged
            mergeWithReExports preludeKey importedOrigin { item with Alias = None; Span = SourceSpan.none } through imported merged |> fst
        | [], _, _ ->
            let merged, _ = mergeWithReExports preludeKey importedOrigin item None imported merged
            if reExportItems |> List.contains item then
                { merged with ReExports = merged.ReExports @ [ { Origin = importedOrigin; Item = item; Module = imported } ] }
            else
                merged
