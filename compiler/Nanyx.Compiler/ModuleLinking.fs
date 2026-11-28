namespace NyxCompiler

open Parser.Program
open NyxCompiler.ExpressionRewrites

/// Linking a checked module into the modules that import it: what an import brings into scope, the items it inlines
/// for the backend and the names they are inlined under.
module internal ModuleLinking =
    /// Whether `name` is a definition attached to a type (`Regex.toString`, `int.double`), one of `typeNames` or a
    /// built-in one, which an instance finds by its type's name: not a block's (`outer.twice`).
    let internal isAttachedName (typeNames: string -> bool) (name: string) =
        match name.LastIndexOf '.' with
        | -1 -> false
        | index ->
            let qualifier = name.Substring(0, index)
            TypeResolution.isBuiltInTypeName qualifier || typeNames qualifier

    let internal importQualifier (item: ImportItem) =
        item.Alias |> Option.map IdentifierNames.sanitizeIdentifier

    /// The name an imported definition `name` is known by: `m.name` under the import's qualifier `m`, else `name`.
    let internal importedName (qualifierOpt: string option) (name: string) =
        match qualifierOpt with
        | Some qualifier -> $"{qualifier}.{name}"
        | None -> name

    let internal reExportsOf (module': Module) =
        module'
        |> List.choose (function
            | Kind (ReExport item) -> Some item
            | _ -> None)

    /// Renames every reference to a name in `renames` throughout an expression. Used when a module is
    /// inlined into its importer, so that its definitions keep pointing at each other under their new names.
    /// A name that inference resolved to a parameter, a local definition or a pattern variable of the module
    /// (`typed`) shadows the top-level definition, so it keeps its name.
    let private renameReferences (references: Map<NodeId, SymbolId>) (symbols: Map<SymbolId, Symbol>) (renames: Map<string, string>) (expr: Expression) : Expression =
        let isShadowed (node: Expression) =
            references |> Map.tryFind node.Id |> Option.bind (fun id -> symbols |> Map.tryFind id)
            |> Option.exists (fun symbol -> symbol.Kind = Parameter || symbol.Kind = LocalValue || symbol.Kind = PatternVariable)
        let rename (node: Expression) (name: string) =
            if isShadowed node then name else renames |> Map.tryFind name |> Option.defaultValue name
        expr
        |> mapExpressionKinds (fun current kind ->
            match kind with
            | IdentifierExpr name -> IdentifierExpr(rename current name)
            | FunctionCall(name, rangeOpt, args) -> FunctionCall(rename current name, rangeOpt, args)
            | Pipe(value, name, rangeOpt, args) -> Pipe(value, rename current name, rangeOpt, args)
            | other -> other)

    let private renameReferencesInExpr (typed: TypedModule) (renames: Map<string, string>) (expr: Expression) : Expression =
        renameReferences typed.References typed.Symbols renames expr

    /// The module's own top-level definitions, of `own`, that are named like one of the library's inlined into it
    /// (`library`), each with the name the backend knows it by: after the module, `moduleName`, as an importer names a
    /// private definition of it (see `CheckedModule.OwnRenames`). A definition attached to a type, one of `typeNames`,
    /// keeps its name, and wins over the library's of its name (see `Library`).
    let internal ownRenames (moduleName: string) (typeNames: string -> bool) (library: Set<string>) (own: Module) : Map<string, string> =
        let qualifier = IdentifierNames.sanitizeIdentifier moduleName
        own
        |> List.choose (function
            | Kind (Def (ValueDef(_, name, _, _))) when library.Contains name && not (isAttachedName typeNames name) -> Some (name, $"{qualifier}.{name}")
            | _ -> None)
        |> Map.ofList

    /// `own` with each definition `renames` names under its new name, and each reference to it, that inference resolved
    /// to it rather than to a local of the same name (`state`), written so.
    let internal renameOwn (state: NyxCompiler.Infer.InferState) (renames: Map<string, string>) (own: Module) : Module =
        if renames.IsEmpty then
            own
        else
            let rename = renameReferences state.References state.Symbols renames
            own
            |> List.map (fun item ->
                match item.Kind with
                | Def (ValueDef(isExport, name, typeOpt, value)) ->
                    { item with Kind = Def (ValueDef(isExport, renames |> Map.tryFind name |> Option.defaultValue name, typeOpt, rename value)) }
                | Expr value -> { item with Kind = Expr (rename value) }
                | _ -> item)

    /// The types of `checkedModule`'s definitions by the names it writes: its own under their own names, though `Typed`
    /// knows those it renames by their new ones (see `CheckedModule.OwnRenames`).
    let internal ownTypes (checkedModule: CheckedModule) : Map<string, Ty> =
        checkedModule.OwnRenames
        |> Map.fold (fun acc own renamed ->
            match checkedModule.Typed.Types |> Map.tryFind renamed with
            | Some ty -> acc |> Map.add own ty
            | None -> acc) checkedModule.Typed.Types

    /// `name`, a definition `Typed` names (see `CheckedModule.OwnRenames`), by the name the module writes.
    let private writtenName (checkedModule: CheckedModule) (name: string) =
        checkedModule.OwnRenames |> Map.tryFindKey (fun _ renamed -> renamed = name) |> Option.defaultValue name

    /// `module'` with each name a block import brought in (`zero`, `Money.new`, `m.zero`), or a block's name without its
    /// file's module in front (`money.zero` in `module shop`), written as the definition it stands for (`money.zero`, or
    /// `shop.money.zero`), which is the only name the backend knows, and each name an import brought in from what a
    /// module re-exports of its blocks written as the name its definition is inlined under. Only a name inference resolved to that definition
    /// is rewritten, not a local that shadows it. So is each context a def's type requires (`Tx`, as `db.Tx`), whose
    /// members the backend finds by the context's layout, which the block's type definition has under its own name.
    let internal writeBlockAliases (state: NyxCompiler.Infer.InferState) (module': Module) : Module =
        if state.BlockAliases.IsEmpty && state.ImportedBlockNames.IsEmpty then
            module'
        else
            let declaredContext (context: TypeExpr) =
                match context.Kind with
                | TypeName name -> { context with Kind = TypeName (WrittenContexts.canonical state name) }
                | TypeApply(name, args) -> { context with Kind = TypeApply(WrittenContexts.canonical state name, args) }
                | _ -> context
            let declaredContexts (typeOpt: TypeExpr option) =
                match typeOpt with
                | Some ({ Kind = TypeWithContext(contexts, inner) } as annotation) ->
                    Some { annotation with Kind = TypeWithContext(contexts |> List.map declaredContext, inner) }
                | _ -> typeOpt
            let resolved (node: Expression) (name: string) =
                let reference = state.References |> Map.tryFind node.Id
                match state.BlockAliases |> Map.tryFind name, reference |> Option.bind (fun id -> state.Symbols |> Map.tryFind id) with
                // A block's re-export of a name another module defines (`export util (double)`) is no alias of its own.
                | Some qualified, Some symbol when symbol.Name = qualified && qualified <> name -> qualified
                | _ ->
                    match state.ImportedBlockNames |> Map.tryFind name with
                    | Some (symbol, inlined) when reference = Some symbol -> inlined
                    | _ -> name
            let rewrite =
                mapExpressionKinds (fun current kind ->
                    match kind with
                    | IdentifierExpr name -> IdentifierExpr(resolved current name)
                    | FunctionCall(name, rangeOpt, args) -> FunctionCall(resolved current name, rangeOpt, args)
                    | Pipe(value, name, rangeOpt, args) -> Pipe(value, resolved current name, rangeOpt, args)
                    // `c.penny` or `coins.penny`, naming a block's definition under another name, is the definition.
                    | MemberAccess _ as access ->
                        match SyntaxTree.dottedName current with
                        | Some name when resolved current name <> name -> IdentifierExpr(resolved current name)
                        | _ -> access
                    | other -> other)
            module'
            |> List.map (fun item ->
                match item.Kind with
                | Def (ValueDef(isExport, name, typeOpt, value)) -> { item with Kind = Def (ValueDef(isExport, name, declaredContexts typeOpt, rewrite value)) }
                | Expr value -> { item with Kind = Expr (rewrite value) }
                | _ -> item)

    /// `module'` with each context member named under an import's alias (`c.write`, of `c.Console`) written by its own
    /// name (`write`), the key the backend passes it by: a member of a context a def declares, or of one a host provides.
    /// Only a name inference resolved to the member is rewritten.
    let internal writeMemberAliases (state: NyxCompiler.Infer.InferState) (module': Module) : Module =
        let members =
            Seq.append
                (state.TypeDefs |> Map.values |> Seq.filter _.IsContext |> Seq.collect (fun info -> info.Members.Values))
                (state.AmbientMembers |> Map.values |> Seq.filter (fun (_, context) -> state.HostContexts.Contains context) |> Seq.map fst)
            |> Set.ofSeq
        let resolved (node: Expression) (name: string) =
            match state.References |> Map.tryFind node.Id with
            | Some id when name.Contains '.' && members.Contains id ->
                let own = name.Substring(name.LastIndexOf '.' + 1)
                match state.Symbols |> Map.tryFind id with
                | Some symbol when symbol.Name = own -> own
                | _ -> name
            | _ -> name
        let rewrite =
            mapExpressionKinds (fun current kind ->
                match kind with
                | IdentifierExpr name -> IdentifierExpr(resolved current name)
                | FunctionCall(name, rangeOpt, args) -> FunctionCall(resolved current name, rangeOpt, args)
                | Pipe(value, name, rangeOpt, args) -> Pipe(value, resolved current name, rangeOpt, args)
                | other -> other)
        module'
        |> List.map (fun item ->
            match item.Kind with
            | Def (ValueDef(isExport, name, typeOpt, value)) -> { item with Kind = Def (ValueDef(isExport, name, typeOpt, rewrite value)) }
            | Expr value -> { item with Kind = Expr (rewrite value) }
            | _ -> item)

    /// `module'` with each bare name that reaches a def attached to a context written as that def's name, `start` as
    /// `Async.start` (see `AttachedContextDefs`), so that what follows inference sees the def it names.
    let internal writeAttachedNames (state: NyxCompiler.Infer.InferState) (module': Module) : Module =
        if state.AttachedByBareName.IsEmpty then module'
        else
            let resolved (node: Expression) (name: string) = state.AttachedByBareName |> Map.tryFind node.Id |> Option.defaultValue name
            let rewrite =
                mapExpressionKinds (fun current kind ->
                    match kind with
                    | IdentifierExpr name -> IdentifierExpr(resolved current name)
                    | FunctionCall(name, rangeOpt, args) -> FunctionCall(resolved current name, rangeOpt, args)
                    | Pipe(value, name, rangeOpt, args) -> Pipe(value, resolved current name, rangeOpt, args)
                    | other -> other)
            module'
            |> List.map (fun item ->
                match item.Kind with
                | Def (ValueDef(isExport, name, typeOpt, value)) -> { item with Kind = Def (ValueDef(isExport, name, typeOpt, rewrite value)) }
                | Expr value -> { item with Kind = Expr (rewrite value) }
                | _ -> item)

    /// The inlined items `own` uses: the value definitions it reaches, directly or through other inlined
    /// definitions, and every type definition, in their original order. A definition named like a member of a
    /// context is always kept, because the backend supplies it as the ambient instance of that context (the
    /// runtime's `Console`, say) without anything naming it. So is each attached definition an instance the type
    /// checker built for a call in `own`, or in a definition kept, is made of (`instances`, by the call's node), as
    /// `int.toString` for a `ToString(int)`: the backend passes it where the instance is required, unnamed. And so is
    /// each definition a component exports (`exported`), which the program needn't name either. And so are a map's and a
    /// set's `toList`, and the functions that convert one's store, which the backend calls to read one, as `dbg` does,
    /// or to box one for its type's description, where nothing names them (see `Maps.toList` and `Maps.convertedStore`).
    let internal reachedItems (instances: Map<NodeId, ContextInstance option list>) (own: Module) (exported: string list) (inlined: TopLevelItem list) : TopLevelItem list =
        let bodies =
            inlined
            |> List.choose (function
                | Kind (Def (ValueDef(_, name, _, value))) -> Some (name, value)
                | _ -> None)
        // The members of the contexts whose type parameters `included` accepts.
        let membersOfContexts (included: (Identifier * TypeExpr option) list -> bool) =
            inlined @ own
            |> List.collect (function
                | Kind (Def (TypeDef(_, modifiers, parameters, Kind (TypeRecord fields)))) when modifiers |> List.contains Context && included parameters ->
                    fields |> List.choose (function TypeField(fieldName, _, _, _) -> Some fieldName | TypeFieldAttribute _ -> None)
                | _ -> [])
            |> Set.ofList
        let contextMembers = membersOfContexts (fun _ -> true)
        // The members of the contexts without parameters, which an import under an alias defines under it (`c.write`).
        let plainContextMembers = membersOfContexts List.isEmpty
        // A definition attached to a type (`int.zero`) is not one an import's alias qualifies, whatever its name.
        let typeNames = inlined @ own |> List.choose (function Kind (Def (TypeDef(name, _, _, _))) -> Some name | _ -> None) |> Set.ofList
        let isAttached (name: string) =
            let qualifier = name.Substring(0, name.LastIndexOf '.')
            TypeResolution.isBuiltInTypeName qualifier || typeNames.Contains qualifier
        let rootNames =
            own
            |> List.collect (function
                | Kind (Def (ValueDef(_, _, _, value))) | Kind (Expr value) -> SyntaxTree.referencedNames value @ synthesizedMembersIn instances value
                | _ -> [])
        let isReferenced (referenced: Set<string>) (name: string) =
            referenced.Contains name
            || contextMembers.Contains name
            || (name.Contains "." && referenced.Contains (name.Substring(name.LastIndexOf '.')))
            || (name.Contains "." && not (isAttached name) && plainContextMembers.Contains (name.Substring(name.LastIndexOf '.' + 1)))
        let rec reach (referenced: Set<string>) (reached: Set<string>) =
            let newlyReached =
                bodies |> List.filter (fun (name, _) -> not (reached.Contains name) && isReferenced referenced name)
            if newlyReached.IsEmpty then
                reached
            else
                let referenced = newlyReached |> List.collect (fun (_, value) -> SyntaxTree.referencedNames value @ synthesizedMembersIn instances value) |> List.fold (fun acc name -> Set.add name acc) referenced
                reach referenced (newlyReached |> List.fold (fun acc (name, _) -> Set.add name acc) reached)
        let reached = reach (Set.ofList (rootNames @ exported @ [ Maps.toList; Sets.toList; Maps.convertedStore; Sets.convertedStore ])) Set.empty
        inlined
        |> List.filter (function
            | Kind (Def (ValueDef(_, name, _, _))) -> reached.Contains name
            | _ -> true)

    let internal exportedTypeNames (module': Module) =
        module'
        |> List.choose (function
            | Kind (Def (TypeDef(name, modifiers, _, _))) when modifiers |> List.contains Export -> Some name
            | _ -> None)
        |> Set.ofList

    /// `byName`, something of `imported`'s by the names of its definitions or types, with the same under each name it
    /// gives from its blocks (see `CheckedModule.BlockReExports`).
    let internal withBlockReExports (imported: CheckedModule) (byName: Map<string, 'a>) : Map<string, 'a> =
        imported.BlockReExports
        |> List.fold (fun acc (name, target) ->
            match byName |> Map.tryFind target with
            | Some found -> acc |> Map.add name found
            | None -> acc) byName

    /// The names of the values `imported` exports: its own exported definitions, and those it re-exports from its
    /// blocks.
    let internal exportedValues (imported: CheckedModule) : Set<string> =
        let own = SyntaxTree.exportedValueNames imported.Own
        imported.BlockReExports |> List.fold (fun acc (name, target) -> if own.Contains target then acc.Add name else acc) own

    /// The names of the types and contexts `imported` exports: its own, and those it re-exports from its blocks.
    let internal exportedTypes (imported: CheckedModule) : Set<string> =
        let own = exportedTypeNames imported.Own
        imported.BlockReExports |> List.fold (fun acc (name, target) -> if own.Contains target then acc.Add name else acc) own

    /// The units of measure `imported` exports, by name: those it declares with `export unit` (see `Units`), in any of
    /// its files, if it is split across files. A unit it imports is not one of them: it is not exported where it is
    /// imported (see `ImportMerging`).
    let internal exportedUnits (imported: CheckedModule) : Map<string, UnitInfo> =
        imported.Typed.Units |> Map.filter (fun _ info -> info.IsExport)

    /// What the file `module'` gives its importers by re-exporting its own `module name =` blocks at its top level, as
    /// `export shapes`: each re-export, with the names it gives that stand for a definition or type of the file. What a
    /// block re-exports from another file's module is a re-export of that module (see `Library`).
    let internal blockReExports (state: NyxCompiler.Infer.InferState) (module': Module) : (NyxCompiler.InferState.BlockImport * (string * string) list) list =
        let defined =
            module'
            |> List.choose (function
                | Kind (Def (ValueDef(_, name, _, _)))
                | Kind (Def (TypeDef(name, _, _, _))) -> Some name
                | _ -> None)
            |> Set.ofList
        state.BlockImports
        |> List.filter (fun blockImport -> blockImport.IsReExport && (NyxCompiler.InferState.blockAround state.ModuleBlocks blockImport.Import.Span).IsNone)
        |> List.map (fun blockImport -> blockImport, blockImport.Names |> Map.toList |> List.filter (snd >> defined.Contains))

    /// The exports of `imported` that `selection` brings in unqualified: each name it selects, and the definitions
    /// attached to each type it selects, as `Email.parse` with `Email`.
    let internal selectedExports (imported: CheckedModule) (selection: (Identifier * SourceSpan) list) : Set<string> =
        let names = selection |> List.map fst |> Set.ofList
        let types = exportedTypes imported |> Set.intersect names
        exportedValues imported
        |> Set.filter (fun name -> types |> Set.exists (fun typeName -> name.StartsWith(typeName + ".")))
        |> Set.union names

    /// The names an importer inlines a module's own value definitions under, for the backend (see
    /// `inlinedItemsForImport`). An export takes the import's alias as a qualifier, and a private definition the
    /// module's own name, so that it can't collide with anything in the importer. Of a module that isn't the standard
    /// library's (`qualified`), an export an import brings in unqualified, whole or selected, `as (a, b)`, is named after
    /// its module too, so that it can't collide with a definition of the importer's, of the library's, or of another
    /// module's of the same name; the importer's references to it are written as that name (see `writeBlockAliases`).
    /// The standard library's keep their names, which every module shares, and so does a definition attached to a type
    /// (`Regex.toString`), which an instance finds by its type's, and a block's, named after its module already.
    let private inlinedRenames (qualified: bool) (item: ImportItem) (checkedModule: CheckedModule) : Map<string, string> =
        let qualifier = importQualifier item
        let moduleName =
            checkedModule.Name
            |> Option.map IdentifierNames.sanitizeIdentifier
            |> Option.defaultValue "import"
        let selected = item.Selection |> Option.map (selectedExports checkedModule)
        checkedModule.Own
        |> List.choose (function
            | Kind (Def (ValueDef(true, name, _, _))) ->
                match qualifier, selected with
                | Some q, _ -> Some (name, $"{q}.{name}")
                | None, Some selected when not (selected.Contains name) -> Some (name, $"{moduleName}.{name}")
                // A block's definition is named after its file's module already (`lib.outer.twice`).
                | None, _ when qualified && not (isAttachedName checkedModule.Typed.TypeDefs.ContainsKey name) && not (name.StartsWith $"{moduleName}.") ->
                    Some (name, $"{moduleName}.{name}")
                | None, _ -> None
            | Kind (Def (ValueDef(false, name, _, _))) -> Some (name, $"{moduleName}.{name}")
            | _ -> None)
        |> Map.ofList

    /// The name the import `item` of `checkedModule` inlines its definition `name` under (see `inlinedItemsForImport`).
    let internal inlinedName (qualified: bool) (item: ImportItem) (checkedModule: CheckedModule) (name: string) =
        inlinedRenames qualified item checkedModule |> Map.tryFind name |> Option.defaultValue name

    /// The names `imported`, from `origin`, gives its importers, each with the module that defines it: its own exports,
    /// and what it re-exports, which for a whole-module re-export (`export m`) is everything `m` gives, so through
    /// chains of them.
    let rec internal exportSources (origin: ModuleOrigin) (imported: CheckedModule) : Map<string, ModuleOrigin> =
        let own =
            Set.union (exportedValues imported) (exportedTypes imported)
            |> Seq.map (fun name -> name, origin)
            |> Map.ofSeq
        imported.ReExports
        |> List.fold (fun sources reExport -> reExportSources reExport |> Map.fold (fun acc name source -> Map.add name source acc) sources) own

    /// The names the re-export `reExport` gives the importers of the module that writes it, each with the module that
    /// defines it.
    and internal reExportSources (reExport: ReExported) : Map<string, ModuleOrigin> =
        let all = exportSources reExport.Origin reExport.Module
        match reExport.Item.Selection with
        | Some selection -> selection |> List.choose (fun (name, _) -> all |> Map.tryFind name |> Option.map (fun source -> name, source)) |> Map.ofList
        | None -> all

    /// The module a re-export names, as it is written.
    let private writtenSource (item: ImportItem) =
        match item.Source with
        | ModuleImport name -> name
        | PathImport path -> $"\"{path}\""

    /// An error for each name a whole re-export gives that the module `own` defines itself, or that a re-export before
    /// it gives from elsewhere, at the whole one, naming both. Each re-export is given with what it gives, by where each
    /// name is defined: in a module, or in a block of `own`, by the name of its definition there. Its fix turns the whole
    /// re-export into one that selects the rest of what it gives.
    let private clashesAmong (own: Module) (reExports: (ImportItem * Map<string, Choice<ModuleOrigin, string>>) list) : Diagnostic list =
        let defined =
            own
            |> List.choose (function
                | Kind (Def (ValueDef(_, name, _, _)))
                | Kind (Def (TypeDef(name, _, _, _))) -> Some name
                | _ -> None)
            |> Set.ofList
        let fix (item: ImportItem) (sources: Map<string, _>) (clashing: Set<string>) =
            match sources.Keys |> Seq.filter (clashing.Contains >> not) |> List.ofSeq with
            | [] -> []
            | rest ->
                let written = writtenSource item
                [ { Title = $"Re-export only what doesn't clash from '{written}'"
                    Edits = [ { Span = item.Span; NewText = $"""{written} ({String.concat ", " rest})""" } ] } ]
        reExports
        |> List.fold (fun (given: Map<string, Choice<ModuleOrigin, string> * ImportItem>, errors) (item, sources) ->
            let written = writtenSource item
            let clashes =
                match item.Selection with
                | Some _ -> []
                | None ->
                    sources
                    |> Map.toList
                    |> List.choose (fun (name, source) ->
                        if defined.Contains name then
                            Some (name, $"'{name}' is re-exported from '{written}', but this module defines '{name}' too", SourceSpan.none)
                        else
                            match given |> Map.tryFind name with
                            | Some (other, earlier) when other <> source ->
                                let message = $"'{name}' is re-exported from '{written}', but also from '{writtenSource earlier}', where it is another definition"
                                Some (name, message, earlier.Span)
                            | _ -> None)
            let fixes = fix item sources (clashes |> List.map (fun (name, _, _) -> name) |> Set.ofList)
            let errors =
                errors
                @ (clashes
                   |> List.map (fun (name, message, earlier) ->
                       { (Diagnostics.at DiagnosticCodes.ReExportClash item.Span message |> Diagnostics.about name) with Fixes = fixes }
                       |> Diagnostics.withRelated earlier "Re-exported here too"))
            Map.fold (fun acc name source -> acc |> Map.add name (source, item)) given sources, errors) (Map.empty, [])
        |> snd

    let private ofModules (reExports: ReExported list) =
        reExports |> List.map (fun reExport -> reExport.Item, reExportSources reExport |> Map.map (fun _ origin -> Choice1Of2 origin))

    /// An error for each name a whole-module re-export (`export m`) gives that the module `own` defines itself, or that
    /// a re-export before it gives from another module (see `clashesAmong`).
    let internal reExportClashes (own: Module) (reExports: ReExported list) : Diagnostic list =
        clashesAmong own (ofModules reExports)

    /// `reExportClashes` for the re-exports of `own`'s blocks (see `blockReExports`), each with the names it gives
    /// and what they stand for, among its re-exports of other modules, `reExports`.
    let internal blockReExportClashes (own: Module) (reExports: ReExported list) (blockReExports: (ImportItem * (string * string) list) list) : Diagnostic list =
        let blocks = blockReExports |> List.map (fun (item, given) -> item, given |> List.map (fun (name, target) -> name, Choice2Of2 target) |> Map.ofList)
        let spans = blocks |> List.map (fun (item, _) -> item.Span) |> Set.ofList
        clashesAmong own (ofModules reExports @ blocks |> List.sortBy (fun (item, _) -> item.Span.Start))
        |> List.filter (fun diagnostic -> diagnostic.Span |> Option.exists spans.Contains)

    /// The error for each name `item` selects that `imported` does not export, with the exports closest to it.
    let internal unexportedSelections (item: ImportItem) (imported: CheckedModule) : Diagnostic list =
        let own = Set.unionMany [ exportedValues imported; exportedTypes imported; Set.ofSeq (exportedUnits imported).Keys ]
        let exports = imported.ReExports |> List.fold (fun names reExport -> Set.union names (Set.ofSeq (reExportSources reExport).Keys)) own
        let shown =
            match item.Source with
            | ModuleImport name -> name
            | PathImport path -> path
        item.Selection
        |> Option.defaultValue []
        |> List.filter (fun (name, _) -> not (exports.Contains name))
        |> List.map (fun (name, span) ->
            NameSuggestions.unknown DiagnosticCodes.NotExported span $"'{shown}' does not export '{name}'" exports name)

    /// The members of the ambient contexts `carried` names that `checkedModule` records (see
    /// `TypedModule.AmbientMembers`), each under the name `inlinedItemsForImport` inlines its definition under.
    let internal ambientMembersForImport (qualified: bool) (item: ImportItem) (checkedModule: CheckedModule) (carried: Set<string>) : AmbientMember list =
        let renames = inlinedRenames qualified item checkedModule
        checkedModule.Typed.AmbientMembers
        |> List.filter (fun ambient -> carried.Contains ambient.Context)
        |> List.map (fun ambient -> { ambient with Definition = renames |> Map.tryFind ambient.Definition |> Option.defaultValue ambient.Definition })

    /// The definitions `checkedModule` exports from a component (see `TypedModule.ComponentExports`), by the names the
    /// import `item` inlines them under.
    let internal componentExportsForImport (qualified: bool) (item: ImportItem) (checkedModule: CheckedModule) : ComponentExport list =
        let renames = inlinedRenames qualified item checkedModule
        checkedModule.Typed.ComponentExports
        |> List.map (fun export ->
            let definition = writtenName checkedModule export.Definition
            { export with Definition = renames |> Map.tryFind definition |> Option.defaultValue definition })

    /// The tests of `checkedModule`, by the names `item` inlines them under.
    let internal testsForImport (qualified: bool) (item: ImportItem) (checkedModule: CheckedModule) : Test list =
        let renames = inlinedRenames qualified item checkedModule
        checkedModule.Typed.Tests
        |> List.map (fun test ->
            let definition = writtenName checkedModule test.Definition
            { test with Definition = renames |> Map.tryFind definition |> Option.defaultValue definition })

    let internal inlinedItemsForImport (qualified: bool) (item: ImportItem) (checkedModule: CheckedModule) : TopLevelItem list =
        let renames = inlinedRenames qualified item checkedModule
        let rename (name: string) = renames |> Map.tryFind name |> Option.defaultValue name
        let ownItems =
            checkedModule.Own
            |> List.choose (function
                | Kind (Def (ValueDef(_, name, typeOpt, expr))) as item ->
                    Some { item with Kind = Def (ValueDef(false, rename name, typeOpt, renameReferencesInExpr checkedModule.Typed renames expr)) }
                | Kind (Def (TypeDef _)) as typeDef -> Some typeDef
                | _ -> None)
        checkedModule.Inlined @ ownItems

    /// The types of the definitions `inlinedItemsForImport` inlines, by the names they are inlined under, so that
    /// the backend knows them as it knows the importer's own (the layout of an imported record, say). The library's
    /// definition a definition of the module's own shadows keeps its name (see `CheckedModule.OwnRenames`).
    let internal inlinedTypesForImport (qualified: bool) (item: ImportItem) (checkedModule: CheckedModule) : Map<string, Ty> =
        let renames = inlinedRenames qualified item checkedModule
        let rename (name: string) = renames |> Map.tryFind name |> Option.defaultValue name
        let written = checkedModule.OwnRenames |> Map.toSeq |> Seq.map (fun (own, renamed) -> renamed, own) |> Map.ofSeq
        checkedModule.Typed.Types
        |> Seq.map (fun (KeyValue(name, ty)) ->
            match written |> Map.tryFind name with
            | Some own -> rename own, ty
            | None when checkedModule.OwnRenames.ContainsKey name -> name, ty
            | None -> rename name, ty)
        |> Map.ofSeq

    /// The instances the calls in the definitions `inlinedItemsForImport` inlines find, with the definitions they name
    /// by the names those are inlined under: a private `__toString_1` a module derives is `two.__toString_1` (#1812).
    let internal inlinedInstancesForImport (qualified: bool) (item: ImportItem) (checkedModule: CheckedModule) : Map<NodeId, ContextInstance option list> =
        let renames = inlinedRenames qualified item checkedModule
        let rename (name: string) = renames |> Map.tryFind name |> Option.defaultValue name
        let renameInstance instance =
            match instance with
            | InstanceSynthesized members -> InstanceSynthesized(members |> List.map (fun (memberName, definition) -> memberName, rename definition))
            | InstanceDerived(memberName, definition, at) -> InstanceDerived(memberName, rename definition, at)
            | InstanceInScope _
            | InstanceBuiltIn -> instance
        checkedModule.Typed.ContextInstances |> Map.map (fun _ found -> found |> List.map (Option.map renameInstance))

    /// Keeps the first definition of each name, so a module inlined along two import paths appears once.
    let internal distinctDefinitions (items: TopLevelItem list) : TopLevelItem list =
        let seen = System.Collections.Generic.HashSet<string>()
        items
        |> List.filter (function
            | Kind (Def (ValueDef(_, name, _, _))) -> seen.Add("value:" + name)
            | Kind (Def (TypeDef(name, _, _, _))) -> seen.Add("type:" + name)
            | _ -> true)

    let internal mergeTypeDefs (target: Map<string, TypeDefInfo>) (incoming: Map<string, TypeDefInfo>) =
        incoming
        |> Map.fold (fun acc key value ->
            if Map.containsKey key acc then acc else acc |> Map.add key value) target

    /// Brings an imported module's exported values into scope, as references to their symbols (`symbols`, by name).
    /// Every type variable left in an exported type is quantified, so each use takes fresh ones. The imported
    /// module numbered its variables separately from the importer, so they are first renumbered below zero, where
    /// the importer never numbers one: a quantified variable that shared an id with one of its fresh replacements
    /// would be replaced twice.
    let internal extendEnvWithImport (env: TypeEnv) (qualifierOpt: string option) (types: Map<string, Ty>) (symbols: Map<string, SymbolId>) =
        let quantify ty =
            // A context variable keeps its name, which a call binds it by (see `ContextVariables`).
            let contextVariables = FunctionContexts.variablesIn ty
            let renumbered =
                TypeEnv.freeTyVars ty
                |> Seq.mapi (fun index id -> id, TyVar (TyVar.ordinary (-(index + 1)) (contextVariables |> Map.tryFind id |> Option.flatten)))
                |> Map.ofSeq
            TypeEnv.generalize TypeEnv.empty (Unifier.apply renumbered ty)
        types
        |> Map.fold (fun acc name ty ->
            acc |> TypeEnv.declare (importedName qualifierOpt name) (symbols |> Map.tryFind name) (quantify ty)) env
