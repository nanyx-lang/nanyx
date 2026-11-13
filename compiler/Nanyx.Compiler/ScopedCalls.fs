/// Keeping what requires a scoped context inside the call that supplies it (see the Context escape page). A function
/// _supplies_ a scoped context to a parameter when the parameter's type requires it and the function's own contexts
/// don't: `memory: [c] ([$Memory & c] () -> a) -> a` supplies `$Memory` to its block. Each call to such a function
/// starts a scope of its own, the variable the scope in the callee's type is instantiated to, and every requirement of
/// the context its arguments make belongs to it. Once the arguments are checked, the scope must not be one of the
/// scopes around the call, or one a signature writes, and the call's result must not mention it. The scope is an
/// ordinary variable otherwise, so a function whose parameter is given it, `{ mk -> memory { mk() } }`, is generalised
/// over it and supplies the context in turn.
module internal NyxCompiler.ScopedCalls

open Parser.Program
open NyxCompiler.InferState

/// Each scoped context in `general`, with its scope there and what stands at the same place in `instantiated`, a type
/// of the same shape.
let rec private pairScopes (general: Ty) (instantiated: Ty) : (string * Ty * Ty) list =
    let all (gs: Ty list) (ss: Ty list) = if gs.Length = ss.Length then List.map2 pairScopes gs ss |> List.concat else []
    match general, instantiated with
    | FunctionContexts.ScopedContext(name, generalScope), FunctionContexts.ScopedContext(_, scope) -> [ name, generalScope, scope ]
    | TyFunc(ga, gr, gc), TyFunc(sa, sr, sc) -> all (ga :: gr :: gc) (sa :: sr :: sc)
    | TyApply(_, gs), TyApply(_, ss)
    | TyAlias(_, gs, _), TyAlias(_, ss, _)
    | TyUnion gs, TyUnion ss -> all gs ss
    | TyRecord gf, TyRecord sf when Set.ofSeq gf.Keys = Set.ofSeq sf.Keys -> all (List.ofSeq gf.Values) (List.ofSeq sf.Values)
    | TyTag(_, Some g), TyTag(_, Some s)
    | TyNominal(_, g, _), TyNominal(_, s, _) -> pairScopes g s
    | _ -> []

/// The names of the scoped contexts among `written`, contexts as a def's type writes them.
let private scopedIn (state: InferState) (written: TypeExpr list) : TypeExpr list =
    written |> List.filter (fun ctx -> match ctx.Kind with TypeName name -> WrittenContexts.isScoped state name | _ -> false)

/// The environment the arguments of the call `call` to `callee` are checked in, and `state` as they start to be.
/// `scheme` is the callee as `env` binds it, `calleeTy` its type at the call, and `written` the contexts its type
/// declares. A scoped context the callee requires itself is used where the call runs, of the scope its type has at the
/// call. Each one it supplies opens the scope it is instantiated to, and a `memory` block can make cells of it.
let enter (env: TypeEnv) (state: InferState) (call: Expression) (callee: Identifier) (scheme: TypeScheme) (calleeTy: Ty) (written: TypeExpr list) =
    let required, state = WrittenContexts.plain state calleeTy (scopedIn state written)
    let state = LambdaContexts.recordUnsupplied env required state
    let own = required |> List.choose (function FunctionContexts.PlainContext name -> Some name | _ -> None) |> Set.ofList
    let supplied =
        match Unifier.unalias scheme.Type, Unifier.unalias calleeTy with
        | TyFunc(general, _, _), TyFunc(input, _, _) ->
            pairScopes general input
            |> List.filter (fun (name, generalScope, _) ->
                not (own.Contains name)
                && match generalScope with
                   | TyVar v -> scheme.Quantified.Contains v.Id
                   | _ -> false)
            |> List.map (fun (name, _, scope) -> name, scope)
            |> List.distinctBy fst
        | _ -> []
    match supplied with
    | [] -> env, state
    | _ ->
        let env =
            match supplied |> List.tryFind (fst >> (=) MemoryCells.context) with
            | Some (_, scope) -> MemoryCells.aroundCall env state callee scope
            | None -> env
        // The scopes signatures write so far: a scope bound outside the call.
        let written = state.TypeVars |> Map.toList |> List.filter (fun (name, _) -> name.StartsWith "@") |> List.map (snd >> TyVar)
        env,
        { state with
            SupplyingCalls = state.SupplyingCalls |> Map.add call.Id (supplied, written)
            OpenScopes = List.map snd supplied @ state.OpenScopes }

