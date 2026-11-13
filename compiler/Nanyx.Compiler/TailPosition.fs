namespace NyxCompiler

open Parser.Program

/// Which calls are in tail position. The language guarantees that a call in tail position uses no stack
/// (`docs/src/content/work-in-progress/tail-calls.md`), so the backend lowers these calls to `return_call`, and the official linter
/// reports a recursive call that is not one of them.
module TailPosition =
    /// The calls in tail position in `body`, the body of a function, by node: calls, pipes and context member calls
    /// that are the last thing the function does. The branches of an `if`, the arms of a `match` or an `except`, the
    /// last expression of a block, the body of a `use ... in` and a lambda's annotated result are in tail position when
    /// the expression they belong to is; nothing else is.
    ///
    /// A lambda with no parameters is a value, so its body is not in tail position of the function it is written in.
    let callsIn (body: Expression) : Set<NodeId> =
        let rec walk (calls: Set<NodeId>) (expr: Expression) =
            match expr.Kind with
            | FunctionCall _
            | Pipe _
            | ContextMemberCall _ -> calls |> Set.add expr.Id
            | IfExpr(_, ifTrue, ifFalse) -> walk (walk calls ifTrue) ifFalse
            // A lambda's annotated result is the value it names (see `LambdaResultSyntax`).
            | LambdaResultSyntax.Annotated(_, inner) -> walk calls inner
            | Match(_, arms)
            | Except(_, arms) -> arms |> List.map snd |> List.fold walk calls
            | Block statements ->
                match statements |> List.tryLast with
                | Some (Kind (ExprStatement last)) -> walk calls last
                | _ -> calls
            | UseIn(_, inner) -> walk calls inner
            | _ -> calls
        walk Set.empty body
