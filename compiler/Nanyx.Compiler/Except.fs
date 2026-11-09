namespace NyxCompiler

open Parser.Program

/// `value except | pattern -> replacement ...` as the `match` it stands for. The arms are the match's arms, and one
/// more arm after them passes every case they do not match through unchanged:
///
///   match value
///     | pattern -> replacement
///     | __except_0 -> __except_0
///
/// The pass-through arm's name is bound after the arms have taken their tags, so it has the union without them
/// (see the `Match` case of `inferExpr`), and the result is the arms' values joined with what is left:
/// `#some(int) | #invalidInt` except `#invalidInt -> #bad` is `#some(int) | #bad`.
///
/// That only takes away tags the arms handle in full. So that `#error(#notFound)` takes `#notFound` out of the
/// `#error`'s payload, the arms on a tag whose payloads are all tag patterns become an `except` of their own on the
/// payload, whose pass-through arm wraps what it passes in the tag again:
///
///   match value
///     | #error(__except_1) -> match __except_1
///         | #notFound -> replacement
///         | __except_1 -> #error(__except_1)
///     | __except_0 -> __except_0
module Except =
    let private passThroughName (depth: int) = $"__except_{depth}"

    let private isCatchAllArm (patterns: Pattern list, _) =
        match patterns with
        | [ pattern ] -> PatternSpace.isCatchAll pattern
        | _ -> false

    let private tagArm (patterns: Pattern list, body: Expression) =
        match patterns with
        | [ Kind (TagPattern(name, payload)) ] -> Some (name, payload, body)
        | _ -> None

    /// The match `value except arms` stands for, `depth` excepts deep, where a case no arm matches becomes
    /// `passOn` of it. The match is made up at `origin`.
    let rec private asMatchAt (origin: Expression) (depth: int) (passOn: Expression -> Expression) (value: Expression) (arms: MatchArm list) : ExpressionKind =
        let made kind = Expression.from origin kind
        let madePattern kind = Pattern.at origin.Span kind
        if arms |> List.exists isCatchAllArm then
            Match([ value ], arms)
        else
            let tagArms = arms |> List.choose tagArm
            let nestedTags =
                if tagArms.Length < arms.Length then
                    Set.empty
                else
                    tagArms
                    |> List.groupBy (fun (name, _, _) -> name)
                    |> List.filter (fun (_, group) ->
                        group |> List.forall (function _, Some (Kind (TagPattern _)), _ -> true | _ -> false))
                    |> List.map fst
                    |> Set.ofList
            // Each tag with nested arms has one arm, where its first arm was.
            let nestedArm (name: string) =
                let payloadName = passThroughName (depth + 1)
                let payloadArms =
                    tagArms
                    |> List.choose (fun (tag, payload, body) ->
                        match payload with
                        | Some payload when tag = name -> Some ([ payload ], body)
                        | _ -> None)
                let rewrap inner = made (TagExpr(name, Some inner))
                let payload = made (IdentifierExpr payloadName)
                [ madePattern (TagPattern(name, Some (madePattern (IdentifierPattern payloadName)))) ],
                made (asMatchAt origin (depth + 1) (passOn << rewrap) payload payloadArms)
            let arms' =
                arms
                |> List.fold (fun (acc, seen: Set<string>) arm ->
                    match tagArm arm with
                    | Some (name, _, _) when nestedTags.Contains name ->
                        if seen.Contains name then acc, seen else acc @ [ nestedArm name ], seen.Add name
                    | _ -> acc @ [ arm ], seen) ([], Set.empty)
                |> fst
            let rest = passThroughName depth
            let passThrough = [ madePattern (IdentifierPattern rest) ], passOn (made (IdentifierExpr rest))
            Match([ value ], arms' @ [ passThrough ])

    /// The match `value except arms`, written at `origin`, stands for.
    let asMatch (origin: Expression) (value: Expression) (arms: MatchArm list) : ExpressionKind =
        asMatchAt origin 0 id value arms
