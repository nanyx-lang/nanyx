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
