namespace NyxCompiler

open Parser.Program

/// Which values patterns match, as far as the compiler reasons about it: what the exhaustiveness check
/// (`Infer`) and the `unreachableArms` rule (`Linters`) both ask. A pattern it cannot reason about (a guard, a list
/// with a splat in the middle) is taken to match no value for sure, so neither ever reports something on its account
/// that might not be so.
module PatternSpace =
    /// Whether `pattern` matches every value of its type. `(a, _)` matches every tuple: a tuple pattern is irrefutable
    /// when each element is. So is a record pattern (`$Money(pence)`, `(x = a)`) whose parts all are: the type says the
    /// value has that shape.
    let rec isCatchAll (pattern: Pattern) =
        match pattern.Kind with
        | WildcardPattern
        | ElsePattern
        | IdentifierPattern _ -> true
        | TuplePattern elements
        | RecordPattern(_, elements) -> elements |> List.forall isCatchAll
        | RecordMemberPattern members -> members |> List.forall (snd >> isCatchAll)
        | LiteralPattern _
        | ListPattern _
        | ListSplatMiddle _
        | RangePattern _
        | GuardPattern _
        | TagPattern _ -> false

    /// The tag `pattern` matches, if it is a tag pattern.
    let tagName (pattern: Pattern) =
        match pattern.Kind with
        | TagPattern(tagName, _) -> Some tagName
        | _ -> None

    let rec private unwrap (ty: Ty) =
        match ty with
        | TyNominal(_, underlying, _)
        | TyAlias(_, _, underlying) -> unwrap underlying
        | _ -> ty

    /// The tags of `ty`, if it is a union of tags and nothing else (no rest left open).
    let tagUnion (ty: Ty) : Set<string> option =
        match unwrap ty with
        | TyUnion items when items |> List.forall (function TyTag _ -> true | _ -> false) ->
            items |> List.choose (function TyTag(name, _) -> Some name | _ -> None) |> Set.ofList |> Some
        | _ -> None

    let isList (ty: Ty) =
        match unwrap ty with
        | TyApply("list", _) -> true
        | _ -> false

    let isRecord (ty: Ty) =
        match unwrap ty with
        | TyRecord _ -> true
        | _ -> false

    /// Whether list patterns at one position of a match cover every list, as the exhaustiveness check counts it: an
    /// empty list and one with a splat.
    let listsCoverAll (patterns: Pattern list) =
        let hasEmpty = patterns |> List.exists (function Kind (ListPattern([], None)) -> true | _ -> false)
        let hasSplat = patterns |> List.exists (function Kind (ListPattern(_, Some _)) -> true | _ -> false)
        hasEmpty && hasSplat

    /// The tag an arm whose patterns are `patterns` handles in full at `index`: its pattern there is that tag with a
    /// catch-all payload, or none, and its pattern at every other position is a catch-all. Every value with that tag
    /// there goes to the arm.
    let tagHandled (patterns: Pattern list) (index: int) : string option =
        let othersCatchAll = patterns |> List.indexed |> List.forall (fun (i, pattern) -> i = index || isCatchAll pattern)
        match List.tryItem index patterns with
        | Some (Kind (TagPattern(tagName, payload))) when othersCatchAll && payload |> Option.forall isCatchAll -> Some tagName
        | _ -> None

    let private intOf (expr: Expression) =
        match expr.Kind with
        | LiteralExpr (IntLit value) -> Some value
        | _ -> None

    /// Whether `general` matches every value `specific` matches.
    let rec subsumes (general: Pattern) (specific: Pattern) : bool =
        let all (generals: Pattern list) (specifics: Pattern list) =
            generals.Length = specifics.Length && List.forall2 subsumes generals specifics
        if isCatchAll general then
            true
        else
            match general.Kind, specific.Kind with
            | LiteralPattern literal, LiteralPattern other -> literal = other
            | RangePattern(low, high), LiteralPattern (IntLit value) ->
                match intOf low, intOf high with
                | Some low, Some high -> low <= value && value <= high
                | _ -> false
            | RangePattern(low, high), RangePattern(otherLow, otherHigh) ->
                match intOf low, intOf high, intOf otherLow, intOf otherHigh with
                | Some low, Some high, Some otherLow, Some otherHigh -> low <= otherLow && otherHigh <= high
                | _ -> false
            | TagPattern(name, payload), TagPattern(otherName, otherPayload) when name = otherName ->
                match payload, otherPayload with
                | None, None -> true
                | Some payload, Some otherPayload -> subsumes payload otherPayload
                | Some payload, None -> isCatchAll payload
                | None, Some _ -> false
            | TuplePattern elements, TuplePattern others -> all elements others
            | RecordPattern(name, elements), RecordPattern(otherName, others) -> name = otherName && all elements others
            | RecordMemberPattern members, RecordMemberPattern others ->
                members
                |> List.forall (fun (name, pattern) ->
                    match others |> List.tryFind (fst >> (=) name) with
                    | Some (_, other) -> subsumes pattern other
                    | None -> isCatchAll pattern)
            // `[a, b]` matches lists of exactly two; `[a, ...rest]` lists of at least one.
            | ListPattern(elements, None), ListPattern(others, None) -> all elements others
            | ListPattern(elements, Some _), ListPattern(others, _) ->
                others.Length >= elements.Length && all elements (others |> List.take elements.Length)
            | _ -> false

    /// Whether `pattern` matches every value of one kind its type has, whatever is inside it: a tag with a catch-all
    /// payload or none, a boolean, or a list of some length (or more) with catch-alls for elements.
    let private handlesInFull (pattern: Pattern) =
        match pattern.Kind with
        | TagPattern(_, payload) -> payload |> Option.forall isCatchAll
        | LiteralPattern (BoolLit _) -> true
        | ListPattern(elements, _) -> elements |> List.forall isCatchAll
        | _ -> false

    /// Whether arms that each handle in full one kind of value (`handlesInFull`) at one position, the patterns
    /// `handled`, whatever is at the others, leave no value of `ty` there: every tag of its union, both booleans, or
    /// every length of list.
    let private coversType (ty: Ty option) (handled: Pattern list) =
        let tags = handled |> List.choose tagName |> Set.ofList
        let literals = handled |> List.choose (function Kind (LiteralPattern literal) -> Some literal | _ -> None) |> Set.ofList
        // The lengths of list every one of the patterns matches: exactly n, or n and more.
        let exact = handled |> List.choose (function Kind (ListPattern(elements, None)) when List.forall isCatchAll elements -> Some elements.Length | _ -> None) |> Set.ofList
        let atLeast = handled |> List.choose (function Kind (ListPattern(elements, Some _)) when List.forall isCatchAll elements -> Some elements.Length | _ -> None)
        let everyList = atLeast |> List.exists (fun least -> List.forall exact.Contains [ 0 .. least - 1 ])
        match ty |> Option.map unwrap with
        | Some (TyPrimitive "bool") -> literals.Contains (BoolLit true) && literals.Contains (BoolLit false)
        | Some itemTy when isList itemTy -> everyList
        | Some itemTy ->
            match tagUnion itemTy with
            | Some union -> not union.IsEmpty && Set.isSubset union tags
            | None -> false
        | None -> everyList

    /// The earlier arms that between them match every value the arm with `patterns` does, so that it can never match,
    /// or `None` if they do not (or it cannot be told). `earlier` are the arms before it, with their indexes, and
    /// `types` the types of the values matched, by position, where known. An earlier arm that matches everything the
    /// arm does is enough; so are arms that each handle a value in full at one position (a tag, a boolean, a length
    /// of list, with catch-alls elsewhere), when the arm's own pattern there matches only values they handle.
    let coveringArms (earlier: (int * Pattern list) list) (types: Ty option list) (patterns: Pattern list) : int list option =
        let single =
            earlier
            |> List.tryFind (fun (_, others) -> others.Length = patterns.Length && List.forall2 subsumes others patterns)
            |> Option.map (fst >> List.singleton)
        let byPosition () =
            patterns
            |> List.indexed
            |> List.tryPick (fun (index, pattern) ->
                // The earlier arms that handle one kind of value in full here, with catch-alls at every other position.
                let handling =
                    earlier
                    |> List.choose (fun (armIndex, others) ->
                        let othersCatchAll = others |> List.indexed |> List.forall (fun (i, other) -> i = index || isCatchAll other)
                        match List.tryItem index others with
                        | Some other when othersCatchAll && others.Length = patterns.Length && handlesInFull other -> Some (armIndex, other)
                        | _ -> None)
                let ty = types |> List.tryItem index |> Option.flatten
                // A single earlier arm that covers a tag or a literal the arm matches is found above; here the arm's
                // pattern matches any value of the position (or any list), which only the handled values together cover.
                let matchesAny =
                    match pattern.Kind with
                    | ListPattern _ -> true
                    | _ -> isCatchAll pattern
                if matchesAny && coversType ty (handling |> List.map snd) then Some (handling |> List.map fst) else None)
        single |> Option.orElseWith byPosition
