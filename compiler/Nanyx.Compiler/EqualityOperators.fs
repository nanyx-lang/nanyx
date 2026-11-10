namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState
open NyxCompiler.WellKnownContexts

/// `==` and `!=` through the well-known context `Eq` (see `WellKnownContexts`). Comparing two values of the type `T`
/// needs an `Eq(T)`. Every type that holds no function has the compiler's own, which compares by structure, so only
/// two things are checked: a function is never compared, and a function that compares values of one of its own type
/// variables requires `Eq` of it. One that does not write its type has the requirement inferred, as it is for a call
/// to a function that requires a context (see `ContextInference`), and its `==` then calls the member of the instance
/// it is passed.
module internal EqualityOperators =
    /// The prelude's context.
    let context = equality.Context

    /// The context's one member, and the operator that reads it. `!=` is not a member: it is always `not (a == b)`.
    let equals = equality.Member

    let notEquals = "!="

    /// The prelude function that compares two values by their structure: the member of the compiler's own instance,
    /// where one is passed to a function that requires `Eq`.
    let builtInFunction = equality.BuiltInFunction

    /// Whether `op` is `==` or `!=`.
    let isOperator (op: string) = equality.Operators |> List.contains op

    /// Whether comparing values of the types `left` and `right` (resolved as far as they can be) joins them into one
    /// union rather than making them equal: when either is a tag or a union of tags, so that `o == #none` compares
    /// at `#none | r` and leaves `o` open to other tags.
    let joinsOperands (left: Ty) (right: Ty) =
        let isTags (ty: Ty) =
            match Unifier.unalias ty with
            | TyTag _
            | TyUnion _ -> true
            | _ -> false
        isTags left || isTags right

    /// Checks `expr`, a `==` or `!=` (`op`) in `env` that compares two values of the type `operandTy` and calls no
    /// definition. A function is an error there. A type with a variable left in it is checked once it is known (see
    /// `WellKnownContexts.pendingOperator`). The prelude's `builtInFunction` is the comparison itself, of any type.
    let require (env: TypeEnv) (state: InferState) (expr: Expression) (op: string) (operandTy: Ty) : Result<InferState, Diagnostic list> =
        let ty = resolveSoFar state operandTy
        match equality.Lacks ty, state.ContextScope with
        | Some reason, _ -> Error (error expr.Span (cannotUse equality op ty reason))
        | None, DefScope(name, false) when name = builtInFunction -> Ok state
        | None, _ when (comparedVars ty).IsEmpty || not (isDeclared equality state) -> Ok state
        | None, _ -> Ok (pendingOperator equality env state expr op ty)

    /// Whether `pending` is a comparison `require` left to be checked, rather than a call.
    let isPending (pending: PendingContextInstance) = pending.Context = context && isOperator pending.Callee

    /// Checks the comparison `pending` now that it compares values of the type `argTys` (see
    /// `WellKnownContexts.checkPending`).
    let resolvePending
        (hasInstance: TypeEnv -> Ty -> bool)
        (defScheme: Identifier -> TypeScheme option)
        (state: InferState)
        (pending: PendingContextInstance)
        (argTys: Ty list)
        : Result<ContextInstance option * InferState, Diagnostic list> =
        checkPending equality hasInstance defScheme state pending argTys

    /// `state` with each comparison among `pendings` that found the instance its def requires (see `ContextInference`)
    /// calling that instance's `==`, as an operator whose name is bound does (see `OperatorCalls`). `inputOf` is the
    /// input type of a function that takes two values of a type.
    let callMembers (inputOf: Ty -> Ty) (pendings: PendingContextInstance list) (state: InferState) : InferState =
        callOperators (function InstanceInScope _ -> true | _ -> false) false (fun _ -> equals) inputOf pendings state

    // Local functions.
    //
    // A local function requires no contexts: the backend compiles it again at each type it is called with, so its
    // comparisons are the compiler's own at that type. A call to one that compares values of a type variable it is
    // generic in still needs an `Eq` of the type that variable is at the call, or the backend would be left to compare
    // functions.

    /// The type variables among `quantified`, those a local function was just generalised in, that the comparisons and
    /// calls requiring `Eq` in `state` compare values of. Each is still pending, as none of its types was known.
    let comparedByLocal (state: InferState) (quantified: Set<int>) : Set<int> =
        state.PendingContextInstances
        |> List.filter (fun pending -> pending.Context = context)
        |> List.collect (fun pending -> pending.ArgTys |> List.collect (resolveSoFar state >> comparedVars))
        |> List.map _.Id
        |> List.filter quantified.Contains
        |> Set.ofList

    /// Checks `call`, at `span` in `env`, to the local function `calleeName`, which compares values of the type `ty`
    /// there, and finds `Eq(ty)` as `instance`. A type with no equality is an error at the call. A type with a
    /// variable left in it is checked as a call to a top-level function would be, once the def around it is inferred
    /// (see `WellKnownContexts.resolveCall`), but the call is passed nothing (`ChecksOnly`).
    let checkLocalCall
        (env: TypeEnv)
        (state: InferState)
        (call: Expression)
        (calleeName: Identifier)
        (span: SourceSpan)
        (ty: Ty)
        (instance: CallInstance)
        : Result<InferState, Diagnostic list> =
        let pendingIn (def: Identifier) (v: TyVar) (annotatedDef: Identifier option) (state: InferState) =
            let pending =
                { Def = def; Call = call.Id; Callee = calleeName; Index = 0; Count = 1
                  Context = context; ArgTys = [ ty ]; Span = span; Errors = error span (openAtCall equality calleeName ty v annotatedDef)
                  ChecksOnly = true }
            Ok { state with PendingContextInstances = pending :: state.PendingContextInstances }
        match instance, state.ContextScope with
        | NoInstance reason, _ -> Error (error span (noInstanceAtCall equality calleeName ty reason))
        | BuiltIn, _
        | Attached, _ -> Ok state
        | Open v, DefScope(defName, true) -> pendingIn defName v None state
        | Open v, scope ->
            let annotatedDef = match scope with DefScope(defName, _) -> Some defName | AmbientScope -> None
            pendingIn InterpolationHoles.moduleEnd v annotatedDef { state with HoleScopes = state.HoleScopes |> Map.add call.Id (env, annotatedDef) }
