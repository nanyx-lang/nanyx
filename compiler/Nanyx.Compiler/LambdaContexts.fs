/// The contexts a lambda uses, which its type carries: `{ n -> println(n) }` is `[Console] int -> ()`. A lambda
/// requires them where it is called, as a def requires its own, so passing it where a pure function is expected is an
/// error, and passing it to a higher-order function with a context variable makes the call require them too.
///
/// While a lambda's body is inferred, each context that something in it requires where it runs is recorded for the
/// innermost lambda around it: a call to a function that requires the context or to one of its members, a call to a
/// function value whose type requires it, and what a call's context variables stand for. A lambda made inside another
/// is not run by it, so what it uses is its own. A context a `use` inside the lambda supplies is not one it uses: it
/// runs with that instance. A def's own body is not such a lambda: what it uses are the def's
/// contexts, checked as they always are. A parameterised context is found where the lambda is made, as a call finds it,
/// and the lambda's type does not carry it, unless the parameter the lambda is passed as gives it the instance (see
/// `Contexts.instancesSuppliedToLambda`).
///
/// The lambda is still checked where it is made, against the contexts there.
///
/// A local def that does not write its type and calls itself, as a `rec` def does, requires what its lambda uses. A call
/// to itself from that lambda uses nothing besides, and the context variable the call's type has stands for exactly
/// what the def requires.
module internal NyxCompiler.LambdaContexts

open Parser.Program
open NyxCompiler.InferState

/// `state` with `lambda` marked as the own body of the def `state.ContextScope` is in: its contexts are the def's, not
/// its type's.
let markBody (lambda: Expression) (state: InferState) : InferState =
    match lambda.Kind, state.ContextScope with
    | Lambda _, DefScope(defName, _) ->
        { state with
            BodyLambdas = state.BodyLambdas.Add lambda.Id
            BodyDepths = state.BodyDepths |> Map.add defName state.LambdaContexts.Length }
    | Lambda _, _ -> { state with BodyLambdas = state.BodyLambdas.Add lambda.Id }
    | _ -> state

/// Whether a lambda within the body of the def `state.ContextScope` is in is being inferred, which requires what is used
/// in it where it is called rather than the def.
let withinLambda (state: InferState) : bool =
    let depth =
        match state.ContextScope with
        | DefScope(defName, _) -> state.BodyDepths |> Map.tryFind defName |> Option.defaultValue 0
        | _ -> 0
    state.LambdaContexts.Length > depth

/// `state` as the body of `lambda` starts to be inferred: what it uses is recorded for it, unless it is a def's body.
let enter (lambda: Expression) (state: InferState) : InferState =
    if state.BodyLambdas.Contains lambda.Id then state
    else { state with LambdaContexts = [] :: state.LambdaContexts; LambdaStarts = (state.NextId, lambda.Span) :: state.LambdaStarts }

/// The scoped contexts the body of the unannotated top-level def `defName`, `lambda`, requires outside any lambda (see
/// `record`), but for the `Memory` of a cell it takes, which its type requires already (see
/// `FunctionContexts.withCellMemory`).
let private inferredScoped (defName: Identifier) (lambda: Expression) (state: InferState) : Ty list =
    let cellScopes =
        match lambda.Kind with
        | Lambda(args, _, _) ->
            args
            |> List.choose (fun (name, _) -> state.DeclarationSymbols |> Map.tryFind (lambda.Id, LambdaParameters, name))
            |> List.choose state.Cells.TryFind
            |> List.map (resolveSoFar state)
        | _ -> []
    state.InferredScopedContexts
    |> Map.tryFind defName
    |> Option.defaultValue []
    |> List.filter (function
        | FunctionContexts.ScopedContext(_, scope) -> not (List.contains (resolveSoFar state scope) cellScopes)
        | _ -> true)

/// `state` with the scoped contexts among `scoped`, those the lambda `depth` lambdas deep that started making type
/// variables at `start` uses, recorded as used by the lambda around it too, if their scope is one from outside the
/// lambda: what the lambda uses of it, it captures, and the lambda around makes it with what it captures. A scope made
/// inside the lambda is the lambda's own, and the scope of a call is given to the lambda the call is given, as a
/// `memory` block is, so it goes no further out than that.
let private capturedFrom (depth: int) (start: int) (scoped: Ty list) (state: InferState) : InferState =
    if scoped.IsEmpty || state.LambdaContexts.IsEmpty then state
    else
        let fromOutside (context: Ty) =
            match resolveSoFar state context with
            | FunctionContexts.ScopedContext(_, TyVar v) as resolved ->
                let outside =
                    match v.Kind, state.CallDepths |> Map.tryFind v.Id with
                    // Not into the lambda the call is given, which the call gives its scope.
                    | CallScope, Some callDepth -> callDepth < depth - 2
                    | CallScope, None -> false
                    | _ -> v.Id < start
                if outside then Some resolved else None
            | _ -> None
        match scoped |> List.choose fromOutside |> List.distinct, state.LambdaContexts with
        | [], _
        | _, [] -> state
        | captured, used :: outer -> { state with LambdaContexts = (List.rev captured @ used) :: outer }

