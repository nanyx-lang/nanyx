/// The members of a builder's context that a builder's block reads (see `WorkflowDesugaring`): a keyword `k` calls the
/// member `k`, a block that falls off its end without `return` is the member `zero` there, and a loop whose body uses
/// a keyword is run by the member `for`. The builder runs the block under its context, which the block's lambda
/// requires, so each member is in scope in the block by its name.
module internal NyxCompiler.WorkflowMembers

open Parser.Program
open NyxCompiler.InferState

/// Whether `name`, read at the node `node`, is a member of a builder's context that no context in scope has: a name
/// desugaring made up for a builder's block that `env` does not bind to a context's member.
let isMissing (env: TypeEnv) (state: InferState) (node: NodeId) (name: Identifier) =
    state.WorkflowMemberPlaces.ContainsKey node
    && not (TypeEnv.tryFind name env |> Option.exists (fun scheme -> scheme.ProvidedBy.IsSome))

/// The error for reading `name` at the node `node`, a member of a builder's context that no context in scope has.
let missing (state: InferState) (node: NodeId) (name: Identifier) : Diagnostic =
    let place, builder = state.WorkflowMemberPlaces.[node]
    let message =
        if name = WorkflowSyntax.zeroMember then
            $"This `{builder}` block can end here without `return`, and the context `{builder}` runs it under has no `zero` to be its value then: end the block with `return`, or give the context a `zero`"
        elif name = WorkflowSyntax.forMember then
            $"This loop's body uses a workflow keyword, and the context `{builder}` runs its block under has no `for` to run it: handle the value with a `match` in the loop's body, or give the context a `for`"
        else
            $"`{name}` is a keyword of this `{builder}` block, but the context `{builder}` runs it under has no member `{name}`: give the context a `{name}`, or write the block after a builder that has one"
    Diagnostics.at DiagnosticCodes.InvalidWorkflow place message

type private Infer = TypeEnv -> InferState -> Expression -> Result<TypedExpr * InferState, Diagnostic list>

/// `expr`, the call of a builder's `for` member a loop whose body uses a keyword is run by, when the loop is over a map
/// or a set: the member takes a list, so the loop's `iterable` is read as the list `map.toList` or `set.toList` gives,
/// and the call is recorded in `ReadAs` to be written with it (#2672). The iterable is inferred once to see what it is,
/// and that inference set aside, as a spread's is (see `CollectionLiterals.inferSpread`). None for any other iterable,
/// which the member is given as it is.
let loopOverStore (infer: Infer) (env: TypeEnv) (state: InferState) (expr: Expression) (iterable: Expression) (others: Expression list) =
    match infer env state iterable with
    | Error _ -> None
    | Ok (typed, seen) ->
        match Unifier.unalias (resolveSoFar seen typed.Type) with
        | Maps.MapType _ -> Some Maps.toList
        | Sets.SetType _ -> Some Sets.toList
        | _ -> None
        |> Option.map (fun toList ->
            let asList = Expression.from iterable (FunctionCall(toList, None, [ iterable ]))
            let call = { expr with Kind = FunctionCall(WorkflowSyntax.forMember, None, asList :: others) }
            infer env state call |> Result.map (fun (typed, next) -> { typed with Expr = expr }, { next with ReadAs = next.ReadAs |> Map.add expr.Id call }))
