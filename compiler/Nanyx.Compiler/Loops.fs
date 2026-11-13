/// Loops (see the loops page of the docs). The parser makes `for xs, ys | arms` a call to the built-in "__for" (see
/// `ForSyntax`), and `while condition -> body` one to "__while" (see `WhileSyntax`); a loop is unit, and runs its body
/// where it is written.
module internal NyxCompiler.Loops

open Parser.Program
open NyxCompiler.InferState
open NyxCompiler.TypeResolution

/// `for xs, ys | arms`, at `expr`: the parser passes the iterables and a lambda of one element of each. Each iterable
/// is a `list(a)`, a `seq(a)`, a `gen(a)`, a `map(k, v)` or a `set(a)`, and the loop is unit. It stops when the shortest iterable
/// does, so a `gen`, which may never end, is only walked in step with one that does.
let inferFor
    (inferExpr: TypeEnv -> InferState -> Expression -> Result<TypedExpr * InferState, Diagnostic list>)
    (inferLambdaWithExpected: TypeEnv -> InferState -> Expression -> (Identifier * TypeExpr option) list -> Expression -> Ty option -> Ty option -> Result<TypedExpr * InferState, Diagnostic list>)
    (env: TypeEnv) (state: InferState) (expr: Expression)
    (iterables: Expression list) (lambdaExpr: Expression) (lambdaArgs: (Identifier * TypeExpr option) list) (lambdaBody: Expression) =
    let elementOf (current: InferState) (iterable: Expression) =
        inferExpr env current iterable
        |> Result.map (fun (typedIterable, afterIterable) ->
            match Unifier.unalias (resolveSoFar afterIterable typedIterable.Type) with
            | TyApply(("seq" | "list" | "gen") as sequence, [ elementTy ]) -> (elementTy, sequence = "gen"), afterIterable
            // A map's elements are its entries, each a key and its value, in the order of `map.toList`.
            | Maps.MapType(key, value) -> (tupleRecordType [ key; value ], false), afterIterable
            | Sets.SetType element -> (element, false), afterIterable
            | _ ->
                let elementTy, next = freshVar afterIterable
                (elementTy, false), addConstraint typedIterable.Type (listType elementTy) next)
    let elements =
        iterables |> List.fold (fun result iterable ->
            result |> Result.bind (fun (found, current) ->
                elementOf current iterable |> Result.map (fun (element, next) -> found @ [ element ], next))) (Ok ([], state))
    match elements with
    | Error err -> Error err
    | Ok (elements, _) when elements |> List.forall snd ->
        let message = "A `for` loop over a `gen` alone may never end: walk it in step with a list or a bounded range, as `for xs, 0.. | x, i` does"
        Error [ Diagnostics.at DiagnosticCodes.EndlessForLoop (List.head iterables).Span message ]
    | Ok (elements, constrained) ->
        let expectedInput = inputTypeFromArgs (List.map fst elements)
        match inferLambdaWithExpected env constrained lambdaExpr lambdaArgs lambdaBody (Some expectedInput) None with
        // The loop runs its body where it is written: what the body uses is used here.
        | Ok (typedLambda, next) -> Ok (mkTypedExpr expr (TyPrimitive "unit") None None None, LambdaContexts.recordRunHere typedLambda.Type next)
        | Error err -> Error err

/// `while condition -> body`, at `expr`: the condition is a `bool`, and the body's value is thrown away each time
/// round. Neither is a lambda, so what they use is used where the loop is written.
let inferWhile
    (inferExpr: TypeEnv -> InferState -> Expression -> Result<TypedExpr * InferState, Diagnostic list>)
    (env: TypeEnv) (state: InferState) (expr: Expression) (condition: Expression) (body: Expression) =
    inferExpr env state condition
    |> Result.bind (fun (typedCondition, afterCondition) ->
        let constrained = addConstraintAt condition.Span typedCondition.Type (TyPrimitive "bool") afterCondition
        inferExpr env constrained body
        |> Result.map (fun (_, next) -> mkTypedExpr expr (TyPrimitive "unit") None None None, next))
