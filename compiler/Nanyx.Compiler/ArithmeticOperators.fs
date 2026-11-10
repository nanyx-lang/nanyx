namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// `+`, `-`, `*`, `/` and `%`, and prefix `-` (see "Numeric contexts" in the docs' work in progress). On `int` and
/// `float` they are the built-in operators. `+`, `-` and prefix `-` read through the prelude's well-known context
/// `Add`, whose members are `zero`, `` `+` ``, `` `-` `` and `negate`: on a type that attaches them, or a type variable
/// of a function that requires `Add` of it, the operator calls the member (see `OperatorCalls`). `*`, `/` and `%` read
/// through `Num`, which has `Add`'s members and those three, and `fromInt`. The compiler's own instances for `int` and
/// `float` are the members the prelude attaches to `Int` and `Float`, which are what generic code is passed.
module internal ArithmeticOperators =
    /// The prelude's context of the values that add and subtract.
    let addContext = "Add"

    /// The prelude's context of the numbers: `Add`'s members, and `*`, `/`, `%` and `fromInt`.
    let numContext = "Num"

    /// `Add`'s member that the literal `0` reads as where a value of a type variable is added to it.
    let zero = "zero"

    /// `Num`'s member that any other integer literal is given to there.
    let fromInt = "fromInt"

    /// `Add`'s member that prefix `-` reads.
    let negate = "negate"

    /// The arithmetic operators.
    let operators = [ "+"; "-"; "*"; "/"; "%" ]

    /// Whether `op` is an arithmetic operator.
    let isOperator (op: string) = List.contains op operators

    /// Whether `ty` is a type that is known not to be a number. A type variable, an open record and a nominal type
    /// are left to unification, which reports them if they are not numbers; the error type is what failed to check.
    let private isKnownNonNumber (ty: Ty) =
        match Unifier.unalias ty with
        | ty when Unifier.isError ty -> false
        | TyPrimitive("int" | "float")
        | TyVar _
        | TyOpenRecord _
        | TyNominal _ -> false
        | _ -> true

    /// The operator `op` as a diagnostic writes it: prefix `-` is written `-`.
    let private written (op: string) = if op = NegateSyntax.builtIn then "-" else op

    /// The error for using `op` on a value of the type `ty`, which is not a number.
    let private notANumber (op: string) (ty: Ty) =
        let advice =
            match op, Unifier.unalias ty with
            | "+", (TyPrimitive "string" | TyApply("list", _)) -> ". Use ++ to join them"
            | _ -> ""
        $"'{Unifier.tyToDisplayString Ascii ty}' cannot be used with {written op}: it is not a number{advice}"

    /// The context the operator `op` needs of a type variable it is used on, and what the operator does, as a
    /// diagnostic says it: `Add` for `+`, `-` and prefix `-`, and `Num` for `*`, `/` and `%`.
    let private contextFor (op: string) =
        match op with
        | "+" -> Some (addContext, "adds")
        | "-" -> Some (addContext, "subtracts")
        | "*" -> Some (numContext, "multiplies")
        | "/" -> Some (numContext, "divides")
        | "%" -> Some (numContext, "takes the remainder of")
        | _ when op = NegateSyntax.builtIn -> Some (addContext, "negates")
        | _ -> None

    /// Whether the prelude's `context` is in scope: a module checked without the prelude has none to require.
    let private isDeclared (state: InferState) (context: string) =
        state.TypeDefs |> Map.tryFind context |> Option.exists (fun info -> info.IsContext && info.Parameters.Length = 1)

    /// The error for `op`, which needs `context`, on values of the type variable `v` of the function `defName`, whose
    /// type does not require it.
    let private undeclared (op: string) (context: string) (does: string) (v: TyVar) (defName: Identifier) =
        let tyText = Unifier.tyToDisplayString Ascii (TyVar v)
        $"'{defName}' {does} values of type '{tyText}' with {written op}, so its type must require {context}({tyText}): add [{context}({tyText})] to the type of '{defName}'"

    /// Checks `expr`, the operator `op` on operands of the types `leftTy` and `rightTy`, which calls no definition. An
    /// operand whose type is already known not to be a number is an error that names the operator. So is one of a type
    /// variable of the type the function around it writes, which then does not require the context the operator needs
    /// of it: had it, the operator would have called its member. That one fails to check, and the def around it
    /// carries on, so that the variable is not also reported as having to be a number. Otherwise the operands are the
    /// number `numberTy`, `int` or `float`.
    let check (state: InferState) (expr: Expression) (op: string) (leftTy: Ty) (rightTy: Ty) (numberTy: Ty) : Result<InferState, Diagnostic list> =
        let tys = [ leftTy; rightTy ] |> List.map (resolveSoFar state)
        let declaredVar = tys |> List.tryPick (function TyVar({ Name = Some _ } as v) -> Some v | _ -> None)
        match tys |> List.tryFind isKnownNonNumber, contextFor op, declaredVar, state.ContextScope with
        | Some ty, _, _, _ -> Error [ Diagnostics.at DiagnosticCodes.TypeMismatch expr.Span (notANumber op ty) ]
        | None, Some (context, does), Some v, DefScope(defName, false) when isDeclared state context ->
            let error = Diagnostics.at DiagnosticCodes.MissingContextMembers expr.Span (undeclared op context does v defName)
            Ok { state with FailedExprs = (expr.Id, [ error ]) :: state.FailedExprs }
        | _ -> Ok (addConstraint leftTy numberTy state)

    /// What a literal operand of an arithmetic operator is, where it is not the literal it would be elsewhere.
    type private LiteralReading =
        /// The member of the context the function requires that it is written as.
        | AsMember of Expression
        /// An error: the literal cannot be of the type it is used at.
        | Wrong of DiagnosticCode * string

    /// What the literal `literal` is, an operand of an arithmetic operator whose other operand is of the type
    /// `otherTy`, where that is a type variable of the type the function around it writes, in a function that requires
    /// `Add` or `Num` of it (and so binds `zero` in `env`). `0` is `zero`, and any other integer `fromInt` of it, which
    /// needs `Num`. A decimal literal cannot be of the variable: it may be an integer type.
    ///
    /// The other operand may be of a variable the constraints so far have put in place of the type's own, as the
    /// result of `x * x` is: the operator calls the member there all the same. Only an error needs to name the type's.
    let private literalReading (env: TypeEnv) (state: InferState) (literal: Expression) (otherTy: Ty) : LiteralReading option =
        let bound (name: Identifier) = (TypeEnv.tryFind name env).IsSome
        match literal.Kind, Unifier.unalias (resolveSoFar state otherTy), state.ContextScope with
        | LiteralExpr value, (TyVar v as var), DefScope(defName, false) when bound zero ->
            let tyText = Unifier.tyToDisplayString Ascii var
            match value with
            | IntLit 0 -> Some (AsMember { literal with Kind = IdentifierExpr zero })
            | IntLit n when bound fromInt ->
                Some (AsMember { literal with Kind = FunctionCall(fromInt, None, [ Expression.at literal.Span (LiteralExpr(IntLit n)) ]) })
            | _ when v.Name.IsNone -> None
            | IntLit n ->
                Some (Wrong (DiagnosticCodes.MissingContextMembers, $"'{n}' is an '{tyText}' here, which needs {numContext}({tyText}): add [{numContext}({tyText})] to the type of '{defName}'"))
            | FloatLit x ->
                let text = x.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                let text = if text.Contains '.' || text.Contains 'E' then text else text + ".0"
                Some (Wrong (DiagnosticCodes.TypeMismatch, $"'{text}' cannot be an '{tyText}': '{tyText}' may be an integer type. Take the value as a parameter"))
            | _ -> None
        | _ -> None

    /// The operands `left` and `right` of `expr`, an arithmetic operator, with a literal among them that is not the
    /// literal it would be elsewhere (see `literalReading`) inferred again as the member it is written as, by `infer`,
    /// and recorded in `LiteralMembers` to be written as it. A literal that cannot be of the type it is used at fails to
    /// check, and is taken to be of that type, so that the operator is not also reported.
    let readLiterals
        (infer: InferState -> Expression -> Result<TypedExpr * InferState, Diagnostic list>)
        (env: TypeEnv)
        (state: InferState)
        (expr: Expression)
        (left: TypedExpr)
        (right: TypedExpr)
        : Result<TypedExpr * TypedExpr * InferState, Diagnostic list> =
        let reread (operand: TypedExpr) (other: TypedExpr) (state: InferState) =
            match expr.Kind, literalReading env state operand.Expr other.Type with
            | BinaryOp(op, _, _), Some (AsMember asMember) when isOperator op ->
                infer state asMember
                |> Result.map (fun (typed, next) -> typed, { next with LiteralMembers = next.LiteralMembers |> Map.add asMember.Id asMember })
            | BinaryOp(op, _, _), Some (Wrong(code, message)) when isOperator op ->
                let error = Diagnostics.at code operand.Expr.Span message
                Ok ({ operand with Type = other.Type }, { state with FailedExprs = (operand.Expr.Id, [ error ]) :: state.FailedExprs })
            | _ -> Ok (operand, state)
        reread left right state
        |> Result.bind (fun (left, next) -> reread right left next |> Result.map (fun (right, next) -> left, right, next))