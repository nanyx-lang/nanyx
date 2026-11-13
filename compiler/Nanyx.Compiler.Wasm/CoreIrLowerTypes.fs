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
    | TyFunc(input, output, _) -> freeVars input @ freeVars output
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
    | TyNominal(nominal, underlying, isPrivate) -> TyNominal(nominal, go underlying, isPrivate)
    | TyFunc(input, output, contexts) -> TyFunc(go input, go output, contexts |> List.map go |> FunctionContexts.flatten)
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

/// The contexts a function type requires that are contexts rather than context variables: a variable still free
/// stands for none. A scoped context's scope means nothing at run time, so it is left out.
let concreteContexts (contexts: Ty list) : Ty list =
    FunctionContexts.flatten contexts
    |> List.filter (FunctionContexts.isVariable >> not)
    |> List.map FunctionContexts.withoutScope
    |> List.distinct

/// The definition and arguments of the recursive alias `ty` refers to, through any aliases of it (see
/// `RecursiveAliases`).
let rec private recursiveReference (ty: Ty) : (Ty * Ty list) option =
    match ty with
    | RecursiveAliases.RecursiveAlias(body, args) -> Some (body, args)
    | TyAlias(_, _, underlying) -> recursiveReference underlying
    | _ -> None

/// `ty` without the contexts its function types require, to compare types as values alone.
let rec private withoutContexts (ty: Ty) : Ty =
    match ty with
    | TyFunc(input, output, _) -> TyFunc(withoutContexts input, withoutContexts output, [])
    | TyRecord fields -> TyRecord(fields |> Map.map (fun _ -> withoutContexts))
    | TyApply(name, args) -> TyApply(name, args |> List.map withoutContexts)
    | TyUnion items -> TyUnion(items |> List.map withoutContexts)
    | _ -> ty

/// The names of the tags `ty` holds, through unions and aliases.
let rec private tagNames (ty: Ty) : string list =
    match Unifier.unalias ty with
    | TyTag(name, _) -> [ name ]
    | TyUnion items -> items |> List.collect tagNames
    | _ -> []

/// Binds the type variables of `scheme` so that it is `instance`, adding to `bindings`. A context variable of a
/// function type in `scheme` is bound to the contexts the same function type in `instance` requires that `scheme`'s
/// does not name, as a union; the first variable takes them all.
///
/// An open union's rest is bound last, from the tags of the instance its union does not name, unless something else
/// binds it: the rest may hold tags the union names too, as `z` in `#some(a) | z` does when it is `#some(int)` (#1027).
let rec bindScheme (bindings: Map<int, Ty>) (scheme: Ty) (instance: Ty) : Map<int, Ty> =
    bindSchemeWith true (bindSchemeWith false bindings scheme instance) scheme instance

