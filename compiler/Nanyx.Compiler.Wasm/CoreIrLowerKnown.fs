/// Specialising a function that requires contexts on the members known where it is called (see "Specialising on
/// known members" in `docs/src/content/work-in-progress/context-calls.md`).
///
/// A member is known when a call finds it as a closure over the wrapper of a top-level function (a member of the
/// ambient instance, or of an instance built from attached definitions) or as a top-level definition's value. A call
/// does not pass its callee the members it knows: it calls a specialisation of the callee on them instead, whose key
/// names them. In the specialisation, a known member is a local that a call to it never reads, since the call goes
/// straight to the function (see `directArguments`), and that a call passing it on passes as known in turn. Only where
/// the member is used as a value, such as by a lambda that captures it, is the local given the member's value (see
/// `usedOf`).
module internal Transpiler.Wasm.CoreIrLowerKnown

open NyxCompiler
open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.CoreIrLowerTypes
open Transpiler.Wasm.CoreIrLowerInput

/// A context member known where a function is called.
type KnownMember =
    /// A closure over `wrapper`, the wrapper of the top-level function `wrapped`, at the type `at`: a call to it is a
    /// call to `wrapped` at that type.
    | KnownFunction of wrapper: string * wrapped: string * at: Ty
    /// The value of the top-level definition of this name, which is of this type.
    | KnownValue of definition: string * Ty

/// What a function is lowered for: a definition, at type arguments (none when it is not generic), and specialised on the
/// members known for some of the contexts it requires, by their keys. The others it takes as parameters.
type SpecKey =
    { Definition: string
      TypeArguments: IrType list
      Known: (ContextMemberKey * KnownMember) list }

module SpecKey =
    /// The function lowered for `definition` at `typeArguments`, with no known members.
    let plain (definition: string) (typeArguments: IrType list) =
        { Definition = definition; TypeArguments = typeArguments; Known = [] }

/// `ty` without the rests of its unions that nothing closes, which add no tags (see `toIrType`): `ToString(#red | r)`
/// is passed at `#red`.
let rec private withoutOpenRests (ty: Ty) =
    match ty with
    | TyUnion items -> TyUnion(items |> List.filter (function TyVar _ -> false | _ -> true) |> List.map withoutOpenRests)
    | TyFunc(input, output, _) -> TyFunc(withoutOpenRests input, withoutOpenRests output, [])
    | TyRecord fields -> TyRecord(fields |> Map.map (fun _ -> withoutOpenRests))
    | TyTag(name, payload) -> TyTag(name, payload |> Option.map withoutOpenRests)
    | TyApply(name, args) -> TyApply(name, args |> List.map withoutOpenRests)
    | TyNominal(nominal, underlying, isPrivate) -> TyNominal(nominal, withoutOpenRests underlying, isPrivate)
    | TyAlias(name, args, underlying) -> TyAlias(name, args |> List.map withoutOpenRests, withoutOpenRests underlying)
    | TyVar _
    | TyPrimitive _
    | TyOpenRecord _ -> ty

/// The top-level function that `wrapper` wraps, and the type a closure over the wrapper is at as a context member of
/// type `memberTy`: the function's own type, or, for a generic function, `memberTy`.
let wrappedAt (input: LowerInput) (wrapper: string) (memberTy: Ty option) : string * Ty =
    let wrapped = wrappedOf input wrapper

    // A member that requires contexts of its own would need them found where the instance is.
    if input.ContextParameters.ContainsKey wrapped then cannot $"a context member '{wrapped}', which requires contexts"
    match input.DefinitionTypes |> Map.tryFind wrapped, memberTy |> Option.map (withoutOpenRests >> unconstrainedAsUnit) with
    | Some ty, _ when (freeVars ty).IsEmpty -> wrapped, ty
    | Some _, Some ty -> wrapped, ty
    | _ -> cannot $"passing the generic function '{wrapped}' as a context member"

/// The type arguments of the wrapper a closure over the known function `wrapped` at `at` calls: none when `wrapped` is
/// not generic, and `at` when it is.
let wrapperTypeArguments (input: LowerInput) (toIrType: Ty -> IrType) (wrapped: string) (at: Ty) =
    match input.DefinitionTypes |> Map.tryFind wrapped with
    | Some ty when (freeVars ty).IsEmpty -> []
    | _ -> [ toIrType at ]

