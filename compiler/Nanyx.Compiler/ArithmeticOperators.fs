namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// `+`, `-`, `*`, `/` and `%`, which work on numbers: `int` and `float` (see "Numeric contexts" in the docs' work in
/// progress).
module internal ArithmeticOperators =
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

    /// The error for using `op` on a value of the type `ty`, which is not a number.
    let private notANumber (op: string) (ty: Ty) =
        let advice =
            match op, Unifier.unalias ty with
            | "+", (TyPrimitive "string" | TyApply("list", _)) -> ". Use ++ to join them"
            | _ -> ""
        $"'{Unifier.tyToDisplayString Ascii ty}' cannot be used with {op}: it is not a number{advice}"

    /// Checks `expr`, the operator `op` on operands of the types `leftTy` and `rightTy`: an operand whose type is
    /// already known not to be a number is an error that names the operator.
    let check (state: InferState) (expr: Expression) (op: string) (leftTy: Ty) (rightTy: Ty) : Result<unit, Diagnostic list> =
        [ leftTy; rightTy ]
        |> List.map (resolveSoFar state)
        |> List.tryFind isKnownNonNumber
        |> function
            | Some ty -> Error [ Diagnostics.at DiagnosticCodes.TypeMismatch expr.Span (notANumber op ty) ]
            | None -> Ok ()
