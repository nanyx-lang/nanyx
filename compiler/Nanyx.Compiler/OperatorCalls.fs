namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// Operators that call a definition. Where an operator's name is bound, as `` `+` `` is in a function that requires
/// `[Sum(a)]`, `x + y` calls that definition with its two operands, so generic code can add any type that has a
/// `+`. On a built-in type the operator is still the built-in one, whatever is in scope, so `count + 1` in that
/// same function adds ints.
module internal OperatorCalls =
    /// The definition the binary operator `expr` calls, if it calls one: the binding of its name in `env`, unless
    /// its left operand (`leftTy`) is already known to be of a built-in type. `and` and `or` are keywords, so their
    /// names are never bound.
    let definition (env: TypeEnv) (state: InferState) (expr: Expression) (leftTy: Ty) : TypeScheme option =
        match expr.Kind, Unifier.unalias (resolveSoFar state leftTy) with
        | _, TyPrimitive _ -> None
        | BinaryOp(op, _, _), _ -> TypeEnv.tryFind op env
        | _ -> None

    /// `expr`, a binary operator that calls `scheme`, typed as that call: the definition takes its operands
    /// (`input`, as a call's arguments are typed) and the operator's value is what it returns.
    let infer (state: InferState) (expr: Expression) (scheme: TypeScheme) (input: Ty) : Ty * InferState =
        let funcTy, next = instantiate (recordReference expr scheme state) scheme
        let resultTy, next = freshVar next
        let next = addConstraint funcTy (TyFunc(input, resultTy)) next
        resultTy, { next with CalleeTypes = next.CalleeTypes |> Map.add expr.Id funcTy; OperatorCalls = next.OperatorCalls |> Set.add expr.Id }
