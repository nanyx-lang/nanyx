/// The contexts a module's own top-level defs require, and those its imports bring in require, as types, so that an
/// editor can show them with the defs' types.
module internal NyxCompiler.RequiredContexts

open NyxCompiler.InferState

/// The contexts each of `topLevel`, the module's own top-level defs, and each local def that writes its contexts
/// requires, written or inferred, by its symbol, as `TypedModule.ContextTypes` holds them once `subst` resolves them.
/// A variable a written context names is the variable of the def's type with that name, as a call reads the context's
/// arguments by name: `a` in `[Show(a)] a -> string`. The parameterised contexts an unannotated top-level def was
/// inferred to require are types already (see `InferredContextTypes`), and come first among those it requires.
let ofDefs (state: InferState) (subst: Subst) (topLevel: Symbol list) : Map<SymbolId, Ty list> =
    let requirements (symbol: Symbol) =
        match symbol.Kind with
        | TopLevelValue -> state.ContextRequirements |> Map.tryFind symbol.Name
        | _ -> state.LocalContextRequirements |> Map.tryFind symbol.Id
    let locals = state.LocalContextRequirements |> Map.keys |> Seq.map (fun id -> state.Symbols.[id]) |> List.ofSeq
    topLevel @ locals
    |> List.choose (fun symbol ->
        match requirements symbol, state.SymbolTypes |> Map.tryFind symbol.Id with
        | Some contexts, Some defTy ->
            let inferred = state.InferredContextTypes |> Map.tryFind symbol.Id |> Option.defaultValue []
            let byName ty =
                ContextInference.varsInOrder ty |> List.choose (fun var -> var.Name |> Option.map (fun name -> name, var))
            // `subst` can bind a named variable to an unnamed one, so the names are read before it too.
            let named = byName (Unifier.apply subst defTy) @ byName defTy |> Map.ofList
            let written =
                contexts
                |> List.skip (min inferred.Length contexts.Length)
                |> List.map (fun context -> fst (Infer.typeExprToTy { state with TypeVars = named } context))
            Some (symbol.Id, inferred @ written)
        | _ -> None)
    |> Map.ofList

/// The type and the contexts of each of `symbols`, the defs an import brings in, by its symbol, as
/// `TypedModule.ImportedContextTypes` holds them. `contextTypes` and `symbolTypes` are the imported module's
/// `ContextTypes` and the types of its defs before `subst`, its substitution, resolves them. The variables are
/// numbered apart from the importer's, as an imported def's type is in its environment, and keep their names.
let ofImport (subst: Subst) (contextTypes: Map<SymbolId, Ty list>) (symbolTypes: Map<SymbolId, Ty>) (symbols: SymbolId seq) : Map<SymbolId, Ty * Ty list> =
    symbols
    |> Seq.choose (fun id ->
        match symbolTypes |> Map.tryFind id with
        | Some general ->
            let contexts = contextTypes |> Map.tryFind id |> Option.defaultValue []
            let resolved = TyApply("", Unifier.apply subst general :: (contexts |> List.map (Unifier.apply subst)))
            let names = ContextInference.varsInOrder resolved |> List.map (fun var -> var.Id, var.Name) |> Map.ofList
            let renumbered =
                TypeEnv.freeTyVars resolved
                |> Seq.mapi (fun index varId ->
                    varId, TyVar { Id = -(index + 1); Name = names |> Map.tryFind varId |> Option.flatten })
                |> Map.ofSeq
            match Unifier.apply renumbered resolved with
            | TyApply(_, general :: contexts) -> Some (id, (general, contexts))
            | _ -> None
        | _ -> None)
    |> Map.ofSeq
