namespace NyxCompiler

/// The variance of each parameter of a nominal type, from what its definition makes of it (#3206): `Covariant` where it
/// is only given out, in a field, an element or a function's result; `Contravariant` where it is only taken in, as a
/// function's input; `Invariant` where it is both, or where it is an argument of a type that holds its argument to
/// change it, `marray` and `box`, or of another type's invariant parameter; and `Unused` where the definition never
/// names it. A type that names itself, or a group of types that name each other, starts each parameter `Unused` and
/// reads its definition again until nothing changes. The variances are worked out where a type's module reads its
/// definition and kept on its `TypeDefInfo`, so a module that imports the type, even a private one whose body it
/// can't see, reads them from there, and in each `TyNominal` of it, where the type checker reads them (see
/// `Unifier.varianceAt`).
module Variances =
    /// The variance of a place of variance `inner` inside a place of variance `outer`.
    let private compose (outer: Variance) (inner: Variance) =
        match outer, inner with
        | Unused, _
        | _, Unused -> Unused
        | Invariant, _
        | _, Invariant -> Invariant
        | Covariant, variance -> variance
        | Contravariant, Covariant -> Contravariant
        | Contravariant, Contravariant -> Covariant

    /// The variance a parameter named in places of both variances has.
    let private join (a: Variance) (b: Variance) =
        match a, b with
        | Unused, variance
        | variance, Unused -> variance
        | a, b when a = b -> a
        | _ -> Invariant

    /// The built-in types that hold their argument to change it, or that stand for it, and so are read at one argument
    /// only.
    let private invariantBuiltIns = set [ MutableArrays.typeName; Boxes.typeName; TypeValues.typeName ]

    /// Each place in `ty`, in a place of variance `at`, that `parameterOf` says is a parameter, with the parameter's
    /// position and the place's variance. A nominal type's arguments are read by its parameters' variances, which
    /// `lookup` gives, and a reference to the type being defined, or to a member of its group, by those `ownOf` gives for
    /// it (`-1` for the type itself). A recursive alias's closed definition inside is read by its own variances.
    let rec private occurrences
        (lookup: NominalId -> NominalFacts -> Variance list)
        (parameterOf: Ty -> int option)
        (ownOf: int -> Variance list)
        (at: Variance)
        (ty: Ty)
        : (int * Variance) list =
        let recur = occurrences lookup parameterOf ownOf
        let applied (variances: Variance list) (args: Ty list) =
            args
            |> List.indexed
            |> List.collect (fun (index, arg) ->
                match if index < variances.Length then variances.[index] else Invariant with
                | Unused -> []
                | variance -> recur (compose at variance) arg)
        match parameterOf ty with
        | Some index -> [ index, at ]
        | None ->
            match ty with
            | TySelf args -> applied (ownOf -1) args
            | TyMember(index, args) -> applied (ownOf index) args
            | TyNominal(id, args, _, facts) -> applied (lookup id facts) args
            | TyApply(name, args) when invariantBuiltIns.Contains name -> args |> List.collect (recur Invariant)
            | TyFunc(input, output, contexts) ->
                let flipped =
                    match at with
                    | Covariant -> Contravariant
                    | Contravariant -> Covariant
                    | other -> other
                recur flipped input @ recur at output @ (contexts |> List.collect (recur Invariant))
            | TyContexts contexts -> contexts |> List.collect (recur Invariant)
            | TyAlias(_, _, underlying) -> recur at underlying
            | TyRecursive(TyGroup(selected, members), args) -> applied (ofGroup lookup members).[selected] args
            | TyRecursive(body, args) -> applied (ofClosed lookup body args.Length) args
            | _ -> Ty.children ty |> List.collect (recur at)

    /// The variances, `count` of them, that `occurring` finds for parameters named nowhere, given the variances they
    /// have so far, read again until they stop changing.
    and private settled (counts: int list) (occurring: Variance list list -> (int * int * Variance) list) : Variance list list =
        let rec loop (current: Variance list list) =
            let found = occurring current
            let next =
                counts
                |> List.mapi (fun owner count ->
                    List.init count (fun index ->
                        found
                        |> List.filter (fun (o, i, _) -> o = owner && i = index)
                        |> List.fold (fun variance (_, _, at) -> join variance at) Unused))
            if next = current then current else loop next
        loop (counts |> List.map (fun count -> List.replicate count Unused))

    /// The variances of the `count` parameters of the closed definition `body`, in which each is a `TyParam` and the type
    /// names itself as a `TySelf`.
    and private ofClosed (lookup: NominalId -> NominalFacts -> Variance list) (body: Ty) (count: int) : Variance list =
        let parameterOf ty =
            match ty with
            | TyParam index when index < count -> Some index
            | _ -> None
        settled [ count ] (fun current ->
            occurrences lookup parameterOf (fun _ -> current.Head) Covariant body |> List.map (fun (index, at) -> 0, index, at))
        |> List.head

    /// The variances of the parameters of each member of a group of types that name each other, in whose definitions a
    /// member's parameters are `TyParam`s and each name of a member is a `TyMember`.
    and ofGroup (lookup: NominalId -> NominalFacts -> Variance list) (members: GroupMember list) : Variance list list =
        let counts = members |> List.mapi (fun index _ -> parameterCount members index)
        settled counts (fun current ->
            members
            |> List.indexed
            |> List.collect (fun (owner, groupMember) ->
                let parameterOf ty =
                    match ty with
                    | TyParam index when index < counts.[owner] -> Some index
                    | _ -> None
                occurrences lookup parameterOf (fun index -> current.[max 0 index]) Covariant groupMember.Body
                |> List.map (fun (index, at) -> owner, index, at)))

    /// How many parameters the member at `index` of a group has: as many as its definition names, or as a member names
    /// it with, whichever is more. A closed definition inside is not looked into: its parameters are its own.
    and private parameterCount (members: GroupMember list) (index: int) : int =
        let rec highest (ty: Ty) =
            match ty with
            | TyParam position -> position + 1
            | TyRecursive(_, args) -> args |> List.fold (fun most arg -> max most (highest arg)) 0
            | _ -> Ty.children ty |> List.fold (fun most child -> max most (highest child)) 0
        let rec namedWith (ty: Ty) =
            match ty with
            | TyMember(member', args) when member' = index -> max args.Length (args |> List.fold (fun most arg -> max most (namedWith arg)) 0)
            | TyRecursive(_, args) -> args |> List.fold (fun most arg -> max most (namedWith arg)) 0
            | _ -> Ty.children ty |> List.fold (fun most child -> max most (namedWith child)) 0
        let named = members |> List.fold (fun most groupMember -> max most (namedWith groupMember.Body)) 0
        max (highest members.[index].Body) named

    /// The variances of the parameters of the nominal type `info` defines, reading the nominal types it is made from by
    /// `lookup`. A member of a group is read with its group.
    let ofDefinition (lookup: NominalId -> NominalFacts -> Variance list) (info: TypeDefInfo) : Variance list =
        let count = info.Parameters.Length
        let fitted (variances: Variance list) = List.init count (fun index -> if index < variances.Length then variances.[index] else Unused)
        match info.Underlying with
        | TyRecursive(TyGroup(selected, members), _) -> fitted (ofGroup lookup members).[selected]
        | TyRecursive(body, _) -> fitted (ofClosed lookup body count)
        | underlying ->
            let positions = info.Parameters |> List.mapi (fun index variable -> variable.Id, index) |> Map.ofList
            let parameterOf ty =
                match ty with
                | TyVar variable -> positions |> Map.tryFind variable.Id
                | _ -> None
            let found = occurrences lookup parameterOf (fun _ -> []) Covariant underlying
            List.init count (fun index -> found |> List.filter (fst >> (=) index) |> List.fold (fun variance (_, at) -> join variance at) Unused)

    /// `ty` with the facts of each nominal member of a group it is closed over holding the variances `ofMember` gives
    /// for that member's identity, so that a member it unfolds to reads them.
    let withGroupVariances (ofMember: NominalId -> Variance list option) (ty: Ty) : Ty =
        match ty with
        | TyRecursive(TyGroup(selected, members), args) ->
            let members =
                members
                |> List.map (fun groupMember ->
                    match groupMember.Nominal with
                    | Some (id, facts) ->
                        match ofMember id with
                        | Some variances -> { groupMember with Nominal = Some (id, { facts with Variances = variances }) }
                        | None -> groupMember
                    | None -> groupMember)
            TyRecursive(TyGroup(selected, members), args)
        | _ -> ty
