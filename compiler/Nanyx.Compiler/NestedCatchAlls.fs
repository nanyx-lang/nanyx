namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// A name bound by a catch-all inside a pattern (a list's item, a tuple's or record's field, a tag's payload) that
/// an earlier arm leaves only some tags to. `| [#some(x), ...rest], acc -> ...` takes every list whose first item is
/// a `#some`, so in a later `| [other, ...rest], acc -> other`, `other` is never one: it has its union without
/// `#some`, and with only the rest left, the rest's type, which the match's result can join as its rest. A catch-all
/// for a whole scrutinee is narrowed so in `Infer.inferMatch`.
module internal NestedCatchAlls =
    /// The tags `earlier`, the pattern of an arm before, takes every value of where `later` binds a name by a
    /// catch-all, each with that name. `earlier` must be a catch-all everywhere but on the way to the tag, and take
    /// apart the same shape as `later` on that way, so every value with that tag there that `later` matches it
    /// matches first. `nested` is whether the patterns are inside a value rather than one of the match's own.
    let rec private handled (nested: bool) (earlier: Pattern) (later: Pattern) : (Identifier * string) list =
        match earlier.Kind, later.Kind with
        | TagPattern(tag, payload), IdentifierPattern name when nested && payload |> Option.forall PatternSpace.isCatchAll ->
            [ name, tag ]
        | TuplePattern(earlierItems, earlierRest), TuplePattern(laterItems, laterRest) when
            earlierItems.Length = laterItems.Length && earlierRest = laterRest
            ->
            parts true earlierItems laterItems
        | RecordPattern(earlierName, earlierItems), RecordPattern(laterName, laterItems) when
            earlierName = laterName && earlierItems.Length = laterItems.Length
            ->
            parts true earlierItems laterItems
        // A list of the same length, or of at least that length when the earlier one ends in a catch-all splat.
        | ListPattern(earlierItems, earlierRest), ListPattern(laterItems, laterRest) when
            earlierItems.Length = laterItems.Length
            && (match earlierRest with
                | Some rest -> PatternSpace.isCatchAll rest
                | None -> laterRest.IsNone)
            ->
            parts true earlierItems laterItems
        | TagPattern(earlierTag, Some earlierPayload), TagPattern(laterTag, Some laterPayload) when earlierTag = laterTag ->
            handled true earlierPayload laterPayload
        | _ -> []

    /// `handled` for the parts of two patterns that take values apart alike, where the earlier one has exactly one
    /// part that is not a catch-all.
    and private parts (nested: bool) (earlier: Pattern list) (later: Pattern list) =
        match List.zip earlier later |> List.filter (fst >> PatternSpace.isCatchAll >> not) with
        | [ earlierPart, laterPart ] -> handled nested earlierPart laterPart
        | _ -> []

    /// Each name the arm `armIndex` of `arms` binds by a nested catch-all, with the tags the arms before it take every
    /// value of there.
    let tagsHandledBefore (arms: (Pattern list * Expression) list) (armIndex: int) : Map<Identifier, Set<string>> =
        let patterns, _ = arms.[armIndex]
        arms
        |> List.take armIndex
        |> List.collect (fun (earlier, _) -> if earlier.Length = patterns.Length then parts false earlier patterns else [])
        |> List.groupBy fst
        |> List.map (fun (name, tags) -> name, tags |> List.map snd |> Set.ofList)
        |> Map.ofList

    /// `env` with each name in `handled` (see `tagsHandledBefore`) bound at its type in `env` without the tags it
    /// cannot be, where that is a union with a rest, and the rest variables of those that are, for the match's result
    /// to join as its rests (see `BranchJoin.joinBranchTypes`).
    let narrow (handled: Map<Identifier, Set<string>>) (env: TypeEnv) (state: InferState) : TypeEnv * Set<int> =
        handled
        |> Map.fold (fun (env, rowVars) name tags ->
            match TypeEnv.tryFind name env with
            | Some scheme ->
                match Unifier.unalias (resolveSoFar state scheme.Type) with
                | TyUnion items when items |> List.exists (function TyVar _ -> true | _ -> false) ->
                    let remaining = items |> List.filter (function TyTag(tag, _) -> not (tags.Contains tag) | _ -> true)
                    let narrowed =
                        match remaining with
                        | [ single ] -> single
                        | _ -> TyUnion remaining
                    let rests = remaining |> List.choose (function TyVar v -> Some v.Id | _ -> None)
                    TypeEnv.declare name scheme.Symbol (TypeEnv.mono narrowed) env, Set.union rowVars (Set.ofList rests)
                | _ -> env, rowVars
            | None -> env, rowVars) (env, Set.empty)
