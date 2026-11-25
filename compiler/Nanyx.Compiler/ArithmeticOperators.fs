namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// `+`, `-`, `*`, `/`, `//` and `%`, and prefix `-` (see "Add(a)", "Sub(a)" and "Num(a)" on the docs' Contexts page).
/// On `int` and `float` they are the built-in operators. Each reads the context member of its name, and prefix `-` reads
/// `negate`, through the prelude's smallest context that has it (see `OperatorContexts`): `+` through `Add`, whose
/// members are `zero` and `` `+` ``, `-` and prefix `-` through `Sub`, whose members are `` `-` `` and `negate`, and
/// `*`, `/`, `//` and `%` through `Num`, which has `Add`'s and `Sub`'s members and those four, and `fromInt`. Each gives
/// a value of its operands' type, but `/`, which gives their `fraction` (see `Fractions`). On a type that attaches the member, or a type variable of a function that
/// requires the context of it, the operator calls the member (see `OperatorCalls`). The compiler's own instances for
/// `int` and `float` are the members the prelude attaches to them, which are what generic code is passed.
///
/// A function that writes its type says which contexts it requires. One that does not has them inferred: an operator
/// on a type still a variable there is left until the function is inferred (see `ArithmeticInference`).
module internal ArithmeticOperators =
    /// `Add`'s member that the literal `0` reads as where a value of a type variable is added to it.
    let zero = "zero"

    /// `Num`'s member that any other integer literal is given to there.
    let fromInt = "fromInt"

    /// `Sub`'s member that prefix `-` reads.
    let negate = "negate"

    /// The operator for powers. It is not a member: `x ** n` is a call to the prelude's `powerFunction`.
    let power = "**"

    /// The prelude function that `x ** n` calls, `[Num(a)] (a, int) -> a`. Its name is reserved, so that no `pow` a
    /// module defines, `math.pow` among them, changes what `**` means.
    let powerFunction = "__pow"

    /// The arithmetic operators.
    let operators = [ "+"; "-"; "*"; "/"; "//"; "%" ]

    /// The operator that divides with a fraction, whose value is of its operands' `fraction` (see `Fractions`).
    let divide = "/"

    /// Whether `op` is an arithmetic operator.
    let isOperator (op: string) = List.contains op operators

    /// Whether an integer literal beside `op` is read as a member in generic code (see `readLiterals`): an arithmetic
    /// operator, a comparison or `==` and `!=`.
    let private readsLiterals (op: string) = isOperator op || OrderingOperators.isOperator op || EqualityOperators.isOperator op

    /// The context member the operator `op` reads: the one of its name, or `negate` for prefix `-`.
    let memberOf (op: string) = if op = NegateSyntax.builtIn then negate else op

    /// What the operator `op` does, as a diagnostic says it.
    let private does (op: string) =
        match op with
        | "+" -> Some "adds"
        | "-" -> Some "subtracts"
        | "*" -> Some "multiplies"
        | "/" -> Some "divides"
        | "//" -> Some "floors the quotient of"
        | "%" -> Some "takes the remainder of"
        | _ when op = NegateSyntax.builtIn -> Some "negates"
        | _ -> None

    /// The context the operator `op` needs of a type variable it is used on, the prelude's smallest with the member it
    /// reads, and what the operator does: `Add` for `+`, `Sub` for `-` and prefix `-`, and `Num` for `*`, `/`, `//`
    /// and `%`. A module checked without the prelude has no context to require.
    let private contextFor (state: InferState) (op: string) =
        match does op, OperatorContexts.smallest state (memberOf op) with
        | Some does, Some context -> Some (context, does)
        | _ -> None

    /// Checks `expr`, the operator `op` on operands of the types `leftTy` and `rightTy`, which gives a value of the type
    /// `resultTy` (see `resultOf`) and calls no definition. An
    /// operand whose type is already known not to be a number is an error that names the operator. So is one of a type
    /// variable of the type the function around it writes, which then does not require the context the operator needs
    /// of it: had it, the operator would have called its member. That one fails to check, and the def around it
    /// carries on, so that the variable is not also reported as having to be a number. In a function that does not
    /// write its type, operands of a type still a variable are left until the function is inferred, which then requires
    /// the context of the variable if it is one of its own (see `ArithmeticInference`). Operands of a number literal's
    /// own variable, as in `1.0 / 0.0`, are whatever number the code around them settles (see `NumericLiterals`), and so
    /// are operands of a type still a variable elsewhere, which the rest of the def may settle (`awaitOperand`).
    /// Otherwise the operands are the number `numberTy`, `int` or `float`.
    /// The built-in operators work on `int`, `float` and the sized numbers. An operation is on the type of whichever
    /// operand is already known to be one of the others, and defaults to `int` otherwise. (Ordering is
    /// `OrderingOperators`'.)
    let numericOperandType (state: InferState) (leftTy: Ty) (rightTy: Ty) =
        let known ty =
            match Unifier.unalias (resolveSoFar state ty) with
            | TyPrimitive "float"
            | SizedNumberType _ as number -> Some number
            | _ -> None
        known leftTy |> Option.orElse (known rightTy) |> Option.defaultValue TypeResolution.numericType

    let check (state: InferState) (expr: Expression) (op: string) (leftTy: Ty) (rightTy: Ty) (resultTy: Ty) (numberTy: Ty) : Result<InferState, Diagnostic list> =
        let tys = [ leftTy; rightTy ] |> List.map (resolveSoFar state)
        let declaredVar = tys |> List.tryPick (function TyVar({ Name = Some _ } as v) -> Some v | _ -> None)
        let areVariables = tys |> List.forall (function TyVar { Name = None } -> true | _ -> false)
        match tys |> List.tryFind NumericLiterals.isKnownNonNumber, contextFor state op, declaredVar, state.ContextScope with
        | Some ty, _, _, _ -> Error [ Diagnostics.at DiagnosticCodes.TypeMismatch expr.Span (NumericLiterals.notANumber op ty) ]
        | None, Some (context, does), Some v, DefScope(defName, false) ->
            let error = NumericLiterals.undeclared state expr.Span op { Def = defName; Context = context; Does = does } v
            Ok { state with FailedExprs = (expr.Id, [ error ]) :: state.FailedExprs }
        | None, Some _, None, DefScope(defName, true) when areVariables ->
            let pending = { InDef = defName; Operator = expr.Id; MemberName = memberOf op; OperandTy = leftTy; ResultTy = resultTy; At = expr.Span }
            Ok { state with PendingArithmetic = pending :: state.PendingArithmetic }
        | _ when NumericLiterals.isOpen state leftTy -> Ok state
        | _ when areVariables ->
            let requires =
                match contextFor state op, state.ContextScope with
                | Some (context, does), DefScope(defName, false) -> Some { Def = defName; Context = context; Does = does }
                | _ -> None
            Ok (NumericLiterals.awaitOperand op expr leftTy requires state)
        | _ -> Ok (addConstraint leftTy numberTy state)

    /// The type of `expr`, the built-in arithmetic operator `op` on operands of the type `operandTy`, and `state` with it
    /// recorded: that type, or its `fraction` for `/` (see `Fractions`).
    let resultOf (state: InferState) (expr: Expression) (op: string) (operandTy: Ty) : Ty * InferState =
        if op = divide then Fractions.resultOf state expr.Span operandTy else operandTy, state

    /// What a literal operand of an arithmetic operator or a comparison is, where it is not the literal it would be
    /// elsewhere.
    type private LiteralReading =
        /// The member of the context the function requires that it is written as.
        | AsMember of Expression
        /// The member a number with a unit is written as, its number of base units, of the dimension it is a value of.
        | AsMeasured of Expression * Dimension
        /// The error at the literal: it cannot be of the type it is used at.
        | Wrong of Diagnostic

    /// What the literal `literal` is, an operand of an arithmetic operator or a comparison whose other operand is of the
    /// type `otherTy`, where that is a type variable of the type the function around it writes, in a function that
    /// requires `Add` or `Num` of it (and so binds `zero` in `env`). `0` is `zero`, and any other integer `fromInt` of
    /// it, which needs `Num`. A decimal literal cannot be of the variable: it may be an integer type.
    ///
    /// The other operand may be of a variable the constraints so far have put in place of the type's own, as the
    /// result of `x * x` is: the operator calls the member there all the same. Only an error needs to name the type's.
    /// One whose type is still a literal's own variable, as `1 + 2` is, is a number the literal takes the type of (see
    /// `NumericLiterals`).
    let private literalReading (env: TypeEnv) (state: InferState) (literal: Expression) (otherTy: Ty) : LiteralReading option =
        let bound (name: Identifier) = (TypeEnv.tryFind name env).IsSome
        match literal.Kind, Unifier.unalias (resolveSoFar state otherTy), state.ContextScope with
        | LiteralExpr value, (TyVar v as var), DefScope(defName, false) when bound zero && not (NumericLiterals.isUnsettledVar state v) ->
            let tyText = Unifier.tyToDisplayString Ascii var
            match value with
            | IntLit n when n.IsZero -> Some (AsMember { literal with Kind = IdentifierExpr zero })
            | IntLit n when bound fromInt ->
                Some (AsMember { literal with Kind = FunctionCall(fromInt, None, [ Expression.at literal.Span (LiteralExpr(IntLit n)) ]) })
            | _ when v.Name.IsNone -> None
            | IntLit n ->
                OperatorContexts.smallest state fromInt
                |> Option.map (fun context ->
                    Diagnostics.at DiagnosticCodes.MissingContextMembers literal.Span
                        $"'{n}' is an '{tyText}' here, which needs {context}({tyText}): add [{context}({tyText})] to the type of '{defName}'"
                    |> Diagnostics.about context
                    |> ContextFixes.withAddToType state defName $"{context}({tyText})"
                    |> Wrong)
            | FloatLit x ->
                let text = x.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                let text = if text.Contains '.' || text.Contains 'E' then text else text + ".0"
                Some (Wrong (Diagnostics.at DiagnosticCodes.TypeMismatch literal.Span $"'{text}' cannot be an '{tyText}', which may be an integer type: take the value as a parameter"))
            | _ -> None
        | _ -> None

    /// What the number with a unit `literal`, `number unitName`, is beside a measured value of the generic storage
    /// `storage`: its number of base units, as `literalReading` reads a plain literal beside a value of a type variable,
    /// `fromInt(500000000)` for `500ms`, or `zero`. A fraction of a base unit is no number of a storage that may be an
    /// integer type.
    let private measuredReading (env: TypeEnv) (state: InferState) (literal: Expression) (number: WrittenNumber) (unitName: Identifier) (storage: Ty) : LiteralReading option =
        let bound (name: Identifier) = (TypeEnv.tryFind name env).IsSome
        match Unifier.unalias (resolveSoFar state storage), state.ContextScope, state.Units |> Map.tryFind unitName with
        | TyVar v, DefScope(_, false), Some info when bound zero && not (NumericLiterals.isUnsettledVar state v) ->
            let value = NumericLiterals.inBaseUnits state number unitName
            let dimension = Dimension.ofBase info.Dimension
            if not (Rational.isWhole value) then
                let written = UnitSyntax.written number unitName
                Some (Wrong (Diagnostics.at DiagnosticCodes.TypeMismatch literal.Span
                                 $"'{written}' is {Rational.text value} of its base unit, which no number of '{Unifier.tyToDisplayString Ascii (TyVar v)}' may be, as an integer type can't: write a whole number of the base unit"))
            elif value.Numerator.IsZero then Some (AsMeasured({ literal with Kind = IdentifierExpr zero }, dimension))
            elif bound fromInt then
                Some (AsMeasured({ literal with Kind = FunctionCall(fromInt, None, [ Expression.at literal.Span (LiteralExpr(IntLit value.Numerator)) ]) }, dimension))
            else None
        | _ -> None

    /// What the literal `operand` is beside a value of the type `otherTy`, an operand of the operator `op`: beside a
    /// measured value, a number with a unit is read by `measuredReading`, and a plain literal that scales it by
    /// `literalReading` as beside its storage; any other as `literalReading` reads it.
    let private readingBeside (env: TypeEnv) (state: InferState) (op: string) (operand: Expression) (otherTy: Ty) : LiteralReading option =
        match operand.Kind, Unifier.unalias (resolveSoFar state otherTy) with
        | LiteralExpr(UnitLit(number, unitName)), TyMeasure(_, storage) -> measuredReading env state operand number unitName storage
        | LiteralExpr(UnitLit _), _ -> None
        | LiteralExpr _, TyMeasure(_, storage) when List.contains op [ "*"; "/"; "//" ] -> literalReading env state operand storage
        | _ -> literalReading env state operand otherTy

    /// `state` without the number literal `literal` is among those to settle and check: it is read as another
    /// expression.
    let private withoutLiteral (literal: Expression) (state: InferState) =
        let other (recorded: NumericLiteral) = recorded.Node <> Some literal.Id
        { state with NumericLiterals = List.filter other state.NumericLiterals; UnsettledLiterals = List.filter other state.UnsettledLiterals }

    /// The operands `left` and `right` of `expr`, an arithmetic operator, a comparison, `==` or `!=`, with a literal
    /// among them that is not the literal it would be elsewhere (see `literalReading`) inferred again as the member it
    /// is written as, by `infer`, and recorded in `ReadAs` to be written as it. A literal that cannot be of the type it
    /// is used at fails to check, and is taken to be of that type, so that the operator is not also reported.
    let readLiterals
        (infer: InferState -> Expression -> Result<TypedExpr * InferState, Diagnostic list>)
        (env: TypeEnv)
        (state: InferState)
        (expr: Expression)
        (left: TypedExpr)
        (right: TypedExpr)
        : Result<TypedExpr * TypedExpr * InferState, Diagnostic list> =
        let reread (operand: TypedExpr) (other: TypedExpr) (state: InferState) =
            let op = match expr.Kind with BinaryOp(op, _, _, _) -> op | _ -> ""
            match readsLiterals op, readingBeside env state op operand.Expr other.Type with
            | true, Some (AsMember asMember) ->
                infer state asMember
                |> Result.map (fun (typed, next) -> typed, { next with ReadAs = next.ReadAs |> Map.add asMember.Id asMember })
            | true, Some (AsMeasured(asMember, dimension)) ->
                infer state asMember
                |> Result.map (fun (typed, next) ->
                    let measured = TyMeasure(dimension, typed.Type)
                    { typed with Type = measured },
                    { withoutLiteral operand.Expr next with
                        ReadAs = next.ReadAs |> Map.add asMember.Id asMember
                        ExprTypes = next.ExprTypes |> Map.add asMember.Id measured })
            | true, Some (Wrong error) ->
                Ok ({ operand with Type = other.Type }, { withoutLiteral operand.Expr state with FailedExprs = (operand.Expr.Id, [ error ]) :: state.FailedExprs })
            | _ -> Ok (operand, state)
        reread left right state
        |> Result.bind (fun (left, next) -> reread right left next |> Result.map (fun (right, next) -> left, right, next))
    /// The error for `expr`, `left ** right` with `right` a negative literal, on a value of the type `leftTy` whose `/`
    /// gives `fraction`, another type, if it does. A negative power of a type that is not an integer type is `1 / x ** n`,
    /// a `fraction`, which the prelude's `__pow` gives as the type itself, as it is on `float` and `F32`; one whose
    /// `fraction` is another type has no such power. An integer type's is not this error: its `**` stops the program,
    /// as any negative power of an integer does.
    let negativePowerError (expr: Expression) (right: Expression) (leftTy: Ty) (fraction: Ty option) : Diagnostic option =
        match right.Kind, Unifier.unalias leftTy, fraction |> Option.map Unifier.unalias with
        | LiteralExpr(IntLit n), (TyNominal _ as own), Some fraction when n.Sign < 0 && fraction <> own && (TypeEnv.freeTyVars fraction).IsEmpty ->
            let at = match expr.Kind with BinaryOp(_, Some opSpan, _, _) -> opSpan | _ -> expr.Span
            let ownText, fractionText = Unifier.tyToDisplayString Ascii own, Unifier.tyToDisplayString Ascii fraction
            Some (
                Diagnostics.at DiagnosticCodes.NegativePowerOfFraction at
                    $"A negative power of a '{ownText}' is '1 / x ** n', a '{fractionText}', which `/` on '{ownText}' gives, and not a '{ownText}': write `{ownText}.fromInt(1) / x ** {-n}` for it")
        | _ -> None

    /// `expr`, `left ** right`, inferred by `infer` as the call `__pow(left, right)` that it is, and recorded in
    /// `ReadAs` to be written as it.
    let inferPower
        (infer: InferState -> Expression -> Result<TypedExpr * InferState, Diagnostic list>)
        (state: InferState)
        (expr: Expression)
        (left: Expression)
        (right: Expression)
        : Result<TypedExpr * InferState, Diagnostic list> =
        inferAsCall infer state expr powerFunction [ left; right ]
