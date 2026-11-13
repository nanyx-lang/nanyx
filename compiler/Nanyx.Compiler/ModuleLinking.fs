namespace NyxCompiler

open Parser.Program
open NyxCompiler.ExpressionRewrites

/// Linking a checked module into the modules that import it: what an import brings into scope, the items it inlines
/// for the backend and the names they are inlined under.
module internal ModuleLinking =
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
    let private renameReferencesInExpr (typed: TypedModule) (renames: Map<string, string>) (expr: Expression) : Expression =
        let isShadowed (node: Expression) =
            typed.References |> Map.tryFind node.Id |> Option.bind (fun id -> typed.Symbols |> Map.tryFind id)
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

    /// `module'` with each name a block import brought in (`zero`, `$Money.new`) written as the definition it stands
    /// for (`money.zero`), which is the only name the backend knows. Only a name inference resolved to that definition
    /// is rewritten, not a local that shadows it.
    let internal writeBlockAliases (state: NyxCompiler.Infer.InferState) (module': Module) : Module =
        if state.BlockAliases.IsEmpty then
            module'
        else
            let resolved (node: Expression) (name: string) =
                match state.BlockAliases |> Map.tryFind name, state.References |> Map.tryFind node.Id |> Option.bind (fun id -> state.Symbols |> Map.tryFind id) with
                | Some qualified, Some symbol when symbol.Name = qualified -> qualified
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

    /// The inlined items `own` uses: the value definitions it reaches, directly or through other inlined
    /// definitions, and every type definition, in their original order. A definition named like a member of a
    /// context is always kept, because the backend supplies it as the ambient instance of that context (the
    /// runtime's `Console`, say) without anything naming it. So is each attached definition an instance the type
    /// checker built for a call in `own`, or in a definition kept, is made of (`instances`, by the call's node), as
    /// `int.toString` for a `ToString(int)`: the backend passes it where the instance is required, unnamed.
    let internal reachedItems (instances: Map<NodeId, ContextInstance option list>) (own: Module) (inlined: TopLevelItem list) : TopLevelItem list =
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
                    fields |> List.map (function TypeField(fieldName, _, _, _) | TypeMember(fieldName, _) -> fieldName)
                | _ -> [])
            |> Set.ofList
        let contextMembers = membersOfContexts (fun _ -> true)
        // The members of the contexts without parameters, which an import under an alias defines under it (`c.write`).
        let plainContextMembers = membersOfContexts List.isEmpty
        let rootNames =
            own
            |> List.collect (function
                | Kind (Def (ValueDef(_, _, _, value))) | Kind (Expr value) -> SyntaxTree.referencedNames value @ synthesizedMembersIn instances value
                | _ -> [])
        let isReferenced (referenced: Set<string>) (name: string) =
            referenced.Contains name
            || contextMembers.Contains name
            || (name.Contains "." && referenced.Contains (name.Substring(name.LastIndexOf '.')))
            || (name.Contains "." && plainContextMembers.Contains (name.Substring(name.LastIndexOf '.' + 1)))
        let rec reach (referenced: Set<string>) (reached: Set<string>) =
            let newlyReached =
                bodies |> List.filter (fun (name, _) -> not (reached.Contains name) && isReferenced referenced name)
            if newlyReached.IsEmpty then
                reached
            else
                let referenced = newlyReached |> List.collect (fun (_, value) -> SyntaxTree.referencedNames value @ synthesizedMembersIn instances value) |> List.fold (fun acc name -> Set.add name acc) referenced
                reach referenced (newlyReached |> List.fold (fun acc (name, _) -> Set.add name acc) reached)
        let reached = reach (Set.ofList rootNames) Set.empty
        inlined
        |> List.filter (function
            | Kind (Def (ValueDef(_, name, _, _))) -> reached.Contains name
            | _ -> true)

    let internal exportedValueNames (module': Module) =
        module'
        |> List.choose (function
            | Kind (Def (ValueDef(true, name, _, _))) -> Some name
            | _ -> None)
        |> Set.ofList

    let internal exportedTypeNames (module': Module) =
        module'
        |> List.choose (function
            | Kind (Def (TypeDef(name, modifiers, _, _))) when modifiers |> List.contains Export -> Some name
            | _ -> None)
        |> Set.ofList

    /// The exports of `imported` that `selection` brings in unqualified: each name it selects, and the definitions
    /// attached to each type it selects, as `$Email.parse` with `$Email`.
    let internal selectedExports (imported: CheckedModule) (selection: (Identifier * SourceSpan) list) : Set<string> =
        let names = selection |> List.map fst |> Set.ofList
        let types = exportedTypeNames imported.Own |> Set.intersect names
        exportedValueNames imported.Own
        |> Set.filter (fun name -> types |> Set.exists (fun typeName -> name.StartsWith(typeName + ".")))
        |> Set.union names

    /// The items an importer inlines for the backend from a module it imports: the module's own value
    /// definitions and type definitions (plus whatever that module inlined itself). Exported definitions keep
    /// their names, or take the import's alias as a qualifier; private ones are qualified with the module's own
    /// name so they cannot collide with anything in the importer.
    /// An import that selects names without an alias, `as (a, b)`, brings in only the exports it selects (see
    /// `selectedExports`) unqualified; the others are inlined as private definitions are.
    let private inlinedRenames (item: ImportItem) (checkedModule: CheckedModule) : Map<string, string> =
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
                | None, _ -> None
            | Kind (Def (ValueDef(false, name, _, _))) -> Some (name, $"{moduleName}.{name}")
            | _ -> None)
        |> Map.ofList

    /// The error for each name `item` selects that `imported` does not export, with the exports closest to it.
    let internal unexportedSelections (item: ImportItem) (imported: CheckedModule) : Diagnostic list =
        let reExported = imported.ReExports |> List.collect (fun reExport -> reExport.Item.Selection |> Option.defaultValue [] |> List.map fst)
        let exports = Set.union (exportedValueNames imported.Own) (exportedTypeNames imported.Own) |> Set.union (Set.ofList reExported)
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
    let internal ambientMembersForImport (item: ImportItem) (checkedModule: CheckedModule) (carried: Set<string>) : AmbientMember list =
        let renames = inlinedRenames item checkedModule
        checkedModule.Typed.AmbientMembers
        |> List.filter (fun ambient -> carried.Contains ambient.Context)
        |> List.map (fun ambient -> { ambient with Definition = renames |> Map.tryFind ambient.Definition |> Option.defaultValue ambient.Definition })

    let internal inlinedItemsForImport (item: ImportItem) (checkedModule: CheckedModule) : TopLevelItem list =
        let renames = inlinedRenames item checkedModule
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
    /// the backend knows them as it knows the importer's own (the layout of an imported record, say).
    let internal inlinedTypesForImport (item: ImportItem) (checkedModule: CheckedModule) : Map<string, Ty> =
        let renames = inlinedRenames item checkedModule
        checkedModule.Typed.Types
        |> Seq.map (fun (KeyValue(name, ty)) -> (renames |> Map.tryFind name |> Option.defaultValue name), ty)
        |> Map.ofSeq

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
                |> Seq.mapi (fun index id -> id, TyVar { Id = -(index + 1); Name = contextVariables |> Map.tryFind id |> Option.flatten })
                |> Map.ofSeq
            TypeEnv.generalize TypeEnv.empty (Unifier.apply renumbered ty)
        types
        |> Map.fold (fun acc name ty ->
            acc |> TypeEnv.declare (importedName qualifierOpt name) (symbols |> Map.tryFind name) (quantify ty)) env