/// Whether `ty` mentions the scope `scope`.
let private mentions (scope: Ty) (ty: Ty) =
    match scope with
    | TyVar v -> (TypeEnv.freeTyVars ty).Contains v.Id
    | _ -> false

/// What inside the call `call` makes it require its scope `scope`, as `resolve` resolves types, if anything does, and
/// how to name it: a use of a cell of that scope or, failing that, the first name whose type mentions the scope and
/// that `isCandidate` picks.
let private introducer (state: InferState) (resolve: Ty -> Ty) (call: Expression) (scope: Ty) (isCandidate: SymbolId -> bool) =
    let nodes =
        match call.Kind with
        | FunctionCall(_, _, args) -> args |> List.collect SyntaxTree.expressionsWithin
        | _ -> []
    let named (node: Expression) =
        match node.Kind, state.References |> Map.tryFind node.Id with
        | IdentifierExpr name, Some symbol -> Some (name, symbol, state.ExprTypes |> Map.tryFind node.Id)
        | FunctionCall(name, _, _), Some symbol -> Some (name, symbol, state.CalleeTypes |> Map.tryFind node.Id)
        | _ -> None
    let cellUse =
        nodes
        |> List.tryPick (fun node ->
            match named node with
            | Some (name, symbol, _) when state.Cells.TryFind symbol |> Option.exists (resolve >> (=) scope) && isCandidate symbol ->
                Some (node, $"the cell '{name}'")
            | _ -> None)
    cellUse
    |> Option.orElse (
        nodes
        |> List.tryPick (fun node ->
            match named node with
            | Some (name, symbol, Some ty) when isCandidate symbol && mentions scope (resolve ty) -> Some (node, $"'{name}'")
            | _ -> None))

/// The error for the scoped context `context` of the call `call`, which is the context's escape: reported at what
/// introduced it, `found`, with the call as a related span, or at the call.
let private escape (call: Expression) (found: (Expression * string) option) (message: string -> string) =
    match found with
    | Some (node, what) ->
        Diagnostics.at DiagnosticCodes.ScopedContextEscapes node.Span (message $" through {what}")
        |> Diagnostics.withRelated call.Span "the call that supplies it"
    | None -> Diagnostics.at DiagnosticCodes.ScopedContextEscapes call.Span (message "")

/// `state` once the call `call` is checked, with the scopes it opened closed (see `enter`), and with the errors, if one
/// of them leaves the call: it became a scope around the call or one a signature writes, or the call's result mentions
/// it. The call keeps its type, so that nothing around it fails for it.
let leave (call: Expression) (state: InferState) : InferState =
    match state.SupplyingCalls |> Map.tryFind call.Id with
    | None -> state
    | Some (supplied, written) ->
        let opened = List.map snd supplied
        let state = { state with OpenScopes = state.OpenScopes |> List.filter (fun scope -> not (List.contains scope opened)) }
        let resolve = Unifier.apply (fst (Unifier.solve state.Constraints))
        let callee = match call.Kind with FunctionCall(name, _, _) -> name | _ -> "the function"
        let result =
            match state.CalleeTypes |> Map.tryFind call.Id |> Option.map Unifier.unalias with
            | Some (TyFunc(_, output, _)) -> resolve output
            | _ -> TyPrimitive "unit"
        let outside = state.OpenScopes @ written |> List.map resolve
        let declaredOutside (symbol: SymbolId) =
            state.Symbols |> Map.tryFind symbol |> Option.forall (fun declared -> not (SourceSpan.contains call.Span declared.Declaration))
        let errors =
            supplied
            |> List.choose (fun (context, scope) ->
                let scope = resolve scope
                if List.contains scope outside then
                    let found = introducer state resolve call scope declaredOutside
                    Some (escape call found (fun through ->
                        $"'{context}' of this '{callee}' call would reach outside it{through}, which is bound outside the call: only what is made inside the call can require it"))
                elif mentions scope result then
                    let found = introducer state resolve call scope (fun _ -> true)
                    Some (escape call found (fun through ->
                        $"The result of this '{callee}' call requires its '{context}'{through}, so it cannot leave the call: return what the cells hold instead"))
                else None)
        if errors.IsEmpty then state else { state with FailedExprs = (call.Id, errors) :: state.FailedExprs }
