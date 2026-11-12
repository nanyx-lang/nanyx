namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// `a ?? b`: the value inside `a`'s `#some`, or `b` for any other tag. `b` runs only when it is needed, as the right of
/// `and` does: the operator is the call `__someOrElse(a, { b })` of a function of the prelude's, as `**` is a call (see
/// `ArithmeticOperators.inferPower`), by a name that is reserved, so that no definition a module makes changes what
/// the operator means. The prelude's `someOrElse` is the same function.
///
/// `??` only unwraps: a `b` that is itself option-like is an error that points at `orElseSome`, unless what is inside
/// `a`'s `#some` is option-like too. In a chain, `a ?? b ?? c` is `a ?? (b ?? c)`: `b` is option-like, and `c` plain.
module internal CoalesceOperator =
    /// The prelude function that `a ?? b` calls.
    let functionName = "__someOrElse"

    /// The tag whose value `??` unwraps.
    let private someTag = "some"

    /// The operands of `left ?? right`, when `kind` is one.
    let (|Call|_|) (kind: ExpressionKind) =
        match kind with
        | BinaryOp(op, _, left, right) when op = coalesceOperator -> Some (left, right)
        | _ -> None

    /// What `ty` holds in its `#some`, when it is option-like.
    let private someValue (ty: Ty) : Ty option =
        let valueOf item =
            match Unifier.unalias item with
            | TyTag(tag, value) when tag = someTag -> Some (value |> Option.defaultValue (TyPrimitive "unit"))
            | _ -> None
        match Unifier.unalias ty with
        | TyUnion items -> items |> List.tryPick valueOf
        | other -> valueOf other

    let private isOptionLike (ty: Ty) = (someValue ty).IsSome

    /// The error for `left ?? right` whose right operand is option-like, where the value inside the left's `#some` is
    /// not: `??` unwraps, so its right operand is the plain value. Each operand is inferred by `infer` on its own, in a
    /// state that is then dropped, only to read its type. A right operand that is itself a `??` is not looked at: it is
    /// checked as an operator of its own.
    let private optionLikeFallback
        (infer: InferState -> Expression -> Result<TypedExpr * InferState, Diagnostic list>)
        (state: InferState)
        (left: Expression)
        (right: Expression)
        : Diagnostic option =
        match right.Kind with
        | Call _ -> None
        | _ ->
            match infer state left |> Result.bind (fun (typedLeft, next) -> infer next right |> Result.map (fun (typedRight, last) -> typedLeft, typedRight, last)) with
            | Ok (typedLeft, typedRight, last) ->
                let rightTy = resolveSoFar last typedRight.Type
                let nested = someValue (resolveSoFar last typedLeft.Type) |> Option.exists (resolveSoFar last >> isOptionLike)
                if isOptionLike rightTy && not nested then
                    let message =
                        $"`??` only unwraps: its right operand is the value to use when the left is not `#some`, but this is '{Unifier.tyToString rightTy}', which is option-like. To fall back to another option, use `orElseSome`, as in `a \\orElseSome {{ b }}`"
                    Some (Diagnostics.at DiagnosticCodes.OptionLikeFallback right.Span message)
                else None
            | Error _ -> None

    /// `expr`, `left ?? right`, inferred by `infer` as the call `__someOrElse(left, { right })` that it is, and recorded
    /// in `ReadAs` to be written as it (see `ExpressionRewrites.writeAsRead`).
    let infer
        (infer: InferState -> Expression -> Result<TypedExpr * InferState, Diagnostic list>)
        (state: InferState)
        (expr: Expression)
        (left: Expression)
        (right: Expression)
        : Result<TypedExpr * InferState, Diagnostic list> =
        match optionLikeFallback infer state left right with
        | Some error -> Error [ error ]
        | None -> inferAsCall infer state expr functionName [ left; Expression.from right (Lambda([], right, false)) ]
