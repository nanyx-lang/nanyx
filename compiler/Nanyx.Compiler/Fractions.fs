namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// The type `x / y` gives on a built-in number type, its `fraction`, `Num`'s associated type (see "Num(a)" on the docs'
/// Contexts page): `float` on `int`, the sized integers and `float`, and `F32` on `F32`. A `/` whose operands' type is
/// still a variable where it is read gives a variable of its own, pending until that type settles (`settle`), or, in an
/// unannotated top-level def generic in it, until the def requires `Num` of it, whose `fraction` the variable then is
/// (see `ArithmeticInference.require`).
module internal Fractions =
    /// The prelude's context that `fraction` is an associated type of.
    let context = "Num"

    /// `Num`'s associated type that `/` gives.
    let associatedType = "fraction"

    /// What `/` gives on the number type `ty`, if `ty` is one.
    let ofNumber (ty: Ty) : Ty option =
        match Unifier.unalias ty with
        | TyPrimitive "F32" as f32 -> Some f32
        | TyPrimitive("int" | "float")
        | SizedNumberType _ -> Some (TyPrimitive "float")
        | _ -> None

    /// The type `/` at `at` gives on operands of the type `operandTy`, and `state` with it pending where that is not
    /// known yet. A decimal literal's own variable is known enough: it is a `float` or an `F32`, each its own fraction.
    /// An integer literal's is not: it may be an integer type, whose fraction is `float`, or a type variable of the def
    /// that `fromInt` gives it as.
    let resultOf (state: InferState) (at: SourceSpan) (operandTy: Ty) : Ty * InferState =
        let resolved = resolveSoFar state operandTy
        match ofNumber resolved, NumericLiterals.writtenLiteral state resolved with
        | Some ty, _ -> ty, state
        | None, Some (FloatLit _) -> resolved, state
        | None, _ ->
            let ty, next = freshVar state
            ty, { next with PendingFractions = { OperandTy = operandTy; ResultTy = ty; At = at } :: next.PendingFractions }

    /// `state` with each pending `/` whose operands are now of a number type given that type's fraction, and those whose
    /// operands are still of one variable given one fraction, as an instance of `Num` gives one.
    let settle (state: InferState) : InferState =
        if state.PendingFractions.IsEmpty then state
        else
            let numbers, pending =
                state.PendingFractions
                |> List.map (fun pending -> pending, ofNumber (resolveSoFar state pending.OperandTy))
                |> List.partition (snd >> Option.isSome)
            let pending = List.map fst pending
            let settled =
                numbers
                |> List.fold
                    (fun current (pending, fraction) -> addConstraintAt pending.At pending.ResultTy fraction.Value current)
                    { state with PendingFractions = pending }
            pending
            |> List.groupBy (fun pending -> resolveSoFar settled pending.OperandTy)
            |> List.fold (fun current (_, onOne) ->
                match onOne with
                | first :: others -> others |> List.fold (fun next other -> addConstraintAt other.At other.ResultTy first.ResultTy next) current
                | [] -> current) settled

    /// `NumericLiterals.settle`, but for a literal of the type a pending `/` gives, which is settled after the others and
    /// the fractions they make: the `1s` of `10m / 2s / 1s` is of the quotient's `fraction`, a `float` once `10m` and `2s`
    /// are `int`, and defaulted with them it would be an `int` (#3134).
    let settleLiterals (isOwn: Ty -> bool) (state: InferState) : InferState =
        let results =
            state.PendingFractions
            |> List.choose (fun pending -> match resolveSoFar state pending.ResultTy with TyVar v -> Some v.Id | _ -> None)
            |> Set.ofList
        // Resolving every literal costs as much as there are, so a def with no `/` pending looks at none of them.
        let ofResult, others =
            if results.IsEmpty then [], state.UnsettledLiterals
            else
                state.UnsettledLiterals
                |> List.partition (fun literal -> match resolveSoFar state literal.Type with TyVar v -> results.Contains v.Id | _ -> false)
        if ofResult.IsEmpty then NumericLiterals.settle isOwn state
        else
            let settled = NumericLiterals.settle isOwn { state with UnsettledLiterals = others } |> settle
            NumericLiterals.settle isOwn { settled with UnsettledLiterals = ofResult @ settled.UnsettledLiterals }

    /// `state` with each number literal in `tys` of its default type (see `NumericLiterals.settleIn`), and so with each
    /// literal of the operands of a pending `/` that gives a variable in `tys`, which is then settled as far as that
    /// lets it (see `settle`): `println(1s / 1ms)` finds `ToString` of the `float` the literals' `int` divides to.
    /// `None` when there is nothing to settle.
    let settleIn (tys: Ty list) (state: InferState) : InferState option =
        let inTys = tys |> List.map (resolveSoFar state >> TypeEnv.freeTyVars) |> Set.unionMany
        let operands =
            state.PendingFractions
            |> List.filter (fun pending ->
                match resolveSoFar state pending.ResultTy with
                | TyVar v -> inTys.Contains v.Id
                | _ -> false)
            |> List.map _.OperandTy
        NumericLiterals.settleIn (tys @ operands) state |> Option.map (fun settled -> if operands.IsEmpty then settled else settle settled)
