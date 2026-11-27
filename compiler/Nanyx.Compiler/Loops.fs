/// Loops (see the loops page of the docs). The parser makes `for xs, ys | arms` a call to the built-in "__for" (see
/// `ForSyntax`), and `while condition -> body` one to "__while" (see `WhileSyntax`); a loop is unit, and runs its body
/// where it is written.
module internal NyxCompiler.Loops

open Parser.Program
open NyxCompiler.InferState
open NyxCompiler.TypeResolution

/// Whether a `for` loop reads an iterable of `var`, a type nothing has settled yet, through its `Iterable`: where a
/// signature around the loop requires `Iterable` of it, and in the body of a def that writes no type, which then
/// requires it (see `ContextInference.resolvePending`). Nothing else can require it, so anywhere else, such as the
/// top level or a sequence's step, which is checked outside the def it is written in, the loop reads it as before.
let private readsThroughIterable (env: TypeEnv) (current: InferState) (var: TyVar) =
    let inScope =
        env.Instances
        |> List.exists (fun instance ->
            match resolveSoFar current instance.Context with
            | TyApply(ctxName, TyVar forVar :: _) -> ctxName = Seqs.iterable && forVar.Id = var.Id
            | _ -> false)
    inScope || (match current.ContextScope with DefScope(_, true) -> true | _ -> false)

/// `for xs, ys | arms`, at `expr`: the parser passes the iterables and a lambda of one element of each. Each iterable
/// is anything `Iterable`, and the loop is unit. A `list(a)`, a `seq(a)`, a `map(k, v)` and a `set(a)` are walked as
/// they are; anything else is read as the prelude's `__toSeq(xs)`, its sequence. So is a type nothing has settled where
/// something can require `Iterable` of it (see `readsThroughIterable`): an unannotated def that loops over its
/// parameter, `{ xs -> for xs | x -> ... }`, requires `Iterable` of it. Anywhere else such a type is a list. The loop
/// stops when the shortest iterable does, so a range with no end, `0..`, is only walked in step with one that ends.
let inferFor
    (inferExpr: TypeEnv -> InferState -> Expression -> Result<TypedExpr * InferState, Diagnostic list>)
    (inferLambdaWithExpected: TypeEnv -> InferState -> Expression -> (Identifier * TypeExpr option) list -> Expression -> Ty option -> Ty option -> Result<TypedExpr * InferState, Diagnostic list>)
    (env: TypeEnv) (state: InferState) (expr: Expression)
    (iterables: Expression list) (lambdaExpr: Expression) (lambdaArgs: (Identifier * TypeExpr option) list) (lambdaBody: Expression) =
    let elementOf (current: InferState) (iterable: Expression) =
        inferExpr env current iterable
        |> Result.bind (fun (typedIterable, afterIterable) ->
            // A range of `int`s the loop walks is counted through with its bounds, not read as the sequence it is (see
            // `Seqs`). A range of any other type is the sequence, pulled as any is.
            let afterIterable =
                match iterable.Kind, Unifier.unalias (resolveSoFar afterIterable typedIterable.Type) with
                | RangeSyntax.Call _, Seqs.SeqType (TyPrimitive "int") -> { afterIterable with ReadAs = afterIterable.ReadAs |> Map.remove iterable.Id }
                | _ -> afterIterable
            match Unifier.unalias (resolveSoFar afterIterable typedIterable.Type) with
            | Lists.ListType elementTy -> Ok ((elementTy, false), afterIterable)
            | Seqs.SeqType elementTy -> Ok ((elementTy, (match iterable.Kind with RangeSyntax.Call(_, None) -> true | _ -> false)), afterIterable)
            // A map's elements are its entries, each a key and its value, and a set's its elements: the backend walks
            // the store (see `StoreWalk`).
            | Maps.MapType(key, value) -> Ok ((tupleRecordType [ key; value ], false), afterIterable)
            | Sets.SetType element -> Ok ((element, false), afterIterable)
            | TyVar var when not (readsThroughIterable env afterIterable var) ->
                let elementTy, next = freshVar afterIterable
                Ok ((elementTy, false), addConstraint typedIterable.Type (listType elementTy) next)
            // The first inference is set aside: the iterable is inferred again, at a node of its own, as the argument of
            // the call it is read as.
            | _ ->
                inferAsCall (inferExpr env) current iterable ForSyntax.toSeq [ Expression.from iterable iterable.Kind ]
                |> Result.map (fun (typedSeq, next) ->
                    match Unifier.unalias (resolveSoFar next typedSeq.Type) with
                    | Seqs.SeqType elementTy -> (elementTy, false), next
                    | _ ->
                        let elementTy, next = freshVar next
                        (elementTy, false), next))
    let elements =
        iterables |> List.fold (fun result iterable ->
            result |> Result.bind (fun (found, current) ->
                elementOf current iterable |> Result.map (fun (element, next) -> found @ [ element ], next))) (Ok ([], state))
    match elements with
    | Error err -> Error err
    | Ok (elements, _) when elements |> List.forall snd ->
        let message = "A `for` loop over a range with no end alone never ends: walk it in step with a list or a range with an end, as `for xs, 0.. | x, i` does"
        Error [ Diagnostics.at DiagnosticCodes.EndlessForLoop (List.head iterables).Span message ]
    | Ok (elements, constrained) ->
        let expectedInput = inputTypeFromArgs (List.map fst elements)
        match inferLambdaWithExpected env constrained lambdaExpr lambdaArgs lambdaBody (Some expectedInput) None with
        // The loop runs its body where it is written: what the body uses is used here.
        | Ok (typedLambda, next) ->
            match ScopedCalls.unsuppliedToLoop next typedLambda.Type expr.Span with
            | [] -> Ok (mkTypedExpr expr (TyPrimitive "unit") None None None, LambdaContexts.recordRunHere env typedLambda.Type next)
            | errors -> Error errors
        | Error err -> Error err

