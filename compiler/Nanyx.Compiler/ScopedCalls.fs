/// Keeping what requires a scoped context inside the call that supplies it (see the Context escape page). A function
/// _supplies_ a scoped context to a parameter when the parameter's type requires it and the function's own contexts
/// don't: `memory: [c] ([Memory, c] () -> a) -> a` supplies `Memory` to its block. Each call to such a function
/// starts a scope of its own, a variable that stands where the callee's type has its scope, and every requirement of
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
    | TyNominal(_, _, g, _), TyNominal(_, _, s, _) -> pairScopes g s
    | _ -> []

/// The names of the scoped contexts among `written`, contexts as a def's type writes them.
let private scopedIn (state: InferState) (written: TypeExpr list) : TypeExpr list =
    written |> List.filter (fun ctx -> match ctx.Kind with TypeName name -> WrittenContexts.isScoped state name | _ -> false)

/// The environment the arguments of the call `call` to `callee` are checked in, the callee's type there, and `state` as
/// they start to be. `scheme` is the callee as `env` binds it, `calleeTy` its type at the call, and `written` the
/// contexts its type declares. A scoped context the callee requires itself is used where the call runs, of the scope
/// its type has at the call. Each one it supplies opens a scope of the call's own (see
/// `FunctionContexts.isCallScope`) where its type is instantiated to one, and a `memory` block can make cells of it.
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
    | [] -> env, calleeTy, state
    | _ ->
        let ownScopes, state =
            supplied
            |> List.choose (fun (_, scope) -> match scope with TyVar v -> Some v.Id | _ -> None)
            |> List.mapFold (fun (current: InferState) id ->
                (id, TyVar { Id = current.NextId; Name = Some FunctionContexts.callScopeName }),
                { current with NextId = current.NextId + 1 }) state
        let ownScopes = Map.ofList ownScopes
        let calleeTy = Unifier.apply ownScopes calleeTy
        let supplied = supplied |> List.map (fun (name, scope) -> name, Unifier.apply ownScopes scope)
        let env =
            match supplied |> List.tryFind (fst >> (=) MemoryCells.context) with
            | Some (_, scope) -> MemoryCells.aroundCall env state callee scope
            | None -> env
        // The scopes signatures write so far: a scope bound outside the call.
        let written = state.TypeVars |> Map.toList |> List.filter (fun (name, _) -> name.StartsWith "@") |> List.map (snd >> TyVar)
        env,
        calleeTy,
        { state with
            SupplyingCalls = state.SupplyingCalls |> Map.add call.Id (supplied, written)
            OpenScopes = List.map snd supplied @ state.OpenScopes }

/// Whether the def `defName`, where it writes its type, has the scoped context `context` in its body: the type writes
/// it, or the def supplies it to a function it is given, as `memory` does, or it takes a cell, for `Memory`.
let private declares (state: InferState) (defName: Identifier) (context: Identifier) =
    let names (contexts: TypeExpr list) = contexts |> List.exists (fun ctx -> ctx.Kind = TypeName context)
    let inParameters (signature: TypeExpr) =
        match signature.Kind with
        | TypeFunc(input, _) ->
            TypeExpr.within input
            |> List.exists (fun typeExpr ->
                match typeExpr.Kind with
                | TypeWithContext(contexts, _) -> names contexts
                | TypeApply(name, [ _ ]) -> name = CellPassingSyntax.typeName && context = MemoryCells.context
                | _ -> false)
        | _ -> false
    match state.WrittenTypes |> Map.tryFind defName with
    | Some { Kind = TypeWithContext(contexts, signature) } -> names contexts || inParameters signature
    | Some { Kind = TypeContext contexts } -> names contexts
    | Some signature -> inParameters signature
    | None -> false

/// The error for `subject` (`'open'`, which `instead` says how to move: `call 'open'`) at `span` requiring the scoped
/// context `context` where nothing supplies it. Something does in a lambda, which requires the context where it is
/// called, and in a def's body, where the def writes the context or, writing no type, is inferred to require it (see
/// `LambdaContexts.record`). A top-level value and a script's statements never have one: only a call that supplies the
/// context, as `memory { ... }` does `Memory`.
let private unsuppliedAt (state: InferState) (subject: string) (instead: string) (context: Identifier) (span: SourceSpan) =
    let supplier = if context = MemoryCells.context then "a `memory { ... }` block" else "a call that supplies it"
    if LambdaContexts.withinLambda state then None
    else
        match state.ContextScope with
        | DefScope(_, true) -> None
        | DefScope(defName, false) when declares state defName context -> None
        | DefScope(defName, false) ->
            Diagnostics.at DiagnosticCodes.UndeclaredContext span
                $"{subject} requires the scoped context '{context}', which '{defName}' does not declare: add [{context}] to the type of '{defName}', or {instead} inside {supplier}"
            |> ContextFixes.withAddToType state defName context
            |> Some
        | ValueScope valueName ->
            Diagnostics.at DiagnosticCodes.UndeclaredContext span
                $"{subject} requires the scoped context '{context}', which the value '{valueName}' cannot have: {instead} inside {supplier}"
            |> Some
        | AmbientScope
        | ScriptValueScope _ ->
            Diagnostics.at DiagnosticCodes.MissingContextMembers span
                $"{subject} requires the scoped context '{context}', which is not available here: {instead} inside {supplier}"
            |> Some

/// The error for the call to `callee` at `span`, which requires the scoped context `context`, where nothing supplies it
/// (see `unsuppliedAt`).
let unsupplied (state: InferState) (callee: Identifier) (context: Identifier) (span: SourceSpan) : Diagnostic option =
    unsuppliedAt state $"'{callee}'" $"call '{callee}'" context span

/// The errors for a loop at `span` whose body, of the type `bodyTy`, runs where it is written and requires scoped
/// contexts nothing supplies there.
let unsuppliedToLoop (state: InferState) (bodyTy: Ty) (span: SourceSpan) : Diagnostic list =
    match Unifier.unalias bodyTy with
    | TyFunc(_, _, contexts) ->
        contexts
        |> List.choose (function FunctionContexts.ScopedContext(name, _) -> Some name | _ -> None)
        |> List.distinct
        |> List.choose (fun context -> unsuppliedAt state "This loop's body" "write the loop" context span)
    | _ -> []

/// Whether `ty` mentions the scope `scope`.
let private mentions (scope: Ty) (ty: Ty) =
    match scope with
    | TyVar v -> (TypeEnv.freeTyVars ty).Contains v.Id
    | _ -> false

/// What inside the call `call` makes it require its scope `scope`, as `resolve` resolves types, if anything does, and
/// how to name it: a use of a cell of that scope or, failing that, the first value and then the first call whose type
/// mentions the scope and that `isCandidate` picks.
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
    let mentioning (isValue: bool) =
        nodes
        |> List.tryPick (fun node ->
            match named node, node.Kind with
            | Some (name, symbol, Some ty), kind when isCandidate symbol && (match kind with IdentifierExpr _ -> isValue | _ -> not isValue) && mentions scope (resolve ty) ->
                Some (node, $"'{name}'")
            | _ -> None)
    cellUse |> Option.orElse (mentioning true) |> Option.orElse (mentioning false)

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
        let resolve = Unifier.applySolved (fst (Unifier.solve state.Constraints))
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
                    let instead = if context = MemoryCells.context then ": return what the cells hold instead" else ""
                    Some (escape call found (fun through ->
                        $"The result of this '{callee}' call requires its '{context}'{through}, so it cannot leave the call{instead}"))
                else None)
        if errors.IsEmpty then state else { state with FailedExprs = (call.Id, errors) :: state.FailedExprs }