/// The member that is the value of the top-level definition `definition` (`Point.`0``).
let knownValue (input: LowerInput) (definition: string) : KnownMember =
    match input.DefinitionTypes |> Map.tryFind definition with
    | Some ty when (freeVars ty).IsEmpty -> KnownValue(definition, ty)
    | _ -> cannot $"passing the generic value '{definition}' as a context member"

/// What a call finds known among the members of `parameters`, each found as `arguments` says, at the types in
/// `memberTys`. A member in scope is known when `inScope` says what it is.
let knownAmong (input: LowerInput) (parameters: ContextParameter list) (arguments: ContextArgument list) (memberTys: Ty option list) (inScope: ContextMemberKey -> KnownMember option) =
    List.zip3 parameters arguments memberTys
    |> List.choose (fun (parameter, argument, memberTy) ->
        match argument with
        | ClosureOverWrapper wrapper ->
            let wrapped, at = wrappedAt input wrapper memberTy
            Some (parameter.Key, KnownFunction(wrapper, wrapped, at))
        | DefinitionValue definition -> Some (parameter.Key, knownValue input definition)
        | MemberInScope key -> inScope key |> Option.map (fun known -> parameter.Key, known)
        | DerivedClosure _
        | PassedByCaller -> None)

/// The arguments of a direct call to a function that takes `count` parameters, given those a call through a closure
/// of input type `input` over its wrapper passes (see `CallValue`), as the wrapper passes them on (see
/// `ValueParameters`): the unit for a lone parameter of type unit, and one tuple of the elements for a lone parameter
/// whose type is a tuple.
let directArguments (bindTemp: Expr -> (Atom -> Expr) -> Expr) (count: int) (input: IrType) (atoms: Atom list) (k: Atom list -> Expr) : Expr =
    match count, input, IrType.tupleElements input with
    | 1, IrUnit, _ -> k [ AUnit ]
    | 1, _, Some elements when elements.Length = atoms.Length ->
        let fields =
            List.zip elements atoms
            |> List.mapi (fun index ((slot, _), atom) -> slot, (string index, atom))
            |> List.sortBy fst
            |> List.map snd
        bindTemp { Kind = MakeRecord fields; Type = input } (fun tuple -> k [ tuple ])
    | _ -> k atoms

/// Which of the variables of `ids` `body` names anywhere.
let usedOf (ids: Set<int>) (body: Expr) : Set<int> =
    let used = System.Collections.Generic.HashSet<int>()
    let var (var: Var) = if ids.Contains var.Id then used.Add var.Id |> ignore
    let atom (atom: Atom) =
        match atom with
        | AVar v -> var v
        | _ -> ()
    let rec visit (e: Expr) =
        let links, last = Chain.split e
        for link in links do
            match link.Kind with
            | Let(_, value, _)
            | Do(value, _) -> visit value
            | Dup(vars, _)
            | Drop(vars, _) -> List.iter var vars
            | Release(released, _) -> var released.Object
            | _ -> ()
        match last.Kind with
        | Atom a
        | MakeTag(_, a)
        | Field(a, _)
        | Dbg a -> atom a
        | Prim(_, atoms)
        | Call(_, atoms)
        | MakeList atoms
        | CallRuntime(_, atoms, _)
        | MakeClosure(_, _, atoms) -> List.iter atom atoms
        | MakeRecord fields -> fields |> List.iter (snd >> atom)
        | UpdateRecord(record, updates) -> atom record; updates |> List.iter (snd >> atom)
        | Assign(target, value)
        | SetField(target, _, value) -> var target; atom value
        | CallClosure(closure, args) -> var closure; List.iter atom args
        | Interpolate parts -> parts |> List.iter (function Hole(hole, _) -> var hole | Text _ -> ())
        | ForEach loop -> ForLoop.sourceAtoms loop |> List.iter atom; visit loop.Body
        | While loop -> visit loop.Condition; visit loop.Body
        | If(cond, ifTrue, ifFalse) -> atom cond; visit ifTrue; visit ifFalse
        | Match(scrutinees, arms) ->
            List.iter var scrutinees
            for arm in arms do
                for _, guard in arm.Patterns |> List.collect Pattern.guards do
                    visit guard
                visit arm.Body
        | DropAfter(inner, vars) -> List.iter var vars; visit inner
        | Reuse(token, inner) -> var token; visit inner
        | BoxFloat _
        | Panic _
        | Let _
        | Do _
        | Dup _
        | Drop _
        | Release _ -> ()
    if not ids.IsEmpty then visit body
    Set.ofSeq used