/// The contexts `lambda`'s body used, each once in the order first used, and `state` as it is once the body has been
/// inferred. The body of an unannotated top-level def uses the scoped contexts it requires outside any lambda (see
/// `inferredScoped`): its type requires them, so its callers must supply them.
let leave (lambda: Expression) (state: InferState) : Ty list * InferState =
    match state.LambdaContexts, state.ContextScope with
    | used :: outer, _ when not (state.BodyLambdas.Contains lambda.Id) ->
        // Two scoped contexts may be of one scope, or of two calls': an inner `memory` block's lambda that changes an
        // outer block's reference requires the outer block's `Memory` besides its own, which the constraints say.
        let scoped = used |> List.filter (function FunctionContexts.ScopedContext _ -> true | _ -> false)
        let used = if scoped.Length > 1 then used |> List.map (resolveSoFar state) else used
        let start, starts = match state.LambdaStarts with (start, _) :: rest -> start, rest | [] -> System.Int32.MaxValue, []
        let state = { state with LambdaContexts = outer; LambdaStarts = starts }
        List.rev used |> List.distinct, capturedFrom (outer.Length + 1) start scoped state

    | _, DefScope(defName, true) -> inferredScoped defName lambda state, state
    | _ -> [], state

/// `state` with the scoped contexts among `contexts` recorded as required by the body of the unannotated top-level def
/// `defName`, each of one scope however often it is required: the scope its callers give it.
let private recordInferredScoped (defName: Identifier) (contexts: Ty list) (state: InferState) : InferState =
    contexts
    |> List.fold (fun current context ->
        match context with
        | FunctionContexts.ScopedContext(name, scope) ->
            let recorded = current.InferredScopedContexts |> Map.tryFind defName |> Option.defaultValue []
            match recorded |> List.tryPick (function FunctionContexts.ScopedContext(other, earlier) when other = name -> Some earlier | _ -> None) with
            | Some earlier -> addConstraint scope earlier current
            | None -> { current with InferredScopedContexts = current.InferredScopedContexts |> Map.add defName (recorded @ [ context ]) }
        | _ -> current) state

/// `state` with `contexts` recorded as used by the innermost lambda being inferred, if any. Outside any lambda, in the
/// body of an unannotated top-level def, a scoped context is the def's (see `recordInferredScoped`).
let record (contexts: Ty list) (state: InferState) : InferState =
    match state.LambdaContexts, state.ContextScope with
    // A context the lambda uses already is not recorded again: `leave` gives each once, and a lambda that makes many
    // calls requiring the same contexts, as `start!`s do, would hold them once for each call (#2551).
    | used :: outer, _ when not contexts.IsEmpty ->
        match contexts |> List.distinct |> List.filter (fun context -> not (List.contains context used)) with
        | [] -> state
        | added -> { state with LambdaContexts = (List.rev added @ used) :: outer }
    | [], DefScope(defName, true) -> recordInferredScoped defName contexts state
    | _ -> state

/// `after` without what the lambdas being inferred used since `before`: for an expression inferred again, as a pipe's
/// value is as its call's first argument, whose second inference records it all again.
let forgetSince (before: InferState) (after: InferState) : InferState =
    { after with LambdaContexts = before.LambdaContexts; InferredScopedContexts = before.InferredScopedContexts }

/// The variables of the contexts the lambdas being inferred use, as `resolve` resolves them. They are those lambdas',
/// so a local def that holds a value of one's scope, as a value of a type in a scoped context's scope is, is of that
/// scope and is not generalised over it.
let usedVars (resolve: Ty -> Ty) (state: InferState) : Set<int> =
    state.LambdaContexts |> List.concat |> List.map (resolve >> TypeEnv.freeTyVars) |> Set.unionMany

/// `state` with `value`, the instance a `use` loads, noted as loaded inside the lambdas being inferred now.
let enterUse (value: Expression) (state: InferState) : InferState =
    { state with UseDepths = state.UseDepths |> Map.add value.Id state.LambdaContexts.Length }

