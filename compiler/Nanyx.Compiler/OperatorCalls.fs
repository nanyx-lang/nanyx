namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// Operators that call a definition. Where an operator's name is bound, as `` `+` `` is in a function that requires
/// `[Sum(a)]`, `x + y` calls that definition with its two operands, so generic code can add any type that has a
/// `+`. On a built-in type the operator is still the built-in one, whatever is in scope, so `count + 1` in that
/// same function adds ints.
///
/// `!=` has no definition of its own: it is `not (a == b)`, so it calls what `==` is bound to. Either calls it only
/// for operands the definition takes: in a function that requires `[Eq(a)]`, `==` on two `a`s calls the context's
/// member, and on two `list(a)`s is the built-in one (see `EqualityOperators`).
///
/// `==` is a lookup of `Eq` at its operands' type even where that type is known, so unlike the other operators it
/// calls what it is bound to on a built-in type too: after `use ignoringCase`, an `Eq(string)`, `a == b` on strings
/// calls its `==`. On a nominal type with a `==` attached and no other in scope, `a == b` calls the attached one.
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

    /// The definition the binary operator `expr` calls with its operands (`input`, as a call's arguments are typed),
    /// if it calls one: the binding of its name in `env`, unless its left operand (`leftTy`) is already known to be
    /// of a built-in type, and the name of the definition when that is not the operator's. `==` and `!=` call the
    /// binding of `==` for any type it takes, and otherwise the `==` attached to the type, which `attached` gives.
    /// `and` and `or` are keywords, so their names are never bound.
    let definition
        (env: TypeEnv)
        (state: InferState)
        (expr: Expression)
        (leftTy: Ty)
        (input: Ty)
        (attached: Ty -> (Identifier * TypeScheme) option)
        : (Identifier option * TypeScheme) option =
        let operandTy = resolveSoFar state leftTy
        match expr.Kind, Unifier.unalias operandTy with
        | BinaryOp(op, _, _), unaliased when EqualityOperators.isOperator op ->
            match TypeEnv.tryFind EqualityOperators.equals env |> Option.filter (compares state unaliased input) with
            | Some scheme -> Some (None, scheme)
            | None -> attached operandTy |> Option.map (fun (name, scheme) -> Some name, scheme)
        | _, TyPrimitive _ -> None
        | BinaryOp(op, _, _), _ -> TypeEnv.tryFind op env |> Option.map (fun scheme -> None, scheme)
        | _ -> None

    /// `expr`, a binary operator that calls `scheme`, typed as that call: the definition takes its operands
    /// (`input`) and the operator's value is what it returns. `target` is the definition's name, where that is not
    /// the operator's: it is recorded with the pipes' (`PipeTargets`), for the call to be written with it.
    let infer (state: InferState) (expr: Expression) (target: Identifier option) (scheme: TypeScheme) (input: Ty) : Ty * InferState =
        let funcTy, next = instantiate (recordReference expr scheme state) scheme
        let resultTy, next = freshVar next
        let next = addConstraint funcTy (TyFunc(input, resultTy)) next
        let targets =
            match target with
            | Some name -> next.PipeTargets |> Map.add expr.Id name
            | None -> next.PipeTargets
        resultTy,
        { next with
            CalleeTypes = next.CalleeTypes |> Map.add expr.Id funcTy
            OperatorCalls = next.OperatorCalls |> Set.add expr.Id
            PipeTargets = targets }
