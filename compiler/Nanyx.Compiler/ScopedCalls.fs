/// Keeping what requires a scoped context inside the call that supplies it (see "Values cannot escape memory scope" on the docs' Memory page). A function
/// _supplies_ a scoped context to a parameter when the parameter's type requires it, or takes a value of a type in its scope, and the function's own contexts
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
    | TyRecord(gf, _), TyRecord(sf, _) when Set.ofSeq gf.Keys = Set.ofSeq sf.Keys -> all (List.ofSeq gf.Values) (List.ofSeq sf.Values)
    | TyTag(_, Some g), TyTag(_, Some s) -> pairScopes g s
    // A value of a scoped type, `MRef(a)`, holds its scope in its arguments, not in what it is made from.
    | TyNominal(_, gs, g, _), TyNominal(_, ss, s, _) -> all (gs @ [ g ]) (ss @ [ s ])
    | TyMeasure(_, g), TyMeasure(_, s) -> pairScopes g s
    | _ -> []

/// The names of the scoped contexts among `written`, contexts as a def's type writes them.
let private scopedIn (state: InferState) (written: TypeExpr list) : TypeExpr list =
    written |> List.filter (fun ctx -> match ctx.Kind with TypeName name -> WrittenContexts.isScoped state name | _ -> false)

/// `required`, the contexts a call of the type `calleeTy` requires itself, with a `TaskScope` made of the scope of the
/// call whose block the call is in, where that call supplies it and nothing else says which scope it is: the callee's
/// input doesn't hold its scope. So `start` requires the `TaskScope` of the `Async.concurrent` whose block it is in (see
/// `TaskScopes`). A `memory` block's `Memory` is not: a block may use an outer block's (`use m`). The block's type would make it so once its lambda is inferred, as it does a `memory` block's
/// `Memory` (see `MemoryCells.settleBlock`); made so now, the block's lambda uses the context once, not once for each
/// call, which each local def it generalises would look at (#2551). A call that is given what holds the scope, as
/// `await` is given a task, may be given one of another block's, and is left alone.
let private ofBlockScope (calleeTy: Ty) (required: Ty list) (state: InferState) : Ty list * InferState =
    let depth = state.LambdaContexts.Length - 1
    let inInput =
        match Unifier.unalias calleeTy with
        | TyFunc(input, _, _) -> TypeEnv.freeTyVars input
        | _ -> Set.empty
    let blockScopeOf (context: string) =
        state.OpenScopes
        |> List.tryFind (function
            | TyVar { Id = id; Name = Some name; Kind = CallScope } -> name = context && state.CallDepths.TryFind id = Some depth
            | _ -> false)
    required
    |> List.mapFold (fun current context ->
        match context with
        | FunctionContexts.ScopedContext(name, (TyVar v as scope)) when name = TaskScopeSyntax.context && v.Kind <> CallScope && not (inInput.Contains v.Id) ->
            match blockScopeOf name with
            | Some blockScope -> FunctionContexts.scoped name blockScope, addConstraint scope blockScope current
            | None -> context, current
        | _ -> context, current) state

