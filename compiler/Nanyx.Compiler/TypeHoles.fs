namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// `_` in a type: a part of an annotation left for inference to fill in, as in `def xs: list(_) = [1, 2]`. Each `_` is
/// a fresh variable of no name (see `typeExprToTy`), unified with whatever the value makes it. Unlike a named variable
/// it is not rigid, and an annotation with one is pre-bound as an unannotated def is, so the def is generalised just as
/// it would be without the annotation: `def id: _ -> _ = { x -> x }` is `a -> a`, and `_ -> _` alone is not `a -> a`.
/// A `_` a context is applied to, as in `[Foo(_)]`, is written as what the value made it once the def is checked (see
/// `settleInContexts`).
module internal TypeHoles =
    /// Whether `typeExpr` leaves any part of itself to inference.
    let hasHoles (typeExpr: TypeExpr) = not (TypeExpr.holes typeExpr).IsEmpty

    /// The error for each `_` in the definition of the type `name`: a type definition says what a type is, so it has
    /// nothing to infer it from.
    let inTypeDef (name: Identifier) (body: TypeExpr) (parameters: (Identifier * TypeExpr option) list) : Diagnostic list =
        body :: (parameters |> List.choose snd)
        |> List.collect TypeExpr.holes
        |> List.map (fun hole ->
            Diagnostics.at DiagnosticCodes.MisplacedTypeHole hole.Span $"'_' cannot be used in the definition of the type '{name}', only in the type of a value")

    /// The input a def's annotation `declared`, read as `declaredTy`, expects of the lambda it types. An input written
    /// as `_` alone is not: the lambda's parameters are typed as if it had no annotation, and `constrainLambda` then
    /// makes the input whatever they are, so `_ -> _` over `{ a, b -> ... }` takes a pair.
    let expectedLambdaInput (declared: TypeExpr) (declaredInput: Ty option) : Ty option =
        match declared.Kind with
        | TypeFunc(Kind TypeHole, _) -> None
        | _ -> declaredInput

    /// `state` with the lambda of type `lambdaTy` that a def's annotation `declared` types unified with the parts of
    /// `declaredTy` it was not checked against: all of it for `_`, and the input for `_ -> ...`.
    let constrainLambda (span: SourceSpan) (declared: TypeExpr option) (declaredTy: Ty) (lambdaTy: Ty) (state: InferState) =
        match declared, Unifier.unalias declaredTy, lambdaTy with
        | Some (Kind TypeHole), _, _ -> addConstraintAt span declaredTy lambdaTy state
        | Some (Kind (TypeFunc(Kind TypeHole, _))), TyFunc(input, _, _), TyFunc(lambdaInput, _, _) -> addConstraintAt span input lambdaInput state
        | _ -> state

    /// `ty` written as a type expression at `span`, where an annotation's variables are `named`: a type variable by the
    /// name it has there, and a primitive or a type this module names by its name. None for any other type, and for a
    /// variable the annotation does not name.
    let rec private written (state: InferState) (named: (Identifier * Ty) list) (span: SourceSpan) (ty: Ty) : TypeExpr option =
        let applied name args =
            let writtenArgs = args |> List.choose (written state named span)
            if writtenArgs.Length <> List.length args then None
            elif writtenArgs.IsEmpty then Some (TypeExpr.at span (TypeName name))
            else Some (TypeExpr.at span (TypeApply(name, writtenArgs)))
        match ty with
        | TyVar var ->
            named
            |> List.tryPick (fun (name, namedTy) -> match namedTy with TyVar namedVar when namedVar.Id = var.Id -> Some name | _ -> None)
            |> Option.map (fun name -> TypeExpr.at span (TypeVar name))
        | TyPrimitive "unit" -> Some (TypeExpr.at span TypeUnit)
        | TyPrimitive name -> Some (TypeExpr.at span (TypeName name))
        | TyApply(name, args)
        | TyAlias(name, args, _) -> applied name args
        | TyNominal(id, args, _, _) ->
            match state.TypeDefs |> Map.tryFind id.Name with
            | Some info when info.Parameters.Length = args.Length -> applied id.Name args
            | _ -> None
        | _ -> None

    /// `typeOpt`, the annotation of the def `name`, with each `_` its contexts are applied to written as the type the
    /// def's value made it, as `a` in `[Foo(_)] a, a -> a` whose body calls `Foo`'s members with `a`s: a call reads a
    /// context's arguments from the annotation, so each `_` must stand for a type the annotation can name. One the value
    /// left as a variable of its own, or made a type it cannot name, is an error at the `_`.
    let settleInContexts (state: InferState) (name: Identifier) (typeOpt: TypeExpr option) : Result<TypeExpr option, Diagnostic list> =
        match typeOpt with
        | Some ({ Kind = TypeWithContext(contexts, inner) } as annotation) when contexts |> List.exists hasHoles ->
            let named = state.TypeVars |> Map.toList |> List.map (fun (varName, var) -> varName, resolveSoFar state (TyVar var))
            let holes =
                contexts
                |> List.collect TypeExpr.holes
                |> List.map (fun hole ->
                    hole, state.ExprTypes |> Map.tryFind hole.Id |> Option.bind (resolveSoFar state >> written state named hole.Span))
            let errors =
                holes
                |> List.choose (function
                    | hole, None ->
                        Some (Diagnostics.at DiagnosticCodes.UndeterminedContextType hole.Span
                                  $"The body of '{name}' does not make this '_' a type its parameters or result name, so no call can supply the context: write the type in place of '_'")
                    | _, Some _ -> None)
            let settled = holes |> List.choose (fun (hole, settledExpr) -> settledExpr |> Option.map (fun e -> hole.Id, e)) |> Map.ofList
            let rec settle (typeExpr: TypeExpr) =
                match settled |> Map.tryFind typeExpr.Id with
                | Some settledExpr -> settledExpr
                | None -> TypeExpr.mapChildren settle typeExpr
            if errors.IsEmpty then Ok (Some { annotation with Kind = TypeWithContext(List.map settle contexts, inner) })
            else Error errors
        | _ -> Ok typeOpt

    /// `state` with `expr`, a def's value of type `actualTy`, checked against `declaredTy`, the type its annotation
    /// `declared` gives it. A lambda written for a function type was checked against it as it was inferred, so only
    /// what `constrainLambda` covers is left; anything else, a lambda written for some other type among them, must be
    /// assignable to it.
    let constrainToAnnotation (expr: Expression) (declared: TypeExpr option) (declaredTy: Ty) (actualTy: Ty) (state: InferState) =
        match expr.Kind, declared, Unifier.unalias declaredTy with
        | Lambda _, Some (Kind TypeHole), _
        | Lambda _, _, TyFunc _ -> constrainLambda expr.Span declared declaredTy actualTy state
        | _ -> Visibility.addDeclaredConstraintAt expr.Span actualTy declaredTy state
