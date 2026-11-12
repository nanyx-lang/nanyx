namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// `++`, which joins two strings or two lists (see "Operator contexts" in the docs' work in progress). On a `string`
/// and on a `list(a)` it is the built-in operator. It reads the member `` `++` ``, through the prelude's smallest
/// context that has it, `Append`, whose instances for `string` and `list(a)` are the members the prelude attaches to
/// them. On a type that attaches `` `++` ``, or a type variable of a function that requires `Append` of it, the
/// operator calls the member (see `OperatorCalls`). A function that does not write its type and joins values of one
/// of its type variables requires `Append` of it, as one that adds them requires `Add` (see `ArithmeticInference`).
module internal AppendOperator =
    /// The operator, and the member of `Append` it reads.
    let operator = "++"

    /// Whether `ty` is a type the built-in `++` joins: a string or a list.
    let isBuiltIn (ty: Ty) =
        match Unifier.unalias ty with
        | TyPrimitive "string"
        | TyApply("list", _) -> true
        | _ -> false

    /// Whether `ty` is a type that is known to have no `++`. A type variable, an open record and a nominal type are
    /// left to unification and to the contexts the function requires; the error type is what failed to check.
    let private isKnownNotJoined (ty: Ty) =
        match Unifier.unalias ty with
        | ty when Unifier.isError ty -> false
        | ty when isBuiltIn ty -> false
        | TyVar _
        | TyOpenRecord _
        | TyNominal _ -> false
        | _ -> true

    /// Checks `expr`, `++` on operands of the types `leftTy` and `rightTy` (already made equal), which calls no
    /// definition, and gives its type. On strings or lists it is the built-in operator. An operand of a type known to
    /// have no `++` is an error. So is one of a type variable of the type the function around it writes, which then
    /// does not require `Append` of it: had it, the operator would have called its member. In a function that does not
    /// write its type, operands of a type still a variable are left until the function is inferred, which then
    /// requires `Append` of the variable if it is one of its own (see `ArithmeticInference`); one that it is not is
    /// left as it is.
    let check (state: InferState) (expr: Expression) (leftTy: Ty) (rightTy: Ty) : Result<Ty * InferState, Diagnostic list> =
        let tys = [ leftTy; rightTy ] |> List.map (resolveSoFar state)
        let declaredVar = tys |> List.tryPick (function TyVar({ Name = Some _ } as v) -> Some v | _ -> None)
        let areVariables = tys |> List.forall (function TyVar { Name = None } -> true | _ -> false)
        let resultTy = List.zip [ leftTy; rightTy ] tys |> List.tryFind (snd >> isBuiltIn) |> Option.map fst |> Option.defaultValue leftTy
        match tys |> List.tryFind isKnownNotJoined, OperatorContexts.smallest state operator, declaredVar, state.ContextScope with
        | Some ty, _, _, _ ->
            let tyText = Unifier.tyToDisplayString Ascii ty
            Error [ Diagnostics.at DiagnosticCodes.TypeMismatch expr.Span $"'{tyText}' cannot be joined with ++: only strings, lists and types with an Append can" ]
        | None, Some context, Some v, DefScope(defName, false) ->
            let tyText = Unifier.tyToDisplayString Ascii (TyVar v)
            let message =
                $"'{defName}' joins values of type '{tyText}' with ++, so its type must require {context}({tyText}): add [{context}({tyText})] to the type of '{defName}'"
            let error = Diagnostics.at DiagnosticCodes.MissingContextMembers expr.Span message |> ContextFixes.withAddToType state defName $"{context}({tyText})"
            Ok (resultTy, { state with FailedExprs = (expr.Id, [ error ]) :: state.FailedExprs })
        | None, Some _, None, DefScope(defName, true) when areVariables ->
            let pending = { InDef = defName; Operator = expr.Id; MemberName = operator; OperandTy = leftTy; At = expr.Span }
            Ok (resultTy, { state with PendingArithmetic = pending :: state.PendingArithmetic })
        | _ -> Ok (resultTy, state)
