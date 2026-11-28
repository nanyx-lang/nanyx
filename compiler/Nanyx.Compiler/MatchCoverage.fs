/// Which arms of a `match` can never be chosen, from the pattern space exhaustiveness checking uses (see `PatternSpace`):
/// what the official linter's `unreachableArms` reports, and what a linter written in Nanyx asks through `Analysis`'s
/// `coveringArms`.
module NyxCompiler.MatchCoverage

open Parser.Program

/// Whether the arm at `index` is one the parser made up: the `()` an `if ... is` without an `else` stands for, which is
/// reported at the `then` branch, where nothing can be done about it.
let isMadeUp (arms: MatchArm list) (index: int) =
    match arms with
    | [ _, thenBranch; _, (Kind UnitExpr as madeUp) ] -> index = 1 && madeUp.Span = thenBranch.Span
    | _ -> false

/// Each arm of the `match` on `values` with `arms`, by its position, with the patterns it matches the values with.
let rows (values: Expression list) (arms: MatchArm list) : (int * Pattern list * Expression) list =
    // A lone `else` or `_` matches whatever every value is, as `desugar` has it for the compiler, and so does any other
    // lone pattern that matches every tuple of them. An arm with another lone pattern is matched against the tuple,
    // whose type isn't recorded, so its match isn't looked at.
    let rowOf (patterns: Pattern list) =
        match PatternSpace.ofArm values.Length patterns with
        | [ pattern ] when values.Length > 1 && PatternSpace.isCatchAll pattern -> List.replicate values.Length (Pattern.at pattern.Span WildcardPattern)
        | row -> row
    arms |> List.mapi (fun index (patterns, body) -> index, rowOf patterns, body)

/// The positions of the arms before the arm at `index` of the `match` on `values` with `arms` that between them match
/// every value it does, given the values' types `types`; `None` when that arm can match, or isn't looked at.
let covering (types: Map<NodeId, Ty>) (values: Expression list) (arms: MatchArm list) (index: int) : int list option =
    let indexed = rows values arms
    let comparable = indexed |> List.forall (fun (_, patterns, _) -> patterns.Length = values.Length)
    match List.tryItem index indexed with
    | Some(_, patterns, _) when comparable && index > 0 && not patterns.IsEmpty && not (isMadeUp arms index) ->
        let earlier = indexed |> List.take index |> List.map (fun (i, others, _) -> i, others)
        PatternSpace.coveringArms earlier (values |> List.map (fun value -> types |> Map.tryFind value.Id)) patterns
    | _ -> None
