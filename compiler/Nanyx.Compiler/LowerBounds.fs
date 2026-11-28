/// A type that holds `undefined` where a value of it gives one out, given where another is expected, is a lower bound on
/// the variables it meets there: `undefined` binds none of them while the constraints are solved, so that another type
/// given for one says what it is (see `Unifier.holdsBottom`). One that nothing else says the type of is `undefined`
/// once the def it is in is generalised, as a number literal nothing settles takes its default type then (see
/// `NumericLiterals.settle`), after the literals are, so a literal's own variable is not one: `list.head` of a
/// `list(undefined)` gives a `#some(undefined) | #emptyList`, which is `#emptyList` (#3129).
module internal NyxCompiler.LowerBounds

open NyxCompiler.InferState

/// The variables of `expected` that `given` has `undefined` at, where a value of `given` gives one out.
let rec private metByBottom (given: Ty) (expected: Ty) : TyVar list =
    match Unifier.unalias given, expected with
    | given, TyVar v when Unifier.isBottom given -> [ v ]
    | TyApply(gName, gArgs), TyApply(eName, eArgs) when gName = eName && gArgs.Length = eArgs.Length -> List.map2 metByBottom gArgs eArgs |> List.concat
    | TyTag(gName, Some gPayload), TyTag(eName, Some ePayload) when gName = eName -> metByBottom gPayload ePayload
    | TyRecord(gFields, _), TyRecord(eFields, _) ->
        gFields |> Map.toList |> List.collect (fun (name, field) -> eFields |> Map.tryFind name |> Option.map (metByBottom field) |> Option.defaultValue [])
    | TyFunc(_, gOutput, _), TyFunc(_, eOutput, _) -> metByBottom gOutput eOutput
    | TyUnion gItems, TyUnion eItems ->
        let tags items = items |> List.choose (function TyTag(name, payload) -> Some (name, payload) | _ -> None) |> Map.ofList
        let eTags = tags eItems
        tags gItems
        |> Map.toList
        |> List.collect (fun (name, payload) ->
            match payload, eTags |> Map.tryFind name with
            | Some g, Some (Some e) -> metByBottom g e
            | _ -> [])
    | _ -> []

/// `state` with each variable a lower bound met (see `BottomGiven`) that is still a variable of its own, and that
/// `isOwn` says the def being generalised has, made `undefined`. A variable a signature names stands for any type, and is
/// left alone. The others are kept for a def around this one.
let settle (isOwn: Ty -> bool) (state: InferState) : InferState =
    if state.BottomGiven.IsEmpty then state
    else
        let settled, kept =
            state.BottomGiven
            |> List.partition (fun (given, expected) ->
                metByBottom (resolveSoFar state given) (resolveSoFar state expected)
                |> List.forall (fun v -> isOwn (TyVar v)))
        let vars =
            settled
            |> List.collect (fun (given, expected) -> metByBottom (resolveSoFar state given) (resolveSoFar state expected))
            |> List.filter (fun v -> v.Name.IsNone)
            |> List.distinctBy _.Id
        vars |> List.fold (fun state v -> addConstraint (TyVar v) Unifier.bottomType state) { state with BottomGiven = kept }

/// `state` without the lower bounds that meet a variable of `declared`, the type a def writes, which stands for any type
/// and is never `undefined`: `xs \head ?? crash("none")` in a `[Add(a)] list(a) -> a` gives an `a`.
let dropWritten (declared: Ty) (state: InferState) : InferState =
    if state.BottomGiven.IsEmpty then state
    else
        let written = TypeEnv.freeTyVars (resolveSoFar state declared)
        let meetsWritten (given, expected) =
            metByBottom (resolveSoFar state given) (resolveSoFar state expected) |> List.exists (fun v -> written.Contains v.Id)
        { state with BottomGiven = state.BottomGiven |> List.filter (meetsWritten >> not) }

/// `ty` as it will be once the def it is in is generalised, if nothing else says what the variables a lower bound met
/// are (see `settle`): each that is still a variable of its own made `undefined`, so a tag whose payload is one has no
/// values. A match on `list.head` of a `list(undefined)` is complete with an arm for `#emptyList` alone, though an arm
/// for `#some(x)`, which can't be reached, still checks (see the official linter's `unreachableArm`).
let asSettled (state: InferState) (ty: Ty) : Ty =
    if state.BottomGiven.IsEmpty then ty
    else
        let met =
            state.BottomGiven
            |> List.collect (fun (given, expected) -> metByBottom (resolveSoFar state given) (resolveSoFar state expected))
            |> List.filter (fun v -> v.Name.IsNone)
        match met with
        | [] -> ty
        | met -> Unifier.apply (met |> List.map (fun v -> v.Id, Unifier.bottomType) |> Map.ofList) ty
