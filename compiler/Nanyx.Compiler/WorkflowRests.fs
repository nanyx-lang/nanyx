/// The rest of a workflow block after an `if` or `match` with keywords in its branches, which the workflow rewrite
/// binds to a local each branch goes on to (see `WorkflowDesugaring.desugarBranching`):
///
///   def __rest_1 = { r -> <rest of the block> }
///   match value
///     | #error(_) -> return 0
///     | other -> __rest_1(other)
///
/// The branches' values meet at the rest's parameter, so they are joined as the branches of an `if` or `match` are
/// (see `BranchJoin`), not made the same type: `r` is the union of what the branches give it.
module internal NyxCompiler.WorkflowRests

open Parser.Program
open NyxCompiler.InferState

/// What the names of the locals the rewrite binds the rest of a block to start with. No name written in the source
/// can start with it.
let prefix = "__rest_"

let isRest (name: Identifier) = name.StartsWith prefix

/// `state` once a call to `name` has passed arguments of the types `argTys`, if `name` is a rest.
let noteCall (name: Identifier) (argTys: Ty list) (state: InferState) : InferState =
    if isRest name then
        let earlier = state.RestArguments |> Map.tryFind name |> Option.defaultValue []
        { state with RestArguments = state.RestArguments |> Map.add name (earlier @ argTys) }
    else
        state

/// `state` once the match at `matchId`, whose arms are `arms` and head is at `headSpan`, has been checked against
/// scrutinees of the types `scrutineeTypes` and found to leave nothing out. A scrutinee whose type was still a variable,
/// as a rest's parameter is until the branches have been inferred, is checked again once it is known (see
/// `joinArguments`).
let noteOpenMatch (matchId: NodeId) (arms: MatchArm list) (scrutineeTypes: Ty list) (headSpan: SourceSpan) (state: InferState) : InferState =
    if scrutineeTypes |> List.exists (function TyVar _ -> true | _ -> false) then
        { state with OpenMatches = { Match = matchId; Arms = arms; Scrutinees = scrutineeTypes; Head = headSpan } :: state.OpenMatches }
    else
        state

/// `state` with each match noted open on `parameter` checked again, now that the parameter is known: one that leaves a
/// value out is reported as any match that does is.
let private checkOpenMatches (parameter: Ty) (state: InferState) : InferState =
    let onParameter, others =
        state.OpenMatches
        |> List.partition (fun open' -> open'.Scrutinees |> List.contains parameter)
    onParameter
    |> List.fold (fun current open' ->
        match MatchExhaustiveness.check open'.Arms (open'.Scrutinees |> List.map (resolveSoFar current)) open'.Head with
        | [] -> current
        | unhandled -> { current with UnhandledMatches = (open'.Match, unhandled) :: current.UnhandledMatches })
        { state with OpenMatches = others }

/// `state` once the block `statements` has been inferred in `env`: the parameter of each rest it binds is the join of
/// the arguments its calls passed. The constraint is the newest, so it is solved before each argument is checked
/// against the parameter. A match on the parameter in the rest is checked then, since its arms were checked before
/// the branches said what it is.
let joinArguments (env: TypeEnv) (statements: Statement list) (state: InferState) : InferState =
    statements
    |> List.fold (fun current statement ->
        match statement.Kind, current.RestArguments with
        | DefStatement(_, name, _, _), arguments when isRest name && arguments.ContainsKey name ->
            let current = { current with RestArguments = arguments.Remove name }
            match TypeEnv.tryFind name env |> Option.map (fun scheme -> Unifier.unalias scheme.Type) with
            // The parameter as the rest's type has it: resolved, it would already be the first argument's type.
            | Some (TyFunc(parameter, _, _)) ->
                let joined, next = BranchJoin.joinBranchTypes Set.empty current arguments[name]
                let joinedState = addConstraint parameter joined next
                match parameter with
                | TyVar _ -> checkOpenMatches parameter joinedState
                | _ -> joinedState
            | _ -> current
        | _ -> current) state
