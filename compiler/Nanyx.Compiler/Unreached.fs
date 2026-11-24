/// An expression that uses a value that never comes, of type `undefined`, such as `crash("no").name` or `x + 1` after
/// `def x = ???`: the value is worked out first and stops the program, so the rest of the expression is never reached.
/// The checker reports nothing more of it (see `Unifier.unify`), and the expression is read as that value alone, which
/// the backend compiles as it does `crash(...)`.
module internal NyxCompiler.Unreached

open Parser.Program
open NyxCompiler.InferState

/// What `expr` works out before anything else of its own, in order, where reading it as one of them is safe: a part
/// worked out before that one has no effect to lose. A call's callee, named, comes before its arguments.
let private firstParts (env: TypeEnv) (expr: Expression) : Expression list =
    match expr.Kind with
    | BinaryOp(_, _, left, right) -> [ left; right ]
    | MemberAccess(baseExpr, _, _) -> [ baseExpr ]
    | Pipe(value, _, _, args) -> value :: args
    | Match(values, _) -> values
    | FunctionCall(name, _, args) ->
        match TypeEnv.tryFind name env with
        | Some scheme when Unifier.isBottom scheme.Type -> [ { expr with Kind = IdentifierExpr name } ]
        | _ -> args
    | _ -> []

/// `state` with `expr`, just inferred, read as its first part that never gives a value, if it has one and every part
/// before it is a name or a literal, which has no effect: `x + 1` as `x`, and `x(1)` as `x`. A part that is a call is
/// read with what was found for it, its callee's type and its instances, and `???`, an error, makes the whole the
/// expression that stops the program, with the error (see `DeferredErrors`).
let readAsNever (env: TypeEnv) (state: InferState) (expr: Expression) : InferState =
    let never (part: Expression) =
        match part.Kind with
        | IdentifierExpr name when part.Id = expr.Id -> TypeEnv.tryFind name env |> Option.exists (fun scheme -> Unifier.isBottom scheme.Type)
        | _ -> state.ExprTypes |> Map.tryFind part.Id |> Option.exists (resolveSoFar state >> Unifier.isBottom)
    let isPure (part: Expression) =
        match part.Kind with
        | IdentifierExpr _
        | LiteralExpr _ -> true
        | _ -> false
    // A part read as its own part that never gives a value is read as that one: the whole is the innermost.
    let rec innermost (part: Expression) =
        match state.ReadAs |> Map.tryFind part.Id with
        | Some read when read.Id <> part.Id -> innermost read
        | Some read -> read
        | None -> part
    let parts = firstParts env expr
    match parts |> List.tryFindIndex never with
    | Some index when parts |> List.take index |> List.forall isPure ->
        let read = innermost parts.[index]
        match state.FailedExprs |> List.tryFind (fst >> (=) read.Id) with
        | Some (_, errors) ->
            { state with FailedExprs = (expr.Id, errors) :: (state.FailedExprs |> List.filter (fst >> (<>) read.Id)) }
        | None ->
            let copied (found: Map<NodeId, 'v>) =
                match found |> Map.tryFind read.Id with
                | Some value -> found |> Map.add expr.Id value
                | None -> found |> Map.remove expr.Id
            match read.Kind with
            | FunctionCall _ ->
                { state with
                    ReadAs = state.ReadAs |> Map.add expr.Id read
                    CalleeTypes = copied state.CalleeTypes
                    ContextInstances = copied state.ContextInstances }
            | _ -> { state with ReadAs = state.ReadAs |> Map.add expr.Id read }
    | _ -> state
