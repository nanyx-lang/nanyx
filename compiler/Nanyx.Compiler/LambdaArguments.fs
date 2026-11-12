namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// A lambda passed to a function with a known signature takes its parameters' types from the parameter it is passed
/// as, and from the arguments before it: in `list.map(["a"], { s -> s.length })`, `s` is already a string when
/// `s.length` is checked, so it reads the string's length, not a record's field.
module internal LambdaArguments =
    /// The input type `lambda`, a lambda with the parameters `lambdaArgs`, takes from `paramTy`, the callee's parameter
    /// it is passed as, resolved against the constraints of `state`. None when its parameters are annotated, or the
    /// parameter is not a function, or its input does not say enough to give the lambda's parameters their types. A
    /// lambda written without parameters, `{ ... }`, takes the parameter's input when it is known, and ignores it; when
    /// it is a type variable still, the lambda takes unit, as it does with no expected type. A number literal's own
    /// variable is known to be a number, as `list.map([1, 2], { 0 })` has it.
    let expectedInput (state: InferState) (paramTy: Ty option) (lambda: Expression) (lambdaArgs: (Identifier * TypeExpr option) list) : Ty option =
        let unannotated = lambdaArgs |> List.forall (fun (_, typeOpt: TypeExpr option) -> typeOpt.IsNone)
        match paramTy |> Option.map Unifier.unalias with
        | Some (TyFunc(lambdaInput, _, _)) when (match lambda.Kind with Lambda([], _, false) -> true | _ -> false) ->
            match Unifier.unify state.Constraints |> Result.map (fun subst -> Unifier.apply subst lambdaInput) with
            | Ok resolved when (match Unifier.unalias resolved with TyVar _ -> true | _ -> false) && not (NumericLiterals.isOpen state resolved) -> None
            | Ok resolved -> Some resolved
            | Error _ -> None
        | Some (TyFunc(lambdaInput, _, _)) when unannotated && not (List.isEmpty lambdaArgs) ->
            match Unifier.unify state.Constraints with
            | Ok subst ->
                match Unifier.apply subst lambdaInput with
                // A rest that widening made up is only room for tags no argument has given yet,
                // not tags the lambda must handle: the lambda takes its input from its own
                // patterns, as it does when the arguments come as one tuple (#452).
                | resolved when
                    (match Unifier.unalias resolved with
                     | TyUnion items -> items |> List.exists (function TyVar v -> Unifier.isMadeUpRest v | _ -> false)
                     | _ -> false) -> None
                // A lambda of several parameters needs the input's fields to split between them.
                | resolved when (match Unifier.unalias resolved with TyRecord _ -> true | _ -> false) -> Some resolved
                | resolved when lambdaArgs.Length = 1 -> Some resolved
                | _ -> None
            | Error _ -> None
        | _ -> None

    /// Infers `items`, the arguments of a call written as one tuple, whose callee takes them as `paramTys`. Each is
    /// inferred with `inferItem`, but a lambda with `inferLambda` and the input `expectedInput` gives it, as far as the
    /// items before it say. Those say it only for that: their constraints against their parameters are added to a
    /// copy of the state, and the call still constrains the whole tuple to its input afterwards, as it does any.
    let inferTupleItems
        (inferItem: InferState -> Expression -> Result<TypedExpr * InferState, Diagnostic list>)
        (inferLambda: InferState -> Expression -> (Identifier * TypeExpr option) list -> Expression -> Ty -> Result<TypedExpr * InferState, Diagnostic list>)
        (state: InferState)
        (items: Expression list)
        (paramTys: Ty list)
        : Result<TypedExpr list * InferState, Diagnostic list> =
        let inferAt (inferred: (TypedExpr * Ty) list) (current: InferState) (item: Expression) (paramTy: Ty) =
            match item with
            | Kind (Lambda(lambdaArgs, body, _)) ->
                let earlier =
                    inferred
                    |> List.rev
                    |> List.fold (fun scratch (typed: TypedExpr, earlierTy) -> addAssignableConstraintAt typed.Expr.Span typed.Type earlierTy scratch) current
                match expectedInput earlier (Some paramTy) item lambdaArgs with
                | Some input -> inferLambda current item lambdaArgs body input
                | None -> inferItem current item
            | _ -> inferItem current item
        let inferred, next, errors =
            List.zip items paramTys
            |> List.fold (fun (inferred, current, errors) (item, paramTy) ->
                match inferAt inferred current item paramTy with
                | Ok (typed, next) -> (typed, paramTy) :: inferred, next, errors
                | Error err -> inferred, current, errors @ err)
                ([], state, [])
        if errors.IsEmpty then Ok (inferred |> List.rev |> List.map fst, next) else Error errors
