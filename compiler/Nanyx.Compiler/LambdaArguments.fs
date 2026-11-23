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
        // A conflict elsewhere in the module does not stop the rest of the constraints saying what the input is.
        let resolved = resolveSoFar state
        match paramTy |> Option.map Unifier.unalias with
        | Some (TyFunc(lambdaInput, _, _)) when (match lambda.Kind with Lambda([], _, false) -> true | _ -> false) ->
            match resolved lambdaInput with
            | input when (match Unifier.unalias input with TyVar _ -> true | _ -> false) && not (NumericLiterals.isOpen state input) -> None
            | input -> Some input
        | Some (TyFunc(lambdaInput, _, _)) when unannotated && not (List.isEmpty lambdaArgs) ->
            match resolved lambdaInput with
            // A rest that widening made up is only room for tags no argument has given yet,
            // not tags the lambda must handle: the lambda takes its input from its own
            // patterns, as it does when the arguments come as one tuple (#452).
            | input when
                (match Unifier.unalias input with
                 | TyUnion items -> items |> List.exists (function TyVar v -> Unifier.isMadeUpRest v | _ -> false)
                 | _ -> false) -> None
            // A lambda of several parameters needs the input's fields to split between them.
            | input when (match Unifier.unalias input with TyRecord _ -> true | _ -> false) -> Some input
            | input when lambdaArgs.Length = 1 -> Some input
            | _ -> None
        | _ -> None

    /// Infers `items`, the arguments of a call written as one tuple, whose callee takes them as `paramTys`. Each is
    /// inferred with `inferItem`, given its parameter's type, but a lambda with `inferLambda`, the input `expectedInput` gives it, as far as the
    /// items before it say, and its parameter's type. Those say it only for that: their constraints against their
    /// parameters are added to a copy of the state, and the call still constrains the whole tuple to its input
    /// afterwards, as it does any.
    let inferTupleItems
        (inferItem: InferState -> Expression -> Ty -> Result<TypedExpr * InferState, Diagnostic list>)
        (inferLambda: InferState -> Expression -> (Identifier * TypeExpr option) list -> Expression -> Ty option -> Ty -> Result<TypedExpr * InferState, Diagnostic list>)
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
                inferLambda current item lambdaArgs body (expectedInput earlier (Some paramTy) item lambdaArgs) paramTy
            | _ -> inferItem current item paramTy
        let inferred, next, errors =
            List.zip items paramTys
            |> List.fold (fun (inferred, current, errors) (item, paramTy) ->
                match inferAt inferred current item paramTy with
                | Ok (typed, next) -> (typed, paramTy) :: inferred, next, errors
                | Error err -> inferred, current, errors @ err)
                ([], state, [])
        if errors.IsEmpty then Ok (inferred |> List.rev |> List.map fst, next) else Error errors

    /// Where a call's arguments written as one tuple, `typedItems`, are reported when the tuple, `tupleTy`, does not
    /// fit the callee's input, `inputTy`, whose parameters are `paramTys`: at the first argument that does not fit with
    /// those before it, rather than at the whole tuple, `tupleSpan`. When that is a lambda whose result would fit
    /// without an argument before it, the error is that argument's, which fixed the type the lambda's parameters took
    /// (see `inferTupleItems`) too narrowly: in `map.fold(m, #none, { first, key, w -> if w > 0 then #some(w) else first })`,
    /// the `#none` (#2452). Only where it is reported changes, not which constraints there are or their order.
    let reportedSpan (state: InferState) (tupleSpan: SourceSpan) (typedItems: TypedExpr list) (paramTys: Ty list) (tupleTy: Ty) (inputTy: Ty) : SourceSpan =
        // A number literal among them that nothing has settled yet is taken to be of its default type, as it is once
        // the def is generalised, when the mismatch would be reported. An error earlier in the module is set aside
        // (see `Solving.agreeing`).
        let settled = NumericLiterals.settleWithin (typedItems |> List.map _.Type) state
        let agreeing = Solving.agreeing settled.Constraints
        // The constraints are solved first to last, as the tuple's would be.
        let fits (constraints: Constraint list) = Solving.unify (constraints @ agreeing) |> Result.isOk
        let assignable given expected : Constraint = (given, expected, Assignable, tupleSpan)
        let items = List.zip typedItems paramTys
        let upTo count = items |> List.truncate count |> List.map (fun (typed: TypedExpr, paramTy) -> assignable typed.Type paramTy)
        if items.IsEmpty || fits [ assignable tupleTy inputTy ] then tupleSpan
        else
            match [ 1 .. items.Length ] |> List.tryFind (fun count -> not (fits (upTo count))) with
            | None -> tupleSpan
            | Some count ->
                let typed, paramTy = items.[count - 1]
                match typed.Expr, Unifier.unalias typed.Type, Unifier.unalias paramTy with
                | Kind (Lambda _), TyFunc(_, output, _), TyFunc(_, expectedOutput, _) when fits [ assignable output expectedOutput ] ->
                    [ 1 .. count - 1 ]
                    |> List.tryFind (fun earlier -> not (fits (upTo earlier @ [ assignable output expectedOutput ])))
                    |> Option.map (fun earlier -> (fst items.[earlier - 1]).Expr.Span)
                    |> Option.defaultValue typed.Expr.Span
                | _ -> typed.Expr.Span

    /// The fields of `arg`, the argument of a call to a constructor whose input is `input`, when it is a record written
    /// with each field named and each a field of `input`: their names, their values and the types `input` gives them.
    /// So `Log(log = { m -> println(m) })` gives `m` the input of `log`'s type, as `inferTupleItems` does.
    let namedFields (arg: Expression) (input: Ty) : (string list * Expression list * Ty list) option =
        match arg.Kind, recordFieldsOf input with
        | RecordExpr fields, Some members ->
            let named = fields |> List.choose (function NamedField(name, value) when members.ContainsKey name -> Some (name, value) | _ -> None)
            if named.Length = fields.Length then
                Some (named |> List.map fst, named |> List.map snd, named |> List.map (fun (name, _) -> members.[name]))
            else None
        | _ -> None