/// Whether a `use` inside the innermost lambda being inferred supplies the context `ctx` in `env`: it binds every
/// member of it there, or, for a parameterised context, the nearest instance of it at its types is the `use`'s. The
/// lambda runs with that instance, so it does not require the context.
let private suppliedWithin (env: TypeEnv) (state: InferState) (ctx: Ty) : bool =
    let depth = state.LambdaContexts.Length
    let usedWithin (provider: ContextProvider option) =
        match provider with
        | Some (FromUse value) -> state.UseDepths |> Map.tryFind value |> Option.exists (fun loaded -> loaded >= depth)
        | _ -> false
    let loadedWithin memberName = TypeEnv.tryFind memberName env |> Option.bind _.ProvidedBy |> usedWithin
    match resolveSoFar state ctx with
    | FunctionContexts.PlainContext name ->
        match state.TypeDefs |> Map.tryFind name with
        | Some info when info.IsContext && not info.Members.IsEmpty -> info.Members |> Map.forall (fun memberName _ -> loadedWithin memberName)
        | _ -> false
    | TyApply(_, _ :: _) as instance ->
        env.Instances
        |> List.tryFind (fun binding -> resolveSoFar state binding.Context = instance)
        |> Option.exists (fun binding -> usedWithin (Some binding.Provider))
    | _ -> false

/// `state` where the nearest instance of the parameterised context `ctx` in `env` is a `use`'s inside the innermost
/// lambda whose type arguments are still open and fit `ctx`'s: `use Emit(emit = { y -> found += y }) in block()`, in a
/// builder that calls a `block` of type `[c, Emit(b)] () -> ()`, loads the `Emit(b)` the call requires, as writing
/// `use Emit(b)(...)` would (#2550). `None` when there is no such `use`.
let private settleUseWithin (env: TypeEnv) (state: InferState) (ctx: Ty) : InferState option =
    let depth = state.LambdaContexts.Length
    match resolveSoFar state ctx with
    | TyApply(ctxName, (_ :: _ as argTys)) ->
        env.Instances
        |> List.tryFind (fun binding -> match resolveSoFar state binding.Context with TyApply(name, _) -> name = ctxName | _ -> false)
        |> Option.bind (fun binding ->
            match binding.Provider, resolveSoFar state binding.Context with
            | FromUse value, TyApply(_, instanceArgs) when
                state.UseDepths |> Map.tryFind value |> Option.exists (fun loaded -> loaded >= depth)
                && instanceArgs.Length = argTys.Length
                && instanceArgs |> List.exists (TypeEnv.freeTyVars >> Set.isEmpty >> not) ->
                let pairs = List.zip instanceArgs argTys
                match Solving.unify ((pairs |> List.map (fun (instanceArg, argTy) -> instanceArg, argTy, Equal, SourceSpan.none)) @ Solving.agreeing state.Constraints) with
                | Ok _ -> Some (pairs |> List.fold (fun current (instanceArg, argTy) -> addConstraint instanceArg argTy current) state)
                | Error _ -> None
            | _ -> None)
    | _ -> None

/// `record` for the contexts among `contexts` that no `use` inside the innermost lambda supplies in `env`, settling the
/// open instance of one that loads a context's instance at types still open (see `settleUseWithin`).
let recordUnsupplied (env: TypeEnv) (contexts: Ty list) (state: InferState) : InferState =
    let unsupplied, state =
        contexts
        |> List.fold (fun (unsupplied, current) ctx ->
            if suppliedWithin env current ctx then unsupplied, current
            else
                match settleUseWithin env current ctx with
                | Some settled -> unsupplied, settled
                | None -> unsupplied @ [ ctx ], current) ([], state)
    record unsupplied state

/// `state` as the value of an unannotated local def starts to be inferred, `selfTy` being the variable the def is bound
/// at meanwhile. The first lambda inferred within the value is the def's own: `{ ... }` in `rec loop = { ... }`.
let enterSelf (selfTy: Ty) (state: InferState) : InferState =
    match selfTy with
    | TyVar v -> { state with SelfTypes = state.SelfTypes |> Map.add v.Id { Depth = state.LambdaContexts.Length + 1; Calls = [] } }
    | _ -> state

/// Whether a call to a function value of type `calleeTy` is a local def calling itself from its own lambda, not from one
/// made within it, which uses what the call requires as it would for any other call.
let isSelfCall (calleeTy: Ty) (state: InferState) : bool =
    match Unifier.unalias calleeTy with
    | TyVar v -> state.SelfTypes |> Map.tryFind v.Id |> Option.exists (fun self -> self.Depth = state.LambdaContexts.Length)
    | _ -> false

