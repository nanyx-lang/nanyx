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
    | TyFunc(input, output, _) -> freeVars input @ freeVars output
    | TyContexts _ -> []
    // What a nominal type is made from has no variables its arguments don't have, and what one that names itself is
    // made from has the contexts it carries as a value (see `RecursiveNominals`).
    | TyNominal(_, args, _, _) -> args |> List.collect freeVars
    | _ -> Ty.children ty |> List.collect freeVars

/// The type a context member's own type variables are at in its context's layout (see `boxOwnVariables`), and a field's
/// or an element's type that is only known when the program runs (see `substitute`): their values are boxed (`IrBox`).
let boxedTy = TyPrimitive "?box"

/// The member of a record type's description, as the backend lays it out, that makes a value of the record from its
/// fields' values (see `CoreIrLowerTypeMatch`). No program can name it.
let makeMember = "__make"

/// Whether `ty` holds a type whose values are boxed (see `boxedTy`).
let rec holdsBoxed (ty: Ty) = ty = boxedTy || Ty.children ty |> List.exists holdsBoxed

/// The call of the prelude's `toList` on a value of `collection`, a map or a set, whose entries or elements are of
/// `elementTy`: the name `callee` gives the function at its type, and the type of the list it gives.
let storeListCall (callee: string -> Ty -> string) (collection: Ty) (elementTy: Ty) : string * Ty =
    let listTy = Lists.ofType elementTy
    callee (match collection with Sets.SetType _ -> Sets.toList | _ -> Maps.toList) (TyFunc(collection, listTy, [])), listTy

