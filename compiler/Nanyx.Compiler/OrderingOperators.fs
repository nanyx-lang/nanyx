namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState
open NyxCompiler.WellKnownContexts

/// `<`, `<=`, `>` and `>=` through the well-known context `Ord` (see `WellKnownContexts`), whose one member is
/// `compare`. Ordering two values of the type `T` needs an `Ord(T)`, found as a call finds a context: an instance in
/// scope (a `use`, or the type of the function around the operator), then the `compare` attached to the type, then
/// the compiler's own. The compiler's own orders numbers, bools, strings, and tuples and lists of them, and is the
/// operator itself: floats follow IEEE 754, so every comparison with NaN is false. Any other instance is called: the
/// operator calls the prelude function that reads it (`__less` for `<`), which is passed the instance, so the four
/// operators cannot disagree with `compare` or with each other.
///
/// A function that orders values of one of its own type variables requires `Ord` of it. One that does not write its
/// type has the requirement inferred, as it is for a call to a function that requires a context (see
/// `ContextInference`).
module internal OrderingOperators =
    /// The prelude's context.
    let context = ordering.Context

    /// Whether `op` is one of the four ordering operators.
    let isOperator (op: string) = ordering.Operators |> List.contains op

    /// The prelude function each operator calls when it reads an instance other than the compiler's own.
    let private helpers = Map.ofList [ "<", "__less"; "<=", "__lessOrEqual"; ">", "__greater"; ">=", "__greaterOrEqual" ]

    /// The prelude function the ordering operator `op` calls to read its instance.
    let private callee (op: string) = helpers.[op]

    /// The instance of `Ord` of a type, in an environment, other than the compiler's own, if it has one: one in scope,
    /// or one built from the `compare` attached to it. Building one can fail, when the attached `compare` has the
    /// wrong type.
    type InstanceOf = TypeEnv -> InferState -> Ty -> Result<ContextInstance option * InferState, Diagnostic list>

    /// `state` with `expr`, the operator `op`, calling its prelude function (see `callee`) with `instance`. The
    /// function takes the operands (`input`, as a call's arguments are typed) and gives a bool.
    let private callWith (state: InferState) (expr: Expression) (op: string) (input: Ty) (instance: ContextInstance) =
        { state with
            CalleeTypes = state.CalleeTypes |> Map.add expr.Id (TyFunc(input, TyPrimitive "bool"))
            OperatorCalls = state.OperatorCalls |> Map.add expr.Id (callee op)
            ContextInstances = state.ContextInstances |> Map.add expr.Id [ Some instance ] }

    /// Whether `ty` is a record or a tag, which only a key or a `compare` of its own can order.
    let rec private isRecordOrTag (ty: Ty) =
        match ty with
        | TyAlias(_, _, underlying)
        | TyNominal(_, underlying, _) -> isRecordOrTag underlying
        | TyRecord fields -> not (isTuple fields)
        | TyOpenRecord _
        | TyTag _
        | TyUnion _ -> true
        | _ -> false

    /// What to do about ordering values of `ty`, a record or a tag, with an operator.
    let private advice (ty: Ty) =
        match ty with
        | TyNominal(name, _, _) when isRecordOrTag ty -> $". Attach '{name}.compare', or order by a key with List.sortBy"
        | _ when isRecordOrTag ty -> ". Make it a nominal type to attach a 'compare' to it, or order by a key with List.sortBy"
        | _ -> ""

    /// Checks `expr`, the operator `op` in `env` on two values of the type `operandTy`, given as `input` (as a call's
    /// arguments are typed). An instance `instanceOf` finds is called. Otherwise the operator is the compiler's own:
    /// a type with no order is an error there, and a type with a variable left in it is checked once it is known (see
    /// `resolvePending`).
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
        match instanceOf env state ty with
        | Error errors -> Error errors
        | Ok (Some instance, next) -> Ok (callWith next expr op input instance)
        | Ok (None, next) ->
            match ordering.Lacks ty with
            | Some reason -> Error (error expr.Span (cannotUse ordering op ty reason + advice ty))
            | None when (comparedVars ty).IsEmpty || not (isDeclared ordering next) -> Ok next
            | None -> Ok (pendingOperator ordering env next expr op ty)

    /// Whether `pending` is an operator `require` left to be checked, rather than a call.
    let isPending (pending: PendingContextInstance) = pending.Context = context && isOperator pending.Callee

    /// Finds the instance the operator `pending` reads, now that it is on values of the type `argTys`, when that is
    /// not one of the variables of the def it is in: one `instanceOf` finds where the operator is written, or else
    /// the compiler's own, which is checked as `WellKnownContexts.checkPending` says.
    let resolvePending
        (instanceOf: InstanceOf)
        (hasInstance: TypeEnv -> Ty -> bool)
        (defScheme: Identifier -> TypeScheme option)
        (state: InferState)
        (pending: PendingContextInstance)
        (argTys: Ty list)
        : Result<ContextInstance option * InferState, Diagnostic list> =
        let env, _, _ = operatorScope defScheme state pending
        match instanceOf env state (List.head argTys) with
        | Ok (None, _) -> checkPending ordering hasInstance defScheme state pending argTys
        | found -> found

    /// `state` with each operator among `pendings` that found an instance (see `ContextInference`) calling its prelude
    /// function with it, as `require` has one it finds at once. `inputOf` is the input type of a function that takes
    /// two values of a type.
    let callHelpers (inputOf: Ty -> Ty) (pendings: PendingContextInstance list) (state: InferState) : InferState =
        callOperators (fun _ -> true) true (fun pending -> callee pending.Callee) inputOf pendings state
