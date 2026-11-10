namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// Operators that call a definition. A type gets an operator by attaching it, `` def $Vector.`+` = ... ``, and then
/// `v + w` on a `$Vector` calls it. Where an operator's name is bound, as `` `+` `` is in a function that requires
/// `[Sum(a)]`, `x + y` calls that definition with its two operands, so generic code can add any type that has a
/// `+`. On a built-in type the operator is still the built-in one, whatever is in scope, so `count + 1` in that
/// same function adds ints.
///
/// `!=` has no definition of its own: it is `not (a == b)`, so it calls what `==` is bound to. Either calls it only
/// for operands the definition takes: in a function that requires `[Eq(a)]`, `==` on two `a`s calls the context's
/// member, and on two `list(a)`s is the built-in one (see `EqualityOperators`).
module internal OperatorCalls =
    /// Whether `scheme`, what `==` is bound to, compares values of the type `operandTy` (resolved as far as it can
    /// be), given as `input` (as a call's arguments are typed). The member of a context required at a type variable,
    /// `Eq(a)`, compares values of that variable, not of a type built from it, which unifying the two would not
    /// tell: an annotation's variables are checked to have stayed variables only once the def is inferred. Any other
    /// definition compares what a call to it with `input` would check for, as far as the constraints gathered so far
    /// tell.
    let private compares (state: InferState) (operandTy: Ty) (input: Ty) (scheme: TypeScheme) =
        let parameter =
            match Unifier.unalias (resolveSoFar state scheme.Type) with
            | TyFunc(TyRecord fields, _) -> fields |> Map.tryFind "0"
            | _ -> None
        match parameter, operandTy with
        | Some (TyVar v), TyVar _ when not (scheme.Quantified.Contains v.Id) -> true
        | Some (TyVar v), _ when not (scheme.Quantified.Contains v.Id) -> false
        | _ ->
            let funcTy, next = instantiate state scheme
            let resultTy, next = freshVar next
            Unifier.unify ((funcTy, TyFunc(input, resultTy), Equal, SourceSpan.none) :: next.Constraints) |> Result.isOk

    /// The definition the operator `expr` calls with its operands (`input`, as a call's arguments are typed), if it
    /// calls one, and the name it calls it by. On a built-in type, that of its left operand (`leftTy`) as far as it is
    /// known, the operator is the built-in one. Otherwise it is the definition attached under the operator's name to
    /// that type, when `attached` finds one: `` $Vector.`+` `` for `v + w`. Otherwise it is the binding of the
    /// operator's name in `env`. `!=` calls what `==` does, and `-x` what `negate` is. `and` and `or` are keywords, so
    /// their names are never bound.
    ///
    /// `==` is a lookup of `Eq` at its operands' type, whose order puts an instance in scope before the type's own: a
    /// binding of `==` that takes the operands comes first, on a built-in type too, so after `use ignoringCase`, an
    /// `Eq(string)`, `a == b` on strings calls its `==`.
    let definition
        (attached: Identifier -> Identifier option)
        (env: TypeEnv)
        (state: InferState)
        (expr: Expression)
        (leftTy: Ty)
        (input: Ty)
        : (Identifier * TypeScheme) option =
        let bound (name: Identifier) = TypeEnv.tryFind name env |> Option.map (fun scheme -> name, scheme)
        let name =
            match expr.Kind with
            | BinaryOp(op, _, _) when EqualityOperators.isOperator op -> Some EqualityOperators.equals
            | BinaryOp(op, _, _) -> Some op
            | FunctionCall(name, _, [ _ ]) when name = NegateSyntax.builtIn -> Some ArithmeticOperators.negate
            | _ -> None
        let inScope (name: Identifier) (operandTy: Ty) =
            if name = EqualityOperators.equals then bound name |> Option.filter (snd >> compares state operandTy input) else None
        match name, Unifier.unalias (resolveSoFar state leftTy) with
        | None, _ -> None
        | Some name, operandTy when (inScope name operandTy).IsSome -> inScope name operandTy
        | Some _, TyPrimitive _ -> None
        | Some name, operandTy ->
            match attached name with
            | Some attachedName -> bound attachedName
            | None when name = EqualityOperators.equals -> bound name |> Option.filter (snd >> compares state operandTy input)
            | None -> bound name

    /// `expr`, an operator that calls `scheme` by the name `callee`, typed as that call: the definition takes its
    /// operands (`input`) and the operator's value is what it returns.
    let infer (state: InferState) (expr: Expression) (callee: Identifier) (scheme: TypeScheme) (input: Ty) : Ty * InferState =
        let funcTy, next = instantiate (recordReference expr scheme state) scheme
        let resultTy, next = freshVar next
        let next = addConstraint funcTy (TyFunc(input, resultTy)) next
        resultTy, { next with CalleeTypes = next.CalleeTypes |> Map.add expr.Id funcTy; OperatorCalls = next.OperatorCalls |> Map.add expr.Id callee }

    /// `expr`, `-x` on an operand of the type `operandTy`: a call to the `negate` of that type or of the function's
    /// `Add` (see `definition`), or else the negation of a built-in number. `numberTy` is the number that negation is
    /// of, `float` or `int`, as far as the operand's type is known.
    let inferNegation
        (attached: Identifier -> Identifier option)
        (env: TypeEnv)
        (state: InferState)
        (expr: Expression)
        (operandTy: Ty)
        (numberTy: Ty)
        : Result<Ty * InferState, Diagnostic list> =
        match definition attached env state expr operandTy operandTy with
        | Some (callee, scheme) -> Ok (infer state expr callee scheme operandTy)
        | None ->
            ArithmeticOperators.check state expr NegateSyntax.builtIn operandTy operandTy numberTy
            |> Result.map (fun negated -> operandTy, negated)
