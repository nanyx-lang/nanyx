namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState
open NyxCompiler.WellKnownContexts

/// `==` and `!=` through the well-known context `Eq` (see `WellKnownContexts`). Comparing two values of the type `T`
/// needs an `Eq(T)`. Every type that holds no function has the compiler's own, which compares by structure, so only
/// two things are checked: a function is never compared, and a function that compares values of one of its own type
/// variables requires `Eq` of it. One that does not write its type has the requirement inferred, as it is for a call
/// to a function that requires a context (see `ContextInference`), and its `==` then calls the member of the instance
/// it is passed. A function that compares values of a union with a rest, `o == #none` on an `#none | r`, needs `Eq` of
/// whatever the rest is at each call to it, which is checked there but not passed (see `WellKnownContexts.comparedRests`).
/// A structure with a part that has an `==` of its own is compared by a definition derived from its parts (see
/// `StructuralInstances`).
module internal EqualityOperators =
    /// The prelude's context.
    let context = equality.Context

    /// The context's one member, and the operator that reads it. `!=` is not a member: it is always `not (a == b)`.
    let equals = equality.Member

    let notEquals = "!="

    /// The prelude function that compares two values by their structure: the member of the compiler's own instance,
    /// where one is passed to a function that requires `Eq`.
    let builtInFunction = equality.BuiltInFunction

    /// The prelude function the derived `==` of a list calls, with the two lists and what compares two elements.
    let listFunction = "__listEqualsBy"

    /// The prelude function the derived `==` of a map calls, with the two maps and what compares two values.
    let mapFunction = "__mapEqualsBy"

    /// The prelude function the derived `==` of a set calls, with the two sets and what compares two elements.
    let setFunction = "__setEqualsBy"

    /// The error for a call to `calleeName` that requires `Eq` of the type `ty`, which has type variables in it and
    /// holds a map or a set: only the `==` derived for it compares a map, and a call cannot be passed that yet.
    let collectionAtGenericCall (calleeName: Identifier) (ty: Ty) =
        $"'{calleeName}' compares values of type '{display ty}', which holds a map or a set and so is compared by an == the compiler derives from its parts, but it cannot yet pass a function that == for a type with type variables in it"

    /// The error for a call to `calleeName`, which compares values of the type `ty`, which holds a map or a set, but is
    /// passed nothing to compare them with (see `WellKnownContexts.checkLocalCall`): it compares with the compiler's
    /// own `==`, which cannot compare a map.
    let collectionPassedNothing (calleeName: Identifier) (ty: Ty) =
        $"'{calleeName}' compares values of type '{display ty}' with the compiler's own ==, which cannot compare a map or a set yet: a local function, or a function that compares a union's rest, is passed no other"

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
    /// `WellKnownContexts.throughElements`). A function is an error. A type with a variable left in it, a union's rest
    /// among them, is checked once it is known (see `WellKnownContexts.pendingOperator`). The prelude's `builtInFunction` is the comparison itself,
    /// of any type.
    ///
    /// Any other structure with a part that has an `==` of its own (`comparesParts`) is compared by a derived
    /// definition (see `StructuralInstances`), which `derive` makes the comparison call once nothing of the type is
    /// left open.
    let require
        (instanceOf: InstanceOf)
        (comparesParts: Ty -> bool)
        (derive: Ty -> InferState -> InferState option)
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
            | None, _ when not (isDeclared equality state) -> Ok state
            | None, _ when (TypeEnv.freeTyVars ty).IsEmpty -> Ok (derive ty state |> Option.defaultValue state)
            | None, _ when (checkedVars ty).IsEmpty && not (comparesParts ty) -> Ok state
            | None, _ -> Ok (pendingOperator equality env state expr op ty)

    /// Whether `pending` is a comparison `require` left to be checked, rather than a call.
    let isPending (pending: PendingContextInstance) = pending.Context = context && isOperator pending.Callee

    /// Checks the comparison `pending` now that it compares values of the type `argTys` (see
    /// `WellKnownContexts.checkPending`). One that passes is the compiler's own, unless `derive` makes it call a
    /// derived definition (see `require`).
    let resolvePending
        (hasInstance: TypeEnv -> Ty -> bool)
        (derive: Ty -> InferState option)
        (defScheme: Identifier -> TypeScheme option)
        (state: InferState)
        (pending: PendingContextInstance)
        (argTys: Ty list)
        : Result<ContextInstance option * InferState, Diagnostic list> =
        match checkPending equality hasInstance defScheme state pending argTys with
        | Ok (None, checkedState) when checkedState.FailedExprs.Length = state.FailedExprs.Length ->
            Ok (None, derive (List.head argTys) |> Option.defaultValue checkedState)
        | result -> result

    /// `state` with each comparison among `pendings` that found the instance its def requires (see `ContextInference`)
    /// calling that instance's `==`, as an operator whose name is bound does (see `OperatorCalls`). `inputOf` is the
    /// input type of a function that takes two values of a type.
    let callMembers (inputOf: Ty -> Ty) (pendings: PendingContextInstance list) (state: InferState) : InferState =
        callOperators (function InstanceInScope _ -> true | _ -> false) false (fun _ -> equals) inputOf pendings state
