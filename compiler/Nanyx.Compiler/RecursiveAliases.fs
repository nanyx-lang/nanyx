namespace NyxCompiler

open Parser.Program

/// A type alias that refers to itself, inside the payload of one of its tags:
/// `type Expr = #num(int) | #add(Expr, Expr)`, `type Tree(a) = #leaf | #node(Tree(a), a, Tree(a))`.
///
/// An alias's type holds the type it stands for (see `TyAlias`), so a type that held itself would never end. A
/// reference to a recursive alias holds its definition closed instead: `TyAlias(name, args, TyApply("@recursive",
/// body :: args))`. The `body` is the alias's definition with each of its parameters a placeholder (`@0`, `@1`, ...)
/// and each reference to the alias itself `TyApply("@self", args)`, so it has no type variables. A pass that walks a
/// type (to apply a substitution, find its free variables, check whether a variable occurs in it) finds the alias's
/// variables in its arguments and ends, and two references to one alias at the same arguments are the same type.
/// `unfold` gives what a reference stands for, one level deep: the references inside it are closed in the same way.
module RecursiveAliases =
    let private definitionName = "@recursive"
    let private selfName = "@self"

    let private placeholder (index: int) = TyPrimitive $"@{index}"

    /// A reference to a recursive alias: its closed definition and its arguments.
    let (|RecursiveAlias|_|) (ty: Ty) =
        match ty with
        | TyAlias(_, _, TyApply(name, body :: args)) when name = definitionName -> Some (body, args)
        | _ -> None

    /// The reference to the recursive alias `name`, defined by the closed `body`, at `args`.
    let reference (name: string) (body: Ty) (args: Ty list) : Ty =
        TyAlias(name, args, TyApply(definitionName, body :: args))

    /// `ty` with each part that `replace` gives a replacement for replaced, and the other parts walked in turn. The
    /// closed definition of a recursive alias inside it is left as it is: its placeholders are its own.
    let rec private mapTy (replace: Ty -> Ty option) (ty: Ty) : Ty =
        let recur = mapTy replace
        match replace ty with
        | Some replaced -> replaced
        | None ->
            match ty with
            | TyAlias(name, _, TyApply(definition, body :: args)) when definition = definitionName ->
                reference name body (List.map recur args)
            | TyVar _
            | TyPrimitive _ -> ty
            | TyNominal(nominal, underlying, isPrivate) -> TyNominal(nominal, recur underlying, isPrivate)
            | TyAlias(name, args, underlying) -> TyAlias(name, List.map recur args, recur underlying)
            | TyFunc(arg, ret, contexts) -> TyFunc(recur arg, recur ret, List.map recur contexts)
            | TyRecord fields -> TyRecord(fields |> Map.map (fun _ fieldTy -> recur fieldTy))
            | TyOpenRecord(fields, row) -> TyOpenRecord(fields |> Map.map (fun _ fieldTy -> recur fieldTy), row)
            | TyTag(name, payload) -> TyTag(name, Option.map recur payload)
            | TyApply(name, args) -> TyApply(name, List.map recur args)
            | TyUnion items -> TyUnion(List.map recur items)

    /// What the reference to the recursive alias `name`, defined by the closed `body`, at `args` stands for: `body`
    /// with each placeholder the argument at its position, and each reference to the alias closed again.
    let unfold (name: string) (body: Ty) (args: Ty list) : Ty =
        let rec replace ty =
            match ty with
            | TyPrimitive text when text.StartsWith "@" ->
                match System.Int32.TryParse(text.Substring 1) with
                | true, index when index < args.Length -> Some args.[index]
                | _ -> None
            | TyApply(name', selfArgs) when name' = selfName -> Some (reference name body (List.map (mapTy replace) selfArgs))
            | _ -> None
        mapTy replace body

    /// The type an alias stands for: its underlying type, or for a recursive alias that unfolded one level. `ty` itself
    /// when it is not an alias.
    let underlying (ty: Ty) : Ty =
        match ty with
        | TyAlias(name, _, TyApply(definition, body :: args)) when definition = definitionName -> unfold name body args
        | TyAlias(_, _, underlying) -> underlying
        | _ -> ty

    /// What a reference to the alias being defined stands for while its definition is read: a marker, closed over
    /// once the definition is read (see `close`).
    let selfMarker (parameters: TyVar list) : Ty =
        TyApply(selfName, parameters |> List.map TyVar)

    /// Whether `ty`, a reference read with `selfMarker` as what the alias stands for, is one to the alias itself.
    let private (|SelfReference|_|) (ty: Ty) =
        match ty with
        | TyAlias(_, args, TyApply(name, _)) when name = selfName -> Some args
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
            | TyVar _
            | TyPrimitive _ -> ()
            | TyNominal(_, underlying, _) -> walk insideTag underlying
            | TyAlias(_, args, underlying) ->
                args |> List.iter (walk insideTag)
                walk insideTag underlying
            | TyFunc(arg, ret, contexts) -> arg :: ret :: contexts |> List.iter (walk insideTag)
            | TyRecord fields
            | TyOpenRecord(fields, _) -> fields |> Map.iter (fun _ fieldTy -> walk insideTag fieldTy)
            | TyTag(_, payload) -> payload |> Option.iter (walk true)
            | TyApply(_, args) -> args |> List.iter (walk insideTag)
            | TyUnion items -> items |> List.iter (walk insideTag)
        walk false ty
        any, unguarded

    /// The underlying type of the alias with `parameters` whose definition, `ty`, was read with `selfMarker` and refers
    /// to itself: the closed definition at the alias's own parameters, so that a reference written with arguments,
    /// `Tree(int)`, is this with the parameters replaced.
    let close (parameters: TyVar list) (ty: Ty) : Ty =
        let positions = parameters |> List.mapi (fun index parameter -> parameter.Id, index) |> Map.ofList
        let rec replace ty =
            match ty with
            | TyVar v when positions.ContainsKey v.Id -> Some (placeholder positions.[v.Id])
            | SelfReference args -> Some (TyApply(selfName, List.map (mapTy replace) args))
            | _ -> None
        TyApply(definitionName, mapTy replace ty :: (parameters |> List.map TyVar))

    /// Each place the definition `body` of the type `name` names the type, with whether that is inside the payload of
    /// a tag.
    let namesOfItself (name: string) (body: TypeExpr) : (TypeExpr * bool) list =
        let rec find (insideTag: bool) (typeExpr: TypeExpr) =
            let here =
                match typeExpr.Kind with
                | TypeName written
                | TypeApply(written, _) when written = name -> [ typeExpr, insideTag ]
                | _ -> []
            let insideTag = insideTag || (match typeExpr.Kind with TypeTag _ -> true | _ -> false)
            here @ (TypeExpr.children typeExpr |> List.collect (find insideTag))
        find false body

    /// The error for each place the definition `body` of the alias `name` names it other than inside the payload of one
    /// of its tags, where a value of it would hold itself with no way to end: `type Pair = (int, Pair)`.
    let unguardedReferences (name: string) (body: TypeExpr) : Diagnostic list =
        namesOfItself name body
        |> List.filter (snd >> not)
        |> List.map (fun (typeExpr, _) ->
            Diagnostics.at DiagnosticCodes.UnguardedRecursiveAlias typeExpr.Span
                $"The type '{name}' refers to itself outside a tag: a type alias can name itself only in the payload of one of its tags, as in `#node({name})`")
