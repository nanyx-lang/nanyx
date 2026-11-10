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

    /// Checks `expr`, a `==` or `!=` (`op`) in `env` that compares two values of the type `operandTy` (given as
    /// `input`, as a call's arguments are typed) and calls no definition. Two lists whose elements have an `Eq` other
    /// than the compiler's own, which `instanceOf` finds, are compared through it (see
    /// `WellKnownContexts.throughElements`). A function is an error. A type with a variable left in it is checked once
    /// it is known (see `WellKnownContexts.pendingOperator`). The prelude's `builtInFunction` is the comparison itself,
    /// of any type.
    let require
        (instanceOf: InstanceOf)
        (env: TypeEnv)
        (state: InferState)
        (expr: Expression)
        (op: string)
        (operandTy: Ty)
        (input: Ty)
        : Result<InferState, Diagnostic list> =
        let ty = resolveSoFar state operandTy
        match throughElements equality instanceOf env state expr op input ty with
        | Error errors -> Error errors
        | Ok (Some called) -> Ok called
        | Ok None ->
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
