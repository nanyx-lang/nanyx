namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// `_` in a type: a part of an annotation left for inference to fill in, as in `def xs: list(_) = [1, 2]`. Each `_` is
/// a fresh variable of no name (see `typeExprToTy`), unified with whatever the value makes it. Unlike a named variable
/// it is not rigid, and an annotation with one is pre-bound as an unannotated def is, so the def is generalised just as
/// it would be without the annotation: `def id: _ -> _ = { x -> x }` is `a -> a`, and `_ -> _` alone is not `a -> a`.
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

    /// `state` with `expr`, a def's value of type `actualTy`, checked against `declaredTy`, the type its annotation
    /// `declared` gives it. A lambda written for a function type was checked against it as it was inferred, so only
    /// what `constrainLambda` covers is left; anything else, a lambda written for some other type among them, must be
    /// assignable to it.
    let constrainToAnnotation (expr: Expression) (declared: TypeExpr option) (declaredTy: Ty) (actualTy: Ty) (state: InferState) =
        match expr.Kind, declared, Unifier.unalias declaredTy with
        | Lambda _, Some (Kind TypeHole), _
        | Lambda _, _, TyFunc _ -> constrainLambda expr.Span declared declaredTy actualTy state
        | _ -> Visibility.addDeclaredConstraintAt expr.Span actualTy declaredTy state
