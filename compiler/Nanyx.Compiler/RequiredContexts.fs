/// The contexts a module's own top-level defs require, as types, so that an editor can show them with the defs' types.
module internal NyxCompiler.RequiredContexts

open NyxCompiler.InferState

/// The contexts each of `defs`, the module's own top-level defs, requires, written or inferred, by its symbol, as
/// `TypedModule.ContextTypes` holds them once `subst` resolves them. A variable a written context names is the
/// variable of the def's type with that name, as a call reads the context's arguments by name: `a` in
/// `[Show(a)] a -> string`. The parameterised contexts an unannotated def was inferred to require are types already
/// (see `InferredContextTypes`), and come first among those it requires.
let ofDefs (state: InferState) (subst: Subst) (defs: Symbol list) : Map<SymbolId, Ty list> =
    defs
    |> List.choose (fun symbol ->
        match state.ContextRequirements |> Map.tryFind symbol.Name, state.SymbolTypes |> Map.tryFind symbol.Id with
        | Some contexts, Some defTy ->
            let inferred = state.InferredContextTypes |> Map.tryFind symbol.Id |> Option.defaultValue []
            let named =
                ContextInference.varsInOrder (Unifier.apply subst defTy)
                |> List.choose (fun var -> var.Name |> Option.map (fun name -> name, var))
                |> Map.ofList
            let written =
                contexts
                |> List.skip (min inferred.Length contexts.Length)
                |> List.map (fun context -> fst (Infer.typeExprToTy { state with TypeVars = named } context))
            Some (symbol.Id, inferred @ written)
        | _ -> None)
    |> Map.ofList