/// The environment the arguments of the call `call` to `callee` are checked in, the callee's type there, and `state` as
/// they start to be. `scheme` is the callee as `env` binds it, `calleeTy` its type at the call, and `written` the
/// contexts its type declares. A scoped context the callee requires itself is used where the call runs, of the scope
/// its type has at the call. Each one it supplies opens a scope of the call's own (see
/// `FunctionContexts.isCallScope`) where its type is instantiated to one, and a `memory` block can make cells of it.
let enter (env: TypeEnv) (state: InferState) (call: Expression) (callee: Identifier) (scheme: TypeScheme) (calleeTy: Ty) (written: TypeExpr list) =
    let required, state = WrittenContexts.plain state calleeTy (scopedIn state written)
    let required, state = ofBlockScope calleeTy required state
    let state = LambdaContexts.recordUnsupplied env required state
    let own = required |> List.choose (function FunctionContexts.PlainContext name -> Some name | _ -> None) |> Set.ofList
    let supplied =
        match Unifier.unalias scheme.Type, Unifier.unalias calleeTy with
        | TyFunc(general, _, generalContexts), TyFunc(input, _, _) ->
            // A scope the callee requires itself is the caller's, though its parameter's type has it too.
            let required = generalContexts |> List.choose (function FunctionContexts.ScopedContext(_, scope) -> Some scope | _ -> None)
            pairScopes general input
            |> List.filter (fun (name, generalScope, _) ->
                not (own.Contains name)
                && not (List.contains generalScope required)
                && match generalScope with
                   | TyVar v -> scheme.Quantified.Contains v.Id
                   | _ -> false)
            |> List.map (fun (name, _, scope) -> name, scope)
            |> List.distinctBy fst
        | _ -> []
    match supplied with
    | [] -> env, calleeTy, state
    | _ ->
        let state = MemoryCells.settleBlock env state
        let ownScopes, state =
            supplied
            |> List.choose (fun (context, scope) -> match scope with TyVar v -> Some (context, v.Id) | _ -> None)
            |> List.mapFold (fun (current: InferState) (context, id) ->
                (id, TyVar { Id = current.NextId; Name = Some context; Kind = CallScope }),
                { current with NextId = current.NextId + 1 }) state
        let state =
            { state with
                CallDepths =
                    ownScopes
                    |> List.fold (fun depths (_, scope) ->
                        match scope with
                        | TyVar v -> depths |> Map.add v.Id state.LambdaContexts.Length
                        | _ -> depths) state.CallDepths }
        let ownScopes = Map.ofList ownScopes
        let calleeTy = Unifier.apply ownScopes calleeTy
        let supplied = supplied |> List.map (fun (name, scope) -> name, Unifier.apply ownScopes scope)
        let env =
            match supplied |> List.tryFind (fst >> (=) MemoryCells.context) with
            | Some (_, scope) -> MemoryCells.aroundCall env state callee scope
            | None -> env
        // The scopes signatures write so far: a scope bound outside the call.
        let written = state.TypeVars |> Map.toList |> List.choose (function ScopeOf _, var -> Some (TyVar var) | _ -> None)
        // The names bound outside the call whose types the arguments may yet fill in: a cell, or a reference of
        // another block, that comes to hold what requires the call's scope outlives the call with it.
        let open' = env.Unquantified.Keys |> Seq.choose (fun name -> env.Names.TryFind name |> Option.map (fun scheme -> name, scheme)) |> List.ofSeq
        env,
        calleeTy,
        { state with
            SupplyingCalls = state.SupplyingCalls |> Map.add call.Id (supplied, written, open')
            OpenScopes = List.map snd supplied @ state.OpenScopes }

/// Whether the def `defName`, where it writes its type, has the scoped context `context` in its body: the type writes
/// it, or it takes a cell, for `Memory`. A parameter whose type requires the context doesn't give the body one: the def
/// that supplies it to such a parameter, as `memory` does, loads an instance with `use` to call it (#2101).
let private declares (state: InferState) (defName: Identifier) (context: Identifier) =
    let names (contexts: TypeExpr list) =
        contexts |> List.exists (fun ctx -> match ctx.Kind with TypeName name -> WrittenContexts.declaredScoped state name = context | _ -> false)
    let takesCell (signature: TypeExpr) =
        match signature.Kind with
        | TypeFunc(input, _) when context = MemoryCells.context ->
            TypeExpr.within input
            |> List.exists (fun typeExpr ->
                match typeExpr.Kind with
                | TypeApply(name, [ _ ]) -> name = CellPassingSyntax.typeName
                | _ -> false)
        | _ -> false
    match state.WrittenTypes |> Map.tryFind defName with
    | Some { Kind = TypeWithContext(contexts, signature) } -> names contexts || takesCell signature
    | Some { Kind = TypeContext contexts } -> names contexts
    | Some signature -> takesCell signature
    | None -> false

/// The error for `subject` (`'open'`, which `instead` says how to move: `call 'open'`) at `span` requiring the scoped
/// context `context` where nothing supplies it. Something does in a lambda, which requires the context where it is
/// called, and in a def's body, where the def writes the context or, writing no type, is inferred to require it (see
/// `LambdaContexts.record`). A top-level value and a script's statements never have one: only a call that supplies the
/// context, as `memory { ... }` does `Memory`. A custom pattern's body can't require one at all (see `PatternPurity`).
let private unsuppliedAt (state: InferState) (subject: string) (instead: string) (context: Identifier) (span: SourceSpan) =
    let supplier = if context = MemoryCells.context then "a `memory { ... }` block" else "a call that supplies it"
    if LambdaContexts.withinLambda state || state.EscapedContexts.Contains context then None
    else
        match state.ContextScope with
        | DefScope(defName, _) when PatternPurity.isPattern defName -> Some (PatternPurity.requires $"{subject} requires" context span)
        | DefScope(_, true) -> None
        | DefScope(defName, false) when declares state defName context -> None
        | DefScope(defName, false) ->
            Diagnostics.at DiagnosticCodes.UndeclaredContext span
                $"{subject} requires the scoped context '{context}', which '{defName}' does not declare: add [{context}] to the type of '{defName}', or {instead} inside {supplier}"
            |> Diagnostics.about context
            |> ContextFixes.withAddToType state defName context
            |> Some
        | ValueScope valueName ->
            Diagnostics.at DiagnosticCodes.UndeclaredContext span
                $"{subject} requires the scoped context '{context}', which the value '{valueName}' cannot have: {instead} inside {supplier}"
            |> Diagnostics.about context
            |> Some
        | AmbientScope
        | ScriptValueScope _ ->
            Diagnostics.at DiagnosticCodes.MissingContextMembers span
                $"{subject} requires the scoped context '{context}', which is not available here: {instead} inside {supplier}"
            |> Diagnostics.about context
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

/// Whether `ty`, as `resolve` resolves types, holds what requires the scope `scope` in a mutable object of another
/// scope, or in a value of a scoped type of another scope: something that outlives a call of `scope` and holds what
/// requires it.
let rec private holds (resolve: Ty -> Ty) (scope: Ty) (ty: Ty) =
    let ofOther (holderScope: Ty) (held: Ty list) =
        resolve holderScope <> scope && held |> List.exists (mentions scope)
    match ty with
    | FunctionContexts.CellType(held, holderScope) when ofOther holderScope [ held ] -> true
    | TyNominal(_, args, _, _) ->
        let scopes, held = args |> List.partition (function FunctionContexts.ScopedContext _ -> true | _ -> false)
        match scopes with
        | [ FunctionContexts.ScopedContext(_, holderScope) ] when ofOther holderScope held -> true
        | _ -> args |> List.exists (holds resolve scope)
    | _ -> Ty.exists (holds resolve scope) ty

/// The first of `bound`, names bound outside the call `call`, that comes to require its scope `scope`: a def's value
/// was made before the call started, so its type can't mention the scope, unless it is the def the call is in, whose
/// type is a variable until it is inferred. A lambda's parameter, or what a pattern binds, may: the function the call
/// is in supplies the scope to it in turn. Neither may hold what requires the scope in a mutable object or a value of a
/// scoped type of another scope (see `holds`), nor be a cell of another scope whose value requires it.
let private outlivingHolder (state: InferState) (resolve: Ty -> Ty) (scope: Ty) (bound: (Identifier * TypeScheme) list) =
    bound
    |> List.tryFind (fun (_, scheme) ->
        let ty = resolve scheme.Type
        let kind = scheme.Symbol |> Option.bind state.Symbols.TryFind |> Option.map _.Kind
        let isDefsValue =
            match kind, scheme.Type with
            | _, TyVar _ -> false
            | Some (Parameter | PatternVariable), _ -> false
            | _ -> true
        let isCellOfOther =
            scheme.Symbol
            |> Option.bind state.Cells.TryFind
            |> Option.exists (fun cellScope -> resolve cellScope <> scope)
        (isDefsValue && mentions scope ty) || (isCellOfOther && mentions scope ty) || holds resolve scope ty)

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
    | Some (supplied, written, bound) ->
        let opened = List.map snd supplied
        let state = { state with OpenScopes = state.OpenScopes |> List.filter (fun scope -> not (List.contains scope opened)) }
        let resolve = Unifier.applySolved (fst (Solving.solve state.Constraints))
        let callee = match call.Kind with FunctionCall(name, _, _) -> name | _ -> "the function"
        let result =
            match state.CalleeTypes |> Map.tryFind call.Id |> Option.map Unifier.unalias with
            | Some (TyFunc(_, output, _)) -> resolve output
            | _ -> TyPrimitive "unit"
        let outside = state.OpenScopes @ written |> List.map resolve
        let declaredOutside (symbol: SymbolId) =
            state.Symbols |> Map.tryFind symbol |> Option.forall (fun declared -> not (SourceSpan.contains call.Span declared.Declaration))
        // A def's own scope for its tasks, which the compiler wrote (see `TaskScopes`), is the def's, not a call's.
        let tasks (message: string -> string) =
            if callee = TaskScopeSyntax.scopeCall then
                fun through ->
                    $"A task can't leave the def that started it: what the def gives back holds one{through}. The def is the scope of its tasks, and waits for them before it returns"
            else message
        let errors =
            supplied
            |> List.choose (fun (context, scope) ->
                let scope = resolve scope
                if List.contains scope outside then
                    let found = introducer state resolve call scope declaredOutside
                    Some (escape call found (tasks (fun through ->
                        $"'{context}' of this '{callee}' call would reach outside it{through}, which is bound outside the call: only what is made inside the call can require it")))
                elif mentions scope result then
                    let found = introducer state resolve call scope (fun _ -> true)
                    let instead = if context = MemoryCells.context then ": return what the cells hold instead" else ""
                    Some (escape call found (tasks (fun through ->
                        $"The result of this '{callee}' call requires its '{context}'{through}, so it cannot leave the call{instead}")))
                else
                    match outlivingHolder state resolve scope bound with
                    | Some (name, scheme) ->
                        let found =
                            introducer state resolve call scope (fun symbol -> scheme.Symbol = Some symbol)
                            |> Option.map (fun (node, _) -> node, $"'{name}'")
                        Some (escape call found (tasks (fun through ->
                            $"'{context}' of this '{callee}' call would reach outside it{through}, which is bound outside the call and outlives it: only what is made inside the call can hold what requires it")))
                    | None -> None)
        if errors.IsEmpty then state
        else
            { state with
                FailedExprs = (call.Id, errors) :: state.FailedExprs
                EscapedContexts = supplied |> List.map fst |> Set.ofList |> Set.union state.EscapedContexts }

/// `ty` without what each function type in it requires where it is called.
let rec private withoutContexts (ty: Ty) : Ty =
    match ty with
    | TyFunc(input, output, _) -> TyFunc(withoutContexts input, withoutContexts output, [])
    | TyNominal(nominal, args, underlying, isPrivate) -> TyNominal(nominal, List.map withoutContexts args, underlying, isPrivate)
    | _ -> Ty.map withoutContexts ty

/// The instance `value`, a context's, that a `use` loads in `env`: the errors, if one of its members holds what belongs
/// to a call around the `use` that supplies a scoped context, such as a `memory` block's reference, which would leave
/// the call through the member's type in the context, written without that scope; and `env` with each member that
/// requires such a context where it is called, as one that changes the block's cells does, requiring it on its name,
/// so that naming it can't take it out of the call either.
let instanceInScope (env: TypeEnv) (state: InferState) (value: Expression) : Diagnostic list * TypeEnv =
    match value.Kind with
    | FunctionCall(name, _, args) when state.UseInstances.Contains value.Id ->
        let state = MemoryCells.settleBlock env state
        let resolve = Unifier.applySolved (fst (Solving.solve state.Constraints))
        // Each scope open around the `use`, with the context it is of.
        let openScopes =
            state.SupplyingCalls
            |> Map.values
            |> Seq.collect (fun (supplied, _, _) -> supplied)
            |> Seq.map (fun (context, scope) -> context, resolve scope)
            |> Seq.filter (fun (_, scope) -> state.OpenScopes |> List.exists (resolve >> (=) scope))
            |> Seq.distinct
            |> List.ofSeq
        let members =
            args
            |> List.collect (fun arg ->
                match arg.Kind with
                | RecordExpr fields -> fields |> List.choose (function NamedField(memberName, field) -> Some (Some memberName, field) | PositionalField _ -> None)
                | _ -> [ None, arg ])
            |> List.choose (fun (memberName, field) -> state.ExprTypes |> Map.tryFind field.Id |> Option.map (fun ty -> memberName, field, resolve ty))
        let errors =
            members
            |> List.choose (fun (_, field, ty) ->
                openScopes
                |> List.tryFind (fun (_, scope) -> mentions scope (withoutContexts ty))
                |> Option.map (fun (context, _) ->
                    Diagnostics.at DiagnosticCodes.ScopedContextEscapes field.Span
                        $"This member of the '{name}' instance holds what belongs to the '{context}' of a call around the `use`, which its type in '{name}' doesn't say, so it could leave the call through the context: give the member what it works on as an argument instead"))
        let env =
            members
            |> List.fold (fun current (memberName, _, ty) ->
                match memberName, Unifier.unalias ty with
                | Some memberName, TyFunc(_, _, contexts) ->
                    let required =
                        contexts
                        |> List.filter (function
                            | FunctionContexts.ScopedContext(_, scope) -> openScopes |> List.exists (snd >> (=) (resolve scope))
                            | _ -> false)
                    match required, TypeEnv.tryFind memberName current with
                    | _ :: _, Some ({ Type = TyFunc(input, output, own) } as scheme) ->
                        TypeEnv.extend memberName { scheme with Type = TyFunc(input, output, own @ required) } current
                    | _ -> current
                | _ -> current) env
        errors, env
    | _ -> [], env