let rec substitute (subst: Map<int, Ty>) (ty: Ty) : Ty =
    let go = substitute subst
    match ty with
    | TyVar v -> subst |> Map.tryFind v.Id |> Option.defaultValue ty
    // A field's or an element's type in a type's description is the one a `Type` match found where the type is known
    // (see `CoreIrLowerTypeMatch`), and otherwise only known when the program runs: its values are boxed.
    | TypeValues.PackagedType id -> subst |> Map.tryFind id |> Option.defaultValue boxedTy
    | TyFunc(input, output, contexts) -> TyFunc(go input, go output, contexts |> List.map go |> FunctionContexts.flatten)
    | TyContexts contexts -> TyContexts(contexts |> List.map go |> FunctionContexts.flatten)
    | TyRecord(fields, order) -> TyRecord(fields |> Map.map (fun _ -> go), order)
    | TyOpenRecord(fields, rest) ->
        let fields = fields |> Map.map (fun _ -> go)
        // A record whose other fields are now known is closed.
        match subst |> Map.tryFind rest.Id |> Option.map Unifier.unalias with
        | Some (TyRecord(others, order)) -> TyRecord(Map.fold (fun acc name fieldTy -> acc |> Map.add name fieldTy) others fields, order)
        | Some (TyOpenRecord(others, rest')) -> TyOpenRecord(Map.fold (fun acc name fieldTy -> acc |> Map.add name fieldTy) others fields, rest')
        | _ -> TyOpenRecord(fields, rest)
    | _ -> Ty.map go ty

/// The contexts a function type requires that are contexts rather than context variables: a variable still free
/// stands for none. A scoped context's scope means nothing at run time, so it is left out.
let concreteContexts (contexts: Ty list) : Ty list =
    FunctionContexts.flatten contexts
    |> List.filter (FunctionContexts.isVariable >> not)
    |> List.map FunctionContexts.withoutScope
    |> List.distinct

/// The definition and arguments of the recursive alias or nominal type `ty` refers to, through any aliases of it (see
/// `RecursiveAliases` and `RecursiveNominals`).
let rec private recursiveReference (ty: Ty) : (Ty * Ty list) option =
    match ty with
    | RecursiveAliases.RecursiveAlias(body, args)
    // A nominal type's own arguments, which hold the contexts it carries (see `FunctionContexts.carried`).
    | TyNominal(_, args, TyRecursive(body, _), _) -> Some (body, args)
    | TyAlias(_, _, underlying) -> recursiveReference underlying
    | _ -> None

/// What says which group of recursive types the closed definition `body` is of: the group's members, for a type of a
/// group of types that name each other (see `TyGroup`), or the definition itself, for a type that names only itself.
let private groupKey (body: Ty) : Ty =
    match body with
    | TyGroup(_, members) -> TyGroup(0, members)
    | _ -> body

/// Which of its group's types the closed definition `body` is: its position for a member of a group, and 0 for a type
/// that names only itself.
let private selectedIn (body: Ty) : int =
    match body with
    | TyGroup(selected, _) -> selected
    | _ -> 0

/// The references `ty` holds to a type of the group `key` (see `groupKey`), but for those inside such a reference: in the
/// arguments of a reference to another recursive type, and anywhere else in the types it is made of.
let rec private referencesIn (key: Ty) (ty: Ty) : Ty list =
    match recursiveReference ty with
    | Some(body, _) when groupKey body = key -> [ ty ]
    | Some(_, args) -> args |> List.collect (referencesIn key)
    | None -> Ty.children ty |> List.collect (referencesIn key)

/// The types of the group of `ty`, a reference to a recursive type, at the arguments each is named with from `ty` and
/// from one another, each by its definition and arguments (see `recursiveReference`), with what it stands for unfolded
/// one level. They are in the order of their positions in the group and then of their arguments, so that a group is the
/// same IR type whichever of its types it is reached from (see `IrRecursive`). A type that names only itself, at the
/// arguments it has, is a group of one.
let private recursiveGroup (ty: Ty) : ((Ty * Ty list) * Ty) list =
    let key = groupKey (fst (recursiveReference ty).Value)
    let rec grow (found: ((Ty * Ty list) * Ty) list) (pending: Ty list) =
        match pending with
        | [] -> found
        | next :: rest ->
            let reference = (recursiveReference next).Value
            if found |> List.exists (fst >> (=) reference) then
                grow found rest
            elif found.Length >= 64 then
                cannot "a recursive type that holds itself at other type arguments"
            else
                let unfolded = RecursiveNominals.underlying (Unifier.unalias next)
                grow (found @ [ reference, unfolded ]) (rest @ referencesIn key unfolded)
    grow [] [ ty ] |> List.sortBy (fun ((body, args), _) -> selectedIn body, args |> List.map Unifier.tyToString |> String.concat ", ")

/// What converting types to IR types has found for a module, by its contexts' layouts (see `toIrTypeIn`): each type's IR
/// type, each recursive type's group (see `recursiveGroup`), and the IR types of each group's types where no other
/// group is around them, by the group and the contexts being expanded. A module's lowering asks for the IR type of every
/// expression it lowers, and one of a type of a large group of recursive types, as a syntax tree's, unfolds the whole
/// group: `nanyx.lint`'s lint build spent 37 of its 38 seconds there before they were kept (#3261). They are a module's
/// own, so a process that compiles many, as the language server, keeps none beyond the module.
type private Converted =
    { IrTypes: System.Collections.Concurrent.ConcurrentDictionary<Ty, IrType>
      Groups: System.Collections.Concurrent.ConcurrentDictionary<Ty, ((Ty * Ty list) * Ty) list>
      GroupTypes: System.Collections.Concurrent.ConcurrentDictionary<(Ty * Ty list) list * Set<string>, IrType list> }

let private converted = System.Runtime.CompilerServices.ConditionalWeakTable<ContextLayouts, Converted>()

let private convertedIn (layouts: ContextLayouts) : Converted =
    converted.GetValue(
        layouts,
        fun _ ->
            { IrTypes = System.Collections.Concurrent.ConcurrentDictionary(HashIdentity.Structural)
              Groups = System.Collections.Concurrent.ConcurrentDictionary(HashIdentity.Structural)
              GroupTypes = System.Collections.Concurrent.ConcurrentDictionary(HashIdentity.Structural) })

/// Whether what `toIrTypeIn` finds on this thread is kept (see `Converted`): for a test, which compares the code made
/// either way. A module is lowered on one thread, and a test's setting is its own.
let internal keepingConverted = new System.Threading.ThreadLocal<bool>(fun () -> true)

/// How many types this thread has converted to IR types itself, rather than found kept (see `Converted`): for a test,
/// which counts the work of lowering a module, as other tests lower theirs on other threads.
let private conversions = new System.Threading.ThreadLocal<int64>(fun () -> 0L)

/// How many types this thread has converted itself (see `conversions`).
let internal conversionCount () = conversions.Value

/// `ty` without the contexts its function types require, to compare types as values alone.
let rec private withoutContexts (ty: Ty) : Ty =
    match ty with
    | TyFunc(input, output, _) -> TyFunc(withoutContexts input, withoutContexts output, [])
    | TyRecord(fields, order) -> TyRecord(fields |> Map.map (fun _ -> withoutContexts), order)
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
        bindScheme (bindScheme (bindContexts bindings contexts contexts') input input') output output'
    | TyContexts contexts, TyContexts contexts' -> bindContexts bindings contexts contexts'
    | TyRecord(fields, _), TyRecord(fields', _) when Set.ofSeq fields.Keys = Set.ofSeq fields'.Keys ->
        fields |> Map.fold (fun acc key fieldTy -> bindScheme acc fieldTy fields'.[key]) bindings
    // A function that reads fields of any record that has them, used at a record: its other fields are the rest.
    | TyOpenRecord(fields, rest), TyRecord(fields', order) when fields.Keys |> Seq.forall fields'.ContainsKey ->
        let bound = fields |> Map.fold (fun acc key fieldTy -> bindScheme acc fieldTy fields'.[key]) bindings
        let others = fields' |> Map.filter (fun key _ -> not (fields.ContainsKey key))
        bindScheme bound (TyVar rest) (TyRecord(others, FieldOrder.within others order))
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
                    // A tag whose payload is `undefined` has no values, so a union leaves it out (see `Unifier.apply`):
                    // `list.head` used at a `#emptyList`, the head of a `list(undefined)`, has its `a` `undefined`.
                    | None ->
                        match item with
                        | TyTag(_, Some payload) when not (TypeEnv.freeTyVars payload).IsEmpty -> bindScheme acc payload Unifier.bottomType
                        | _ -> cannot $"the tag `#{name}` is not in the type it is used at"
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
    | TyScope scope, TyScope scope' -> bindScheme bindings scope scope'
    | TyNominal(nominal, _, underlying, _), TyNominal(nominal', _, underlying', _) when nominal = nominal' ->
        bindScheme bindings underlying underlying'
    | TyMeasure(dimension, storage), TyMeasure(dimension', storage') when dimension = dimension' -> bindScheme bindings storage storage'
    | _ -> cannot "a generic definition's type does not fit how it is used"

/// `bindings` with the context variables among `contexts`, those of a function type or a type generic in contexts in a
/// scheme, bound to the contexts among `contexts'`, the instance's, that `contexts` does not name, as a union.
and private bindContexts (bindings: Map<int, Ty>) (contexts: Ty list) (contexts': Ty list) : Map<int, Ty> =
    let named = concreteContexts contexts
    // `Yield(int)` is the instance of a named `Yield(b)` at the type `b` stands for, not another context.
    let instanceOfNamed (context: Ty) =
        named
        |> List.exists (fun namedContext ->
            match namedContext, context with
            | TyApply(name, args), TyApply(name', args') -> name = name' && args.Length = args'.Length && not (freeVars namedContext).IsEmpty
            | _ -> false)
    let others = concreteContexts contexts' |> List.filter (fun context -> not (List.contains context named || instanceOfNamed context))
    contexts
    |> List.choose (function TyVar v -> Some v | _ -> None)
    |> List.mapi (fun index v -> v, (if index = 0 then others else []))
    |> List.fold (fun acc (v, required) -> if acc |> Map.containsKey v.Id then acc else acc |> Map.add v.Id (TyUnion required)) bindings

/// The order a record's fields are stored in: by position for a tuple, by name for anything else.
let storageOrder (names: string list) =
    let positions = names |> List.map (fun name -> match System.Int32.TryParse name with | true, index -> Some index | _ -> None)
    if positions |> List.forall Option.isSome then List.zip positions names |> List.sortBy fst |> List.map snd
    else names |> List.sortWith (fun a b -> System.String.CompareOrdinal(a, b))

/// `fields` (name and whatever goes with it) in the order they are stored.
let sortFields (fields: (string * 'a) list) =
    let byName = Map.ofList fields
    storageOrder (List.map fst fields) |> List.map (fun name -> name, byName.[name])

let (|BoxedType|_|) (ty: Ty) = if ty = boxedTy then Some () else None

/// The type variables of `ty` that are the type of a value: not the rest of a union or record, which changes nothing in
/// how a value is laid out, nor a context variable.
let valueVariables (ty: Ty) : int list =
    let others = Set.union (Unifier.restVarIds true ty) (FunctionContexts.variablesIn ty |> Map.keys |> Set.ofSeq)
    freeVars ty |> List.filter (others.Contains >> not) |> List.distinct

/// `memberTy`, the type a context without parameters declares for a member, with its own type variables at `boxedTy`:
/// the member's value is made once, where its instance is, and each call has them at the types it has, so their values
/// are held in a box, the same whatever they are (see `CoreIrLowerBoxes`).
let boxOwnVariables (memberTy: Ty) : Ty =
    substitute (valueVariables memberTy |> List.map (fun id -> id, boxedTy) |> Map.ofList) memberTy

/// The IR type of `ty`, once `subst` is applied. A type variable nothing constrains (the element of `[]`, the rest of
/// an open union that no use closes) has no value of its own at run time, since nothing can make or inspect one, so it
/// is unit, and the rest of a union adds no tags. A function type takes the members of the contexts it requires, which
/// `layouts` gives (see `IrFunction`): those of a parameterised one at the types it requires it at, each keyed by the
/// instance (`show` of `Show(Point)`, see `Identifiers.typeKeyOfType`). One required at a type with a variable in it
/// is found where the function is made, so it takes none for that.
let rec toIrTypeIn (layouts: ContextLayouts) (ty: Ty) : IrType =
    if not keepingConverted.Value then toIrTypeExpanding layouts Set.empty [] ty
    else
        let kept = (convertedIn layouts).IrTypes
        match kept.TryGetValue ty with
        | true, found -> found
        | false, _ ->
            let found = toIrTypeExpanding layouts Set.empty [] ty
            kept.[ty] <- found
            found

/// `toIrTypeIn`, inside the members of the contexts `expanding`: a member's type that requires one of them again takes
/// none of its members, so that a context whose member requires the context itself has an IR type. `recursing` is the
/// groups of recursive types `ty` is inside, innermost first, each its types by their definitions and arguments: one of
/// them again is `IrSelf` of where its group is among them and where it is in its group.
and private toIrTypeExpanding (layouts: ContextLayouts) (expanding: Set<string>) (recursing: (Ty * Ty list) list list) (ty: Ty) : IrType =
    conversions.Value <- conversions.Value + 1L
    let toIrType = toIrTypeExpanding layouts expanding recursing
    match recursiveReference ty with
    // A recursive type it is inside, the innermost group's at depth 0.
    | Some reference when recursing |> List.exists (List.contains reference) ->
        let depth = recursing |> List.findIndex (List.contains reference)
        IrSelf(depth, recursing.[depth] |> List.findIndex ((=) reference))
    // A type that holds itself at other arguments, such as `#node(T(list(a)))` in `T(a)`, holds ever more types.
    | Some(body, _) when (recursing |> List.filter (List.exists (fst >> groupKey >> (=) (groupKey body))) |> List.length) > 8 ->
        cannot "a recursive type that holds itself at other type arguments"
    | Some reference ->
        let group =
            if keepingConverted.Value then (convertedIn layouts).Groups.GetOrAdd(ty, recursiveGroup) else recursiveGroup ty
        let frame = group |> List.map fst
        let groupTypes () = group |> List.map (snd >> toIrTypeExpanding layouts expanding (frame :: recursing))
        // Where no other group is around it, a group's types are the same IR types whichever of them is reached.
        let types =
            if keepingConverted.Value && recursing.IsEmpty then (convertedIn layouts).GroupTypes.GetOrAdd((frame, expanding), fun _ -> groupTypes ())
            else groupTypes ()
        IrRecursive(frame |> List.findIndex ((=) reference), types)
    | None ->
    match Unifier.unalias ty with
    | TyPrimitive "int" -> IrInt
    | SizedIntegerType integer -> IrSized integer
    | TyPrimitive "bool" -> IrBool
    | TyPrimitive "unit" -> IrUnit
    | TyPrimitive "string" -> IrString
    | TyPrimitive "float" -> IrFloat
    | TyPrimitive "F32" -> IrF32
    // A `char` is its code point, which fits in 32 bits and is never negative.
    | Chars.CharType -> IrSized SizedInteger.U32
    | TyPrimitive "?error" -> IrError
    | BoxedType -> IrBox
    // A scoped context's scope means nothing at run time: a value of the context's type is unit.
    | TyScope _ -> IrUnit
    // A `Type` value is the number of the type it stands for, which the module's descriptions are kept by (see
    // `CoreIrTypeIds`).
    | TypeValues.TypeValueType _ -> IrInt
    // A value of a description type is the record it is, with the packaged type's values boxed. A record's description
    // also holds what makes a value of the record from its fields' values, boxed, which its `build` calls (see
    // `CoreIrLowerTypeMatch`).
    | TyApply(name, args) when TypeValues.isDescriptionType name ->
        let members = TypeValues.descriptionMembers name (args |> List.map (fun _ -> boxedTy)) boxedTy
        let members = if name = TypeValues.recordInfo then members @ [ makeMember, TyFunc(Lists.ofType boxedTy, boxedTy, []) ] else members
        toIrType (TyRecord(Map.ofList members, FieldOrder.ofList members))
    // A cell passed to a function is the box it is held in (see `CellSyntax`).
    | FunctionContexts.CellType(held, _) -> CoreIrRegions.boxType (toIrType held)
    // A `box`, which the standard library's `MRef` is (see `Boxes`), is on its region's list (see `CoreIrRegions`).
    | Boxes.BoxType held -> CoreIrRegions.boxType (toIrType held)
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
    | TyRecord(fields, _) -> IrRecord(fields |> Map.toList |> sortFields |> List.map (fun (name, fieldTy) -> name, toIrType fieldTy))
    | TyNominal _ as nominal -> toIrType (RecursiveNominals.underlying nominal)
    // A measured value is its storage, in its dimension's default storage unit (see `Units`).
    | TyMeasure(_, storage) -> toIrType storage
    | TyApply("list", [ element ]) -> IrList(toIrType element)
    // A map and a set are laid out as their stores (see `Maps.storeOf` and `Sets.storeOf`).
    | Maps.MapType(key, value) -> toIrType (Maps.storeOf key value)
    | Sets.SetType element -> toIrType (Sets.storeOf element)
    | Arrays.ArrayType element -> IrArray(toIrType element)
    // A mutable array is laid out as an array is (see `MutableArrays`).
    | MutableArrays.MutableArrayType element -> CoreIrRegions.boxType (IrArray(toIrType element))
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
                layouts.Plain
                |> Map.tryFind name
                |> Option.defaultValue []
                |> List.map (fun (key, memberTy) -> key, toIrTypeExpanding layouts (expanding.Add name) recursing memberTy))
            |> List.distinctBy fst
        let instances =
            concreteContexts contexts
            |> List.choose (function
                | TyApply(name, (_ :: _ as args)) as context when
                    not (expanding.Contains name)
                    && layouts.Parameterised |> Map.tryFind name |> Option.exists (fun (parameters, _) -> parameters.Length = args.Length) ->
                    Identifiers.typeKeyOfType context |> Option.map (fun key -> key, name, args)
                | _ -> None)
            |> List.distinctBy (fun (key, _, _) -> key)
            |> List.sortBy (fun (key, _, _) -> ContextMemberKey.text (InstanceMember(key, "")))
            |> List.collect (fun (key, name, args) ->
                let parameters, declared = layouts.Parameterised.[name]
                let bound = List.zip (parameters |> List.map _.Id) args |> Map.ofList
                declared
                |> List.map (fun (memberName, memberTy) ->
                    InstanceMember(key, memberName), toIrTypeExpanding layouts (expanding.Add name) recursing (boxOwnVariables (substitute bound memberTy))))
        let waits = concreteContexts contexts |> List.exists (function TyApply(name, []) | TyAlias(name, [], _) -> layouts.Waiting.Contains name | _ -> false)
        IrFunction(toIrType input, toIrType output, members @ instances, waits)
    | TyOpenRecord _ -> cannot "a record whose other fields are not known here"
    | TyApply(name, _) -> cannot $"a value of type {name}"
    | TyAlias _ -> cannot "an alias"
    | TyContexts _ -> cannot "the contexts a type carries, as a value"
    | TyRecursive _
    | TySelf _
    | TyMember _
    | TyGroup _
    | TyParam _ -> cannot "a recursive alias's definition outside the alias"

/// The members of the contexts among `contexts`, each with its key and IR type, in the order a function value that
/// requires them takes them (see `IrFunction`).
let contextMembersOf (layouts: ContextLayouts) (contexts: Ty list) : (ContextMemberKey * IrType) list =
    match toIrTypeIn layouts (TyFunc(TyPrimitive "unit", TyPrimitive "unit", contexts)) with
    | IrFunction(_, _, members, _) -> members
    | _ -> []

/// The arguments an adapter from a function value of type `givenTy` to one of `expectedTy` takes and passes on (see
/// `CoreIrLowerMembers.coerce`): the elements of a tuple, none for unit, or else the one value. An expected input of unit
/// may be a type variable nothing constrained where the type was found (see `toIrType`), so the given input decides then,
/// unless it is a box, which the adapter makes of unit itself.
let valuesOf (input: IrType) =
    match input, IrType.tupleElements input with
    | IrUnit, _ -> []
    | _, Some elements -> List.map snd elements
    | _, None -> [ input ]

let adaptedValueTypes (givenTy: IrType) (expectedTy: IrType) : IrType list =
    match givenTy, expectedTy with
    | IrFunction(given, _, _, _), IrFunction(IrUnit, _, _, _) when given <> IrBox -> valuesOf given
    | _, IrFunction(expected, _, _, _) -> valuesOf expected
    | _ -> []

/// `ty` with each type variable it still has made unit: nothing constrains one where a value of `ty` is made, as in the
/// instance passed for the elements of `[]`, so no value of it is ever met (see `toIrType`). The rest of a union stays a
/// variable, which adds no tags, as `#some(list(int)) | r` from a call to `list.map` has it until something closes it:
/// unit is no tag.
let unconstrainedAsUnit (ty: Ty) =
    let unionRests = Set.difference (Unifier.restVarIds true ty) (Unifier.restVarIds false ty)
    let unconstrained = freeVars ty |> List.filter (unionRests.Contains >> not)
    substitute (unconstrained |> List.map (fun id -> id, TyPrimitive "unit") |> Map.ofList) ty

/// The type each context without parameters in `contextLayouts` declares for its member of each key, where only one
/// declares a member of that key.
let declaredMemberTypes (contextLayouts: ContextLayouts) =
    contextLayouts.Plain
    |> Map.values
    |> Seq.concat
    |> Seq.groupBy fst
    |> Seq.choose (fun (key, declared) ->
        match declared |> Seq.map snd |> Seq.distinct |> List.ofSeq with
        | [ memberTy ] -> Some (key, memberTy)
        | _ -> None)
    |> Map.ofSeq

/// The contexts a function of type `fnTy` requires where it is called.
let requiredContexts (fnTy: Ty) =
    match Unifier.unalias fnTy with
    | TyFunc(_, _, contexts) -> contexts
    | _ -> []

/// The types of the parameters `names` of a function of type `fnTy`, and of its result. Several parameters are the
/// fields of the input, each named by its position or by its own name when the type names it
/// (`(int, step: int) -> int`).
let parameterTypes (names: string list) (fnTy: Ty) (isLambda: bool) : Ty list * Ty =
    match names, Unifier.unalias fnTy with
    | [], TyFunc(_, result, _) when isLambda -> [], result
    | [], _ -> [], fnTy
    | [ _ ], TyFunc(input, result, _) -> [ input ], result
    | several, TyFunc(input, result, _) ->
        match Unifier.unalias input with
        | TyRecord(fields, _) when fields.Count = several.Length ->
            several
            |> List.mapi (fun index parameterName ->
                match fields |> Map.tryFind (string index), fields |> Map.tryFind parameterName with
                | Some ty, _
                | None, Some ty -> ty
                | None, None -> cannot $"a function whose parameter {parameterName} is not among its input's fields"), result
        // An open record of at least the parameters' fields, `(x: int, y: int, ...)`, or one its row is bound to a record
        // with more: each parameter takes the field of its name.
        | TyRecord(fields, _)
        | TyOpenRecord(fields, _) when several |> List.forall fields.ContainsKey -> several |> List.map (fun name -> fields.[name]), result
        | _ -> cannot "a function whose parameters are not a tuple"
    | _ -> cannot "a definition whose type is not a function"
/// The type a cell of type `cellTy` holds a value of type `given` as: the cell's own when both are unions of tags, since
/// any union holds a tag the same way (`mut found: #some(int) | #none = #none`), so that the cell is counted as a value of
/// its own type; otherwise `given`, which is the cell's.
let asCellType (cellTy: IrType) (given: IrType) =
    match IrType.unfold given, IrType.unfold cellTy with
    | IrTags _, IrTags _ -> cellTy
    | _ -> given
