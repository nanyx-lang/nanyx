namespace NyxCompiler

open Parser.Program

/// A type alias that refers to itself, inside the payload of one of its tags:
/// `type Expr = #num(int) | #add(Expr, Expr)`, `type Tree(a) = #leaf | #node(Tree(a), a, Tree(a))`.
///
/// An alias's type holds the type it stands for (see `TyAlias`), so a type that held itself would never end. A
/// reference to a recursive alias holds its definition closed instead: `TyAlias(name, args, TyRecursive(body, args))`.
/// The `body` is the alias's definition with each of its parameters a `TyParam` and each reference to the alias itself
/// a `TySelf`, so it has no type variables. A pass that walks a type (to apply a substitution, find its free variables,
/// check whether a variable occurs in it) finds the alias's variables in its arguments and ends, and two references to
/// one alias at the same arguments are the same type. `unfold` gives what a reference stands for, one level deep: the
/// references inside it are closed in the same way.
///
/// Types that name each other, `type Expr = (id: int, body: #block(list(Statement)))` and `type Statement = (expr:
/// Expr)`, are a group, closed together: each holds `TyRecursive(TyGroup(selected, members), args)`, where `members`
/// are the definitions of all of them, in each of which a reference to a member is a `TyMember` of its position, and
/// `selected` says which member this is. A member may be an alias or a nominal type (`memberReference`).
module RecursiveAliases =
    /// A reference to a recursive alias: its closed definition and its arguments.
    let (|RecursiveAlias|_|) (ty: Ty) =
        match ty with
        | TyAlias(_, _, TyRecursive(body, args)) -> Some (body, args)
        | _ -> None

    /// The reference to the recursive alias `name`, defined by the closed `body`, at `args`.
    let reference (name: string) (body: Ty) (args: Ty list) : Ty =
        TyAlias(name, args, TyRecursive(body, args))

    /// `ty` with each part that `replace` gives a replacement for replaced, and the other parts walked in turn. The
    /// closed definition of a recursive alias or nominal type inside it is left as it is: its parameters are its own.
    let rec private mapTy (replace: Ty -> Ty option) (ty: Ty) : Ty =
        let recur = mapTy replace
        match replace ty with
        | Some replaced -> replaced
        | None ->
            match ty with
            | TyRecursive(body, args) -> TyRecursive(body, List.map recur args)
            | _ -> Ty.map recur ty

    /// The reference to the member at `index` of the group `members` at `args`: a recursive alias, or a nominal type
    /// whose closed definition holds its arguments as they are (see `RecursiveNominals.underlying`).
    let memberReference (members: GroupMember list) (index: int) (args: Ty list) : Ty =
        let groupMember = members.[index]
        match groupMember.Nominal with
        | Some(id, isPrivate) -> TyNominal(id, args, TyRecursive(TyGroup(index, members), args), isPrivate)
        | None -> reference groupMember.Name (TyGroup(index, members)) args

    /// The definition of the member `selected` of the group `members` at `args`: its body with each parameter the
    /// argument at its position, through `argument`, and each reference to a member closed again.
    let unfoldMember (argument: Ty -> Ty) (selected: int) (members: GroupMember list) (args: Ty list) : Ty =
        let rec replace ty =
            match ty with
            | TyParam index when index < args.Length -> Some (argument args.[index])
            | TyMember(index, memberArgs) -> Some (memberReference members index (List.map (mapTy replace) memberArgs))
            | _ -> None
        mapTy replace members.[selected].Body

    /// What the reference to the recursive alias `name`, defined by the closed `body`, at `args` stands for: `body`
    /// with each parameter the argument at its position, and each reference to the alias, or to a member of its group,
    /// closed again.
    let unfold (name: string) (body: Ty) (args: Ty list) : Ty =
        let rec replace ty =
            match ty with
            | TyParam index when index < args.Length -> Some args.[index]
            | TySelf selfArgs -> Some (reference name body (List.map (mapTy replace) selfArgs))
            | _ -> None
        match body with
        | TyGroup(selected, members) -> unfoldMember id selected members args
        | _ -> mapTy replace body

    /// The type an alias stands for: its underlying type, or for a recursive alias that unfolded one level. `ty` itself
    /// when it is not an alias.
    let underlying (ty: Ty) : Ty =
        match ty with
        | TyAlias(name, _, TyRecursive(body, args)) -> unfold name body args
        | TyAlias(_, _, underlying) -> underlying
        | _ -> ty

    /// What a reference to the alias being defined stands for while its definition is read: a marker, closed over
    /// once the definition is read (see `close`).
    let selfMarker (parameters: TyVar list) : Ty =
        TySelf(parameters |> List.map TyVar)

    /// Whether `ty`, a reference read with `selfMarker` as what the alias stands for, is one to the alias itself.
    let private (|SelfReference|_|) (ty: Ty) =
        match ty with
        | TyAlias(_, args, TySelf _) -> Some args
        | _ -> None

    /// Whether `ty`, a definition read with `selfMarker`, refers to the alias it defines; and whether it does anywhere
    /// but inside the payload of a tag, where a value of it would hold itself with no way to end.
    let selfReferences (ty: Ty) : bool * bool =
        let mutable any = false
        let mutable unguarded = false
        let rec walk (insideTag: bool) ty =
            match ty with
            | SelfReference args ->
                any <- true
                if not insideTag then unguarded <- true
                args |> List.iter (walk insideTag)
            | RecursiveAlias(_, args) -> args |> List.iter (walk insideTag)
            | TyTag(_, payload) -> payload |> Option.iter (walk true)
            | _ -> Ty.children ty |> List.iter (walk insideTag)
        walk false ty
        any, unguarded

    /// The underlying type of the alias with `parameters` whose definition, `ty`, was read with `selfMarker` and refers
    /// to itself: the closed definition at the alias's own parameters, so that a reference written with arguments,
    /// `Tree(int)`, is this with the parameters replaced.
    let close (parameters: TyVar list) (ty: Ty) : Ty =
        let positions = parameters |> List.mapi (fun index parameter -> parameter.Id, index) |> Map.ofList
        let rec replace ty =
            match ty with
            | TyVar v when positions.ContainsKey v.Id -> Some (TyParam positions.[v.Id])
            | SelfReference args -> Some (TySelf(List.map (mapTy replace) args))
            | _ -> None
        TyRecursive(mapTy replace ty, parameters |> List.map TyVar)

    /// What a reference to the member at `index` of a group of types that name each other stands for while the group's
    /// definitions are read: a marker, closed over once they are read (see `closeMember`). An alias's holds its
    /// `parameters`, as `selfMarker` does; a nominal type's none, as `RecursiveNominals.selfMarker`.
    let memberMarker (index: int) (parameters: TyVar list) : Ty = TyMember(index, parameters |> List.map TyVar)

    /// The member and arguments of `ty` if it is a reference, read with `memberMarker`, to a member of the group being
    /// defined.
    let private (|MemberReference|_|) (ty: Ty) =
        match ty with
        | TyAlias(_, args, TyMember(index, _))
        | TyNominal(_, args, TyMember(index, _), _) -> Some (index, args)
        | _ -> None

    /// The closed definition of a member of a group of types that name each other, whose definition `ty` was read with
    /// `memberMarker` for each member, at the member's own `variables`: each of them a `TyParam`, and each reference to
    /// a member a `TyMember` (see `TyGroup`).
    let closeMember (variables: TyVar list) (ty: Ty) : Ty =
        let positions = variables |> List.mapi (fun index variable -> variable.Id, index) |> Map.ofList
        let rec replace ty =
            match ty with
            | TyVar v when positions.ContainsKey v.Id -> Some (TyParam positions.[v.Id])
            | MemberReference(index, args) -> Some (TyMember(index, List.map (mapTy replace) args))
            | _ -> None
        mapTy replace ty

    /// Each place the definition `body` of a type names one of the types `names`, with the name, and whether that is
    /// inside the payload of a tag.
    let namesOf (names: Set<string>) (body: TypeExpr) : (string * TypeExpr * bool) list =
        let rec find (insideTag: bool) (typeExpr: TypeExpr) =
            let here =
                match typeExpr.Kind with
                | TypeName written
                | TypeApply(written, _) when names.Contains written -> [ written, typeExpr, insideTag ]
                | _ -> []
            let insideTag = insideTag || (match typeExpr.Kind with TypeTag _ -> true | _ -> false)
            here @ (TypeExpr.children typeExpr |> List.collect (find insideTag))
        find false body

    /// Each place the definition `body` of the type `name` names the type, with whether that is inside the payload of
    /// a tag.
    let namesOfItself (name: string) (body: TypeExpr) : (TypeExpr * bool) list =
        namesOf (Set.singleton name) body |> List.map (fun (_, typeExpr, insideTag) -> typeExpr, insideTag)

    /// The places, each in the definition of an alias of `aliases` (by name, with its definition) and naming another,
    /// where the aliases name each other in a cycle with no tag on it: a value of them would hold itself with no way to
    /// end, as `type A = (int, B)` and `type B = (A, string)` would. Each is the name of the alias written in, the
    /// alias it names, where, and the aliases of its cycle in the order written.
    let unguardedCycles (aliases: (string * TypeExpr) list) : (string * string * TypeExpr * string list) list =
        let names = aliases |> List.map fst |> Set.ofList
        let edges =
            aliases
            |> List.collect (fun (name, body) ->
                namesOf names body |> List.filter (fun (named, _, insideTag) -> not insideTag && named <> name) |> List.map (fun (named, typeExpr, _) -> name, named, typeExpr))
        let next name = edges |> List.filter (fun (from, _, _) -> from = name) |> List.map (fun (_, target, _) -> target) |> List.distinct
        let reachableFrom name =
            let rec walk (seen: Set<string>) frontier =
                match frontier with
                | [] -> seen
                | current :: rest ->
                    let fresh = next current |> List.filter (seen.Contains >> not)
                    walk (Set.union seen (Set.ofList fresh)) (fresh @ rest)
            walk Set.empty [ name ]
        let reaches = aliases |> List.map (fun (name, _) -> name, reachableFrom name) |> Map.ofList
        edges
        |> List.filter (fun (from, target, _) -> reaches.[target].Contains from)
        |> List.map (fun (from, target, typeExpr) ->
            let cycle = aliases |> List.map fst |> List.filter (fun name -> reaches.[name].Contains from && reaches.[from].Contains name)
            from, target, typeExpr, cycle)

    /// The error for each place the definition `body` of the alias `name` names it other than inside the payload of one
    /// of its tags, where a value of it would hold itself with no way to end: `type Pair = (int, Pair)`.
    let unguardedReferences (name: string) (body: TypeExpr) : Diagnostic list =
        namesOfItself name body
        |> List.filter (snd >> not)
        |> List.map (fun (typeExpr, _) ->
            Diagnostics.at DiagnosticCodes.UnguardedRecursiveAlias typeExpr.Span
                $"It refers to itself outside a tag: a type alias can name itself only in the payload of one of its tags, as in `#node({name})`"
            |> Diagnostics.about name)

    /// The error for each place a cycle of aliases that name each other with no tag on it goes through (see
    /// `unguardedCycles`), with the name of the alias it is written in.
    let unguardedCycleErrors (aliases: (string * TypeExpr) list) : (string * Diagnostic) list =
        unguardedCycles aliases
        |> List.map (fun (name, target, typeExpr, cycle) ->
            let quoted = cycle |> List.map (fun name -> $"'{name}'") |> String.concat ", "
            name,
            Diagnostics.at DiagnosticCodes.UnguardedRecursiveAlias typeExpr.Span
                $"The aliases {quoted} name each other with no tag in between: type aliases can name each other only in the payload of a tag, as in `#node({target})`"
            |> Diagnostics.about name)
