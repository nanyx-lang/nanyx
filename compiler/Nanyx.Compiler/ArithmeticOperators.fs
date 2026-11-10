namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// `+`, `-`, `*`, `/` and `%`, and prefix `-` (see "Numeric contexts" in the docs' work in progress). On `int` and
/// `float` they are the built-in operators. `+`, `-` and prefix `-` read through the prelude's well-known context
/// `Add`, whose members are `zero`, `` `+` ``, `` `-` `` and `negate`: on a type that attaches them, or a type variable
/// of a function that requires `Add` of it, the operator calls the member (see `OperatorCalls`). The compiler's own
/// `Add(int)` and `Add(float)` are the members the prelude attaches to `Int` and `Float`, which are what generic code is
/// passed.
module internal ArithmeticOperators =
    /// The prelude's context of the values that add and subtract.
    let addContext = "Add"

    /// `Add`'s member that the literal `0` reads as where a value of a type variable is added to it.
    let zero = "zero"

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

    /// The context the operator `op` needs of a type variable it is used on, if it is one there is: `Add` for `+`, `-`
    /// and prefix `-`.
    let private contextFor (op: string) =
        match op with
        | "+" | "-" -> Some (addContext, (if op = "+" then "adds" else "subtracts"))
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

    /// The member the literal `literal` reads as, an operand of an arithmetic operator whose other operand is of the
    /// type `otherTy`, if it reads as one: where that is a type variable of the type the function around it writes, `0`
    /// is `zero`, when `env` binds it (a function that requires `Add` binds it).
    let private literalMember (env: TypeEnv) (state: InferState) (literal: Expression) (otherTy: Ty) : Identifier option =
        match literal.Kind, Unifier.unalias (resolveSoFar state otherTy), state.ContextScope with
        | LiteralExpr(IntLit 0), TyVar { Name = Some _ }, DefScope(_, false) when (TypeEnv.tryFind zero env).IsSome -> Some zero
        | _ -> None

    /// The operands `left` and `right` of `expr`, an arithmetic operator, with a literal among them that reads as a
    /// member (see `literalMember`) inferred again as that member, by `infer`, and recorded in `LiteralMembers` to be
    /// written as it.
    let readLiterals
        (infer: InferState -> Expression -> Result<TypedExpr * InferState, Diagnostic list>)
        (env: TypeEnv)
        (state: InferState)
        (expr: Expression)
        (left: TypedExpr)
        (right: TypedExpr)
        : Result<TypedExpr * TypedExpr * InferState, Diagnostic list> =
        let reread (operand: TypedExpr) (other: TypedExpr) (state: InferState) =
            match expr.Kind, literalMember env state operand.Expr other.Type with
            | BinaryOp(op, _, _), Some memberName when isOperator op ->
                let asMember = { operand.Expr with Kind = IdentifierExpr memberName }
                infer state asMember
                |> Result.map (fun (typed, next) -> typed, { next with LiteralMembers = next.LiteralMembers |> Map.add asMember.Id asMember })
            | _ -> Ok (operand, state)
        reread left right state
        |> Result.bind (fun (left, next) -> reread right left next |> Result.map (fun (right, next) -> left, right, next))