/// `while condition -> body`, at `expr`: the condition is a `bool`, and the body's value is thrown away each time
/// round. Neither is a lambda, so what they use is used where the loop is written. A condition whose tests bind names
/// is checked as the loop that binds them in the body (see `IsSyntax.whileAsIf`).
let inferWhile
    (inferExpr: TypeEnv -> InferState -> Expression -> Result<TypedExpr * InferState, Diagnostic list>)
    (env: TypeEnv) (state: InferState) (expr: Expression) (condition: Expression) (body: Expression) =
    let condition, body = IsSyntax.whileAsIf condition body |> Option.defaultValue (condition, body)
    inferExpr env state condition
    |> Result.bind (fun (typedCondition, afterCondition) ->
        let constrained = addConstraintAt condition.Span typedCondition.Type (TyPrimitive "bool") afterCondition
        inferExpr env constrained body
        |> Result.map (fun (_, next) -> mkTypedExpr expr (TyPrimitive "unit") None None None, next))

/// Whether `iterable`, which a `for` loop walks as `__toSeq(iterable)` (see `inferFor`, and `WorkflowDesugaring` for
/// one in a sequence's block), is walked as the sequence it is, its steps' contexts and all: a sequence, or a type
/// nothing has settled where nothing can require `Iterable` of it (see `readsThroughIterable`), as in a sequence's
/// step. Anything else is read through its `Iterable`. The inference is set aside: the iterable is inferred again as
/// the argument of the call it is read as.
let walksAsSequence
    (inferExpr: TypeEnv -> InferState -> Expression -> Result<TypedExpr * InferState, Diagnostic list>)
    (env: TypeEnv) (state: InferState) (iterable: Expression) =
    match inferExpr env state iterable with
    | Ok (typedIterable, afterIterable) ->
        match Unifier.unalias (resolveSoFar afterIterable typedIterable.Type) with
        | Seqs.SeqType _ -> true
        | TyVar var -> not (readsThroughIterable env afterIterable var)
        | _ -> false
    | Error _ -> true