/// `state` with `contexts`, those of the type of a call to a function value of type `calleeTy`, noted as a call of a
/// local def to itself if it is one.
let recordSelfCall (calleeTy: Ty) (contexts: Ty list) (state: InferState) : InferState =
    match Unifier.unalias calleeTy with
    | TyVar v ->
        match state.SelfTypes |> Map.tryFind v.Id with
        | Some self -> { state with SelfTypes = state.SelfTypes |> Map.add v.Id { self with Calls = self.Calls @ contexts } }
        | None -> state
    | _ -> state

/// `state` once the value of the local def bound at `selfTy` (see `enterSelf`) has been inferred as `valueTy`: each of
/// its calls to itself requires the contexts the def does, no more.
let leaveSelf (selfTy: Ty) (valueTy: Ty) (state: InferState) : InferState =
    match selfTy with
    | TyVar v ->
        let calls = state.SelfTypes |> Map.tryFind v.Id |> Option.map _.Calls |> Option.defaultValue []
        let state = { state with SelfTypes = state.SelfTypes.Remove v.Id }
        match Unifier.unalias valueTy with
        | TyFunc(_, _, own) -> calls |> List.fold (fun current call -> addConstraint call (TyUnion own) current) state
        | _ -> state
    | _ -> state

/// `state` with the contexts a lambda of type `lambdaTy` requires recorded as used: a lambda run where it is written,
/// as a `for` loop's body is. A context a `use` around it supplies in `env`, inside the lambda it is run in, is not used
/// (see `recordUnsupplied`).
let recordRunHere (env: TypeEnv) (lambdaTy: Ty) (state: InferState) : InferState =
    match Unifier.unalias lambdaTy with
    | TyFunc(_, _, contexts) -> recordUnsupplied env contexts state
    | _ -> state

/// `state` with the plain contexts among `written`, contexts as a def's type writes them, recorded as used (see
/// `recordUnsupplied`). A parameterised context, or one written by its members, is found where the lambda is made. A
/// scoped context is of the scope the callee's type has where it is called, so that is where it is recorded (see
/// `ScopedCalls.enter`).
let recordWritten (env: TypeEnv) (written: TypeExpr list) (state: InferState) : InferState =
    let unscoped = written |> List.filter (fun ctx -> match ctx.Kind with TypeName name -> not (WrittenContexts.isScoped state name) | _ -> true)
    let plain, next = WrittenContexts.plain state (TyPrimitive "unit") unscoped
    recordUnsupplied env plain next

/// The context whose member `name`, as `env` binds it, is: of the ambient instance, of one a def around declares, or of
/// one a `use` of a value of the context's type loads (`use Foo(bar = ...)`). `None` for any other name, including a
/// member of a record written out that a `use` loads, and for a member of a parameterised context.
let contextOfMember (env: TypeEnv) (state: InferState) (name: Identifier) : Identifier option =
    match TypeEnv.tryFind name env |> Option.bind _.Symbol with
    | None -> None
    | Some symbol ->
        match state.AmbientMembers |> Map.tryFind name with
        | Some (ambient, ctxName) when ambient = symbol -> Some ctxName
        | _ ->
            state.TypeDefs
            |> Map.tryPick (fun ctxName info ->
                if info.IsContext && info.Parameters.IsEmpty && info.Members |> Map.exists (fun _ id -> id = symbol) then Some ctxName
                else None)
            |> Option.map (WrittenContexts.canonical state)

/// The scoped contexts whose scope `ty` holds: each one a function type in it requires, each one a value of a scoped
/// type in it belongs to, and `Memory` of each cell's.
let rec private scopedIn (ty: Ty) : Ty list =
    match ty with
    | FunctionContexts.ScopedContext _ -> [ ty ]
    | FunctionContexts.CellType(held, scope) -> FunctionContexts.scoped FunctionContexts.cellContext scope :: scopedIn held
    | TyNominal(_, args, _, _) -> args |> List.collect scopedIn
    | _ -> Ty.fold (fun found child -> found @ scopedIn child) [] ty

/// `state` once the value `symbol`, of the type `ty` where it is named, is named inside the innermost lambda: a value
/// bound outside it, in a block or by a lambda around, that holds what belongs to a scope, such as a block's reference, a lambda over a
/// block's cells, or a value of a scoped type, is captured by a lambda that names it, which requires the scope's context
/// as one that reads it does (see `leave`). A top-level value never holds one.
let recordNamed (symbol: Symbol option) (ty: Ty) (state: InferState) : InferState =
    match symbol with
    | Some ({ Kind = LocalValue | Parameter | PatternVariable } as symbol)
        when state.LambdaStarts |> List.tryHead |> Option.exists (fun (_, span) -> not (SourceSpan.contains span symbol.Declaration)) ->
        match scopedIn ty |> List.distinct with
        | [] -> state
        | scoped -> record scoped state
    | _ -> state
