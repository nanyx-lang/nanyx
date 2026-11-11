/// The type checker's types as the lowering to the core IR reads them (see `CoreIrLower`), and how the lowering says
/// what it cannot lower yet.
module internal Transpiler.Wasm.CoreIrLowerTypes

open NyxCompiler
open Transpiler.Wasm.CoreIr

/// Something the IR cannot lower yet, and why.
exception CannotLower of string

let cannot message = raise (CannotLower message)

let rec freeVars (ty: Ty) : int list =
    match ty with
    | TyVar v -> [ v.Id ]
    | TyPrimitive _ -> []
    | TyNominal(_, underlying, _) -> freeVars underlying
    | TyFunc(input, output) -> freeVars input @ freeVars output
    | TyRecord fields -> fields |> Map.toList |> List.collect (snd >> freeVars)
    | TyOpenRecord(fields, rest) -> (fields |> Map.toList |> List.collect (snd >> freeVars)) @ [ rest.Id ]
    | TyTag(_, payload) -> payload |> Option.map freeVars |> Option.defaultValue []
    | TyApply(_, args) -> args |> List.collect freeVars
    | TyUnion items -> items |> List.collect freeVars
    | TyAlias(_, args, underlying) -> (args |> List.collect freeVars) @ freeVars underlying

let rec substitute (subst: Map<int, Ty>) (ty: Ty) : Ty =
    let go = substitute subst
    match ty with
    | TyVar v -> subst |> Map.tryFind v.Id |> Option.defaultValue ty
    | TyPrimitive _ -> ty
    | TyNominal(name, underlying, isPrivate) -> TyNominal(name, go underlying, isPrivate)
    | TyFunc(input, output) -> TyFunc(go input, go output)
    | TyRecord fields -> TyRecord(fields |> Map.map (fun _ -> go))
    | TyOpenRecord(fields, rest) ->
        let fields = fields |> Map.map (fun _ -> go)
        // A record whose other fields are now known is closed.
        match subst |> Map.tryFind rest.Id |> Option.map Unifier.unalias with
        | Some (TyRecord others) -> TyRecord(Map.fold (fun acc name fieldTy -> acc |> Map.add name fieldTy) others fields)
        | Some (TyOpenRecord(others, rest')) -> TyOpenRecord(Map.fold (fun acc name fieldTy -> acc |> Map.add name fieldTy) others fields, rest')
        | _ -> TyOpenRecord(fields, rest)
    | TyTag(name, payload) -> TyTag(name, payload |> Option.map go)
    | TyApply(name, args) -> TyApply(name, args |> List.map go)
    | TyUnion items -> TyUnion(items |> List.map go)
    | TyAlias(name, args, underlying) -> TyAlias(name, args |> List.map go, go underlying)

/// Binds the type variables of `scheme` so that it is `instance`, adding to `bindings`.
let rec bindScheme (bindings: Map<int, Ty>) (scheme: Ty) (instance: Ty) : Map<int, Ty> =
    // A tag alone is a union of one: a union's rest left with one tag is bound as `TyUnion [ tag ]`, and a lambda that
    // takes its input from its pattern has the tag itself (#457).
    let oneTagAsTag ty =
        match ty with
        | TyUnion [ TyTag _ as tag ] -> tag
        | _ -> ty
    match Unifier.unalias scheme, Unifier.unalias instance with
    | TyVar v, _ ->
        match bindings |> Map.tryFind v.Id with
        | Some bound when oneTagAsTag bound <> oneTagAsTag instance -> cannot "a type variable stands for two types in one call"
        | _ -> bindings |> Map.add v.Id instance
    | TyPrimitive a, TyPrimitive b when a = b -> bindings
    | TyFunc(input, output), TyFunc(input', output') -> bindScheme (bindScheme bindings input input') output output'
    | TyRecord fields, TyRecord fields' when Set.ofSeq fields.Keys = Set.ofSeq fields'.Keys ->
        fields |> Map.fold (fun acc key fieldTy -> bindScheme acc fieldTy fields'.[key]) bindings
    // A function that reads fields of any record that has them, used at a record: its other fields are the rest.
    | TyOpenRecord(fields, rest), TyRecord fields' when fields.Keys |> Seq.forall fields'.ContainsKey ->
        let bound = fields |> Map.fold (fun acc key fieldTy -> bindScheme acc fieldTy fields'.[key]) bindings
        let others = fields' |> Map.filter (fun key _ -> not (fields.ContainsKey key))
        bindScheme bound (TyVar rest) (TyRecord others)
    | TyTag(name, payload), TyTag(name', payload') when name = name' ->
        match payload, payload' with
        | Some p, Some p' -> bindScheme bindings p p'
        | None, None -> bindings
        | _ -> cannot "a tag's payload differs between a definition and its use"
    // A tag alone is a union of one.
    | TyUnion _, (TyTag _ as tag) -> bindScheme bindings scheme (TyUnion [ tag ])
    | (TyTag _ as tag), TyUnion [ _ ] -> bindScheme bindings (TyUnion [ tag ]) instance
    | TyUnion items, TyUnion items' ->
        // The tags of a union may come in another order: pair them by name. An open union's rest (a type variable among
        // its tags) is the tags of the instance that the union does not name.
        let tagName = function TyTag(name, _) -> Some name | _ -> None
        let named = items |> List.choose tagName |> Set.ofList
        let bound =
            items
            |> List.fold (fun acc item ->
                match item with
                | TyTag(name, _) ->
                    match items' |> List.tryFind (fun item' -> tagName item' = Some name) with
                    | Some item' -> bindScheme acc item item'
                    | None -> cannot $"the tag #{name} is not in the type it is used at"
                | _ -> acc) bindings
        let rest = items' |> List.filter (fun item' -> match tagName item' with Some name -> not (named.Contains name) | None -> true)
        match items |> List.filter (fun item -> (tagName item).IsNone) with
        | [] when rest.IsEmpty -> bound
        | [] -> cannot "a union used at a type with more tags than it has"
        | [ restVar ] -> bindScheme bound restVar (TyUnion rest)
        | _ -> cannot "a union with more than one rest"
    | TyApply(name, args), TyApply(name', args') when name = name' && args.Length = args'.Length ->
        List.fold2 bindScheme bindings args args'
    | TyNominal(name, underlying, _), TyNominal(name', underlying', _) when name = name' ->
        bindScheme bindings underlying underlying'
    | _ -> cannot "a generic definition's type does not fit how it is used"

/// The order a record's fields are stored in: by position for a tuple, by name for anything else.
let storageOrder (names: string list) =
    let positions = names |> List.map (fun name -> match System.Int32.TryParse name with | true, index -> Some index | _ -> None)
    if positions |> List.forall Option.isSome then List.zip positions names |> List.sortBy fst |> List.map snd
    else names |> List.sortWith (fun a b -> System.String.CompareOrdinal(a, b))

/// `fields` (name and whatever goes with it) in the order they are stored.
let sortFields (fields: (string * 'a) list) =
    let byName = Map.ofList fields
    storageOrder (List.map fst fields) |> List.map (fun name -> name, byName.[name])

/// The IR type of `ty`, once `subst` is applied. A type variable nothing constrains (the element of `[]`, the rest of
/// an open union that no use closes) has no value of its own at run time, since nothing can make or inspect one, so it
/// is unit, and the rest of a union adds no tags.
let rec toIrType (ty: Ty) : IrType =
    match Unifier.unalias ty with
    | TyPrimitive "int" -> IrInt
    | TyPrimitive "bool" -> IrBool
    | TyPrimitive "unit" -> IrUnit
    | TyPrimitive "string" -> IrString
    | TyPrimitive "float" -> IrFloat
    | TyPrimitive "?error" -> IrError
    | TyTag _
    | TyUnion _ as tags ->
        let rec members ty =
            match Unifier.unalias ty with
            | TyTag(name, payload) -> [ name, payload |> Option.map toIrType ]
            | TyUnion items -> items |> List.collect members
            | TyVar _ -> []
            | _ -> cannot "a union of something other than tags"
        IrTags(members tags |> List.distinct |> List.sortBy fst)
    // A `Map`'s keys are in the order a record's fields are stored in.
    | TyRecord fields -> IrRecord(fields |> Map.toList |> sortFields |> List.map (fun (name, fieldTy) -> name, toIrType fieldTy))
    | TyNominal(_, underlying, _) -> toIrType underlying
    | TyApply("list", [ element ]) -> IrList(toIrType element)
    // A map is the list of its entries, each a tuple of its key and its value (see `Maps`).
    | Maps.MapType(key, value) -> toIrType (TyApply("list", [ TyRecord(Map.ofList [ "0", key; "1", value ]) ]))
    // A set is the list of its elements (see `Sets`).
    | Sets.SetType element -> IrList(toIrType element)
    | TyVar _ -> IrUnit
    | TyPrimitive other -> cannot $"a value of type {other}"
    | TyFunc(input, output) -> IrFunction(toIrType input, toIrType output)
    | TyOpenRecord _ -> cannot "a record whose other fields are not known here"
    | TyApply(name, _) -> cannot $"a value of type {name}"
    | TyAlias _ -> cannot "an alias"