/// `bindScheme`, binding open unions' rests only when `rests` is true.
and private bindSchemeWith (rests: bool) (bindings: Map<int, Ty>) (scheme: Ty) (instance: Ty) : Map<int, Ty> =
    let bindScheme = bindSchemeWith rests
    // A tag alone is a union of one: a union's rest left with one tag is bound as `TyUnion [ tag ]`, and a lambda that
    // takes its input from its pattern has the tag itself (#457).
    let oneTagAsTag ty =
        match ty with
        | TyUnion [ TyTag _ as tag ] -> tag
        | _ -> withoutContexts ty
    // Unfolded, two references to one recursive alias would hold the same two references again: their arguments are
    // what differs. One with no type variables binds none.
    match recursiveReference scheme, recursiveReference instance with
    | Some (body, args), Some (body', args') when body = body' -> List.fold2 bindScheme bindings args args'
    | Some _, _ when (freeVars scheme).IsEmpty -> bindings
    | _ ->
    match Unifier.unalias scheme, Unifier.unalias instance with
    | TyVar v, _ ->
        match bindings |> Map.tryFind v.Id with
        | Some bound when oneTagAsTag bound <> oneTagAsTag instance -> cannot "a type variable that stands for two types in one call"
        | _ -> bindings |> Map.add v.Id instance
    | TyPrimitive a, TyPrimitive b when a = b -> bindings
    | TyFunc(input, output, contexts), TyFunc(input', output', contexts') ->
        let named = concreteContexts contexts
        let others = concreteContexts contexts' |> List.filter (fun context -> not (List.contains context named))
        let bound =
            contexts
            |> List.choose (function TyVar v -> Some v | _ -> None)
            |> List.mapi (fun index v -> v, (if index = 0 then others else []))
            |> List.fold (fun acc (v, required) -> if acc |> Map.containsKey v.Id then acc else acc |> Map.add v.Id (TyUnion required)) bindings
        bindScheme (bindScheme bound input input') output output'
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
        | [ _ ] when not rests -> bound
        | [ restVar ] ->
            let holdsRest =
                match Unifier.unalias restVar with
                | TyVar v when bound |> Map.containsKey v.Id ->
                    let held = tagNames bound.[v.Id] |> Set.ofList
                    rest |> List.forall (fun item -> tagName item |> Option.exists held.Contains)
                | _ -> false
            if holdsRest then bound else bindScheme bound restVar (TyUnion rest)
        | _ -> cannot "a union with more than one rest"
    | TyApply(name, args), TyApply(name', args') when name = name' && args.Length = args'.Length ->
        List.fold2 bindScheme bindings args args'
    | TyNominal(nominal, underlying, _), TyNominal(nominal', underlying', _) when nominal = nominal' ->
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

/// The members of each context without parameters, by the context's name: each member's key and declared type, in
/// the order the context's layout stores them.
type ContextLayouts = Map<string, (ContextMemberKey * Ty) list>

/// The IR type of `ty`, once `subst` is applied. A type variable nothing constrains (the element of `[]`, the rest of
/// an open union that no use closes) has no value of its own at run time, since nothing can make or inspect one, so it
/// is unit, and the rest of a union adds no tags. A function type takes the members of the contexts without parameters
/// it requires, which `layouts` gives (see `IrFunction`); a parameterised one is found where the function is made, so
/// it takes none for that.
let rec toIrTypeIn (layouts: ContextLayouts) (ty: Ty) : IrType = toIrTypeExpanding layouts Set.empty [] ty

/// `toIrTypeIn`, inside the members of the contexts `expanding`: a member's type that requires one of them again takes
/// none of its members, so that a context whose member requires the context itself has an IR type. `recursing` is the
/// recursive aliases `ty` is inside, innermost first, each by its definition and arguments: one of them again is
/// `IrSelf`.
and private toIrTypeExpanding (layouts: ContextLayouts) (expanding: Set<string>) (recursing: (Ty * Ty list) list) (ty: Ty) : IrType =
    let toIrType = toIrTypeExpanding layouts expanding recursing
    match recursiveReference ty with
    | Some reference when recursing |> List.tryHead = Some reference -> IrSelf
    // A type that holds itself at other arguments, such as `#node(T(list(a)))` in `T(a)`, holds ever more types.
    | Some reference when recursing |> List.contains reference || (recursing |> List.filter (fst >> (=) (fst reference)) |> List.length) > 8 ->
        cannot "a recursive type that holds itself at other type arguments"
    | Some reference -> IrRecursive(toIrTypeExpanding layouts expanding (reference :: recursing) (Unifier.unalias ty))
    | None ->
    match Unifier.unalias ty with
    | TyPrimitive "int" -> IrInt
    | SizedIntegerType integer -> IrSized integer
    | TyPrimitive "bool" -> IrBool
    | TyPrimitive "unit" -> IrUnit
    | TyPrimitive "string" -> IrString
    | TyPrimitive "float" -> IrFloat
    | TyPrimitive "F32" -> IrF32
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
    | TyFunc(input, output, contexts) ->
        let names =
            concreteContexts contexts
            |> List.choose (function
                | TyApply(name, [])
                | TyAlias(name, [], _) when not (expanding.Contains name) -> Some name
                | _ -> None)
            |> List.distinct
            |> List.sort
        let members =
            names
            |> List.collect (fun name ->
                layouts
                |> Map.tryFind name
                |> Option.defaultValue []
                |> List.map (fun (key, memberTy) -> key, toIrTypeExpanding layouts (expanding.Add name) recursing memberTy))
            |> List.distinctBy fst
        IrFunction(toIrType input, toIrType output, members)
    | TyOpenRecord _ -> cannot "a record whose other fields are not known here"
    | TyApply(name, _) -> cannot $"a value of type {name}"
    | TyAlias _ -> cannot "an alias"
