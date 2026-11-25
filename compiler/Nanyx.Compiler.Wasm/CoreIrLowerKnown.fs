/// Specialising a function that requires contexts on the members known where it is called (see "Specialising on
/// known members" in `docs/src/content/deep-dives/context-calls.md`).
///
/// A member is known when a call finds it as a closure over the wrapper of a top-level function (a member of the
/// ambient instance, or of an instance built from attached definitions), as a top-level definition's value, or in a
/// local that a `def` gave a top-level function, a lambda or a constant, as a `use` does with `use C(bump = addThree,
/// limit = 3)` (see `FunctionLowering.NoteDefinedKnown`). A call
/// does not pass its callee the members it knows: it calls a specialisation of the callee on them instead, whose key
/// names them. In the specialisation, a known member is a local that a call to it never reads, since the call goes
/// straight to the function (see `directArguments`), and that a call passing it on passes as known in turn. Only where
/// the member is used as a value, such as by a lambda that captures it, is the local given the member's value (see
/// `usedOf`).
module internal Transpiler.Wasm.CoreIrLowerKnown

open NyxCompiler
open Parser.Program
open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.CoreIrLowerTypes
open Transpiler.Wasm.CoreIrLowerInput

/// A context member known where a function is called.
type KnownMember =
    /// A closure over `wrapper`, the wrapper of the top-level function `wrapped`, at the type `at`: a call to it is a
    /// call to `wrapped` at that type.
    | KnownFunction of wrapper: string * wrapped: string * at: Ty
    /// `closure`, the static closure over `lifted`, a lifted lambda that captures nothing: a call to it is a call to
    /// `lifted` with the closure as its environment.
    | KnownLambda of lifted: string * closure: Atom
    /// A closure over `lifted`, a lifted lambda that captures values, which differs from call to call: unlike the
    /// others, the member is still passed, as the lambda's environment, but a call through it is a call to `lifted`.
    | KnownLambdaOver of lifted: string
    /// The value of the top-level definition of this name, which is of this type.
    | KnownValue of definition: string * Ty
    /// A constant, an int or a bool, of this type: what a `use` binds a member to when it is given a literal.
    | KnownConstant of Atom * IrType

module KnownMember =
    /// Whether a function specialised on `knownMember` is still passed it: a lambda's environment is.
    let isPassed (knownMember: KnownMember) =
        match knownMember with
        | KnownLambdaOver _ -> true
        | _ -> false

/// What a function is lowered for: a definition, at type arguments (none when it is not generic), and specialised on the
/// members known for some of the contexts it requires, by their keys. The others it takes as parameters. Types that
/// share an IR type, `list(int)` and `set(int)`, have different descriptions, so the type arguments a `Type` value can
/// stand for are in `Described` too, as types (see `SpecKey.describedVariables`), and so are those of the function a
/// lambda is lifted from.
type SpecKey =
    { Definition: string
      TypeArguments: IrType list
      Known: (ContextMemberKey * KnownMember) list
      Described: Ty list }

module SpecKey =
    /// The function lowered for `definition` at `typeArguments`, with no known members.
    let plain (definition: string) (typeArguments: IrType list) =
        { Definition = definition; TypeArguments = typeArguments; Known = []; Described = [] }

    /// The type variables of a definition's type `scheme` that a `Type` value it is given or makes can stand for: those in
    /// a `Type(…)` it names, and in a `Typed(…)` context it requires, whose member is one.
    let describedVariables (scheme: Ty) : Set<int> =
        let rec walk (found: Set<int>) (ty: Ty) =
            match ty with
            | TyApply(name, args) when TypeValues.isTypeName name || name = TypeValues.typedContext ->
                args |> List.collect freeVars |> List.fold (fun found v -> Set.add v found) found
            | _ -> Ty.fold walk found ty
        walk Set.empty scheme

/// `ty` without the rests of its unions that nothing closes, which add no tags (see `toIrType`): `ToString(#red | r)`
/// is passed at `#red`.
let rec private withoutOpenRests (ty: Ty) =
    match ty with
    | TyUnion items -> TyUnion(items |> List.filter (function TyVar _ -> false | _ -> true) |> List.map withoutOpenRests)
    | TyFunc(input, output, _) -> TyFunc(withoutOpenRests input, withoutOpenRests output, [])
    | TyContexts _ -> TyContexts []
    | TyOpenRecord _ -> ty
    // A recursive type's closed definition is left as it is: the contexts it names are its own parameters (see
    // `RecursiveNominals`).
    | TyRecursive(body, args) -> TyRecursive(body, List.map withoutOpenRests args)
    | _ -> Ty.map withoutOpenRests ty

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

/// The member that is the value of the top-level definition `definition` (`Point.`0``), as a context member of type
/// `memberTy`: the value at its own type, or, for a generic value (`list.default: list(a)`), at `memberTy`.
let knownValue (input: LowerInput) (definition: string) (memberTy: Ty option) : KnownMember =
    match input.DefinitionTypes |> Map.tryFind definition, memberTy |> Option.map (withoutOpenRests >> unconstrainedAsUnit) with
    | Some ty, _ when (freeVars ty).IsEmpty -> KnownValue(definition, ty)
    | Some _, Some ty -> KnownValue(definition, ty)
    | _ -> cannot $"passing the generic value '{definition}' as a context member where the type it is needed at is not known"

/// What a call finds known among the members of `parameters`, each found as `arguments` says, at the types in
/// `memberTys`. A member in scope is known when `inScope` says what it is.
let knownAmong (input: LowerInput) (parameters: ContextParameter list) (arguments: ContextArgument list) (memberTys: Ty option list) (inScope: ContextMemberKey -> KnownMember option) =
    List.zip3 parameters arguments memberTys
    |> List.choose (fun (parameter, argument, memberTy) ->
        match argument with
        | ClosureOverWrapper wrapper ->
            let wrapped, at = wrappedAt input wrapper memberTy
            Some (parameter.Key, KnownFunction(wrapper, wrapped, at))
        | DefinitionValue definition -> Some (parameter.Key, knownValue input definition memberTy)
        | MemberInScope key -> inScope key |> Option.map (fun known -> parameter.Key, known)
        | DerivedClosure _
        | PassedByCaller
        | Unfound _ -> None)

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
        bindTemp (Expr.make (MakeRecord fields) input) (fun tuple -> k [ tuple ])
    | _ -> k atoms

/// Which of the variables of `ids` `body` names anywhere.
let usedOf (ids: Set<int>) (body: Expr) : Set<int> =
    let used = System.Collections.Generic.HashSet<int>()
    let var (var: Var) = if ids.Contains var.Id then used.Add var.Id |> ignore
    let atom (atom: Atom) =
        match atom with
        | AVar v -> var v
        | _ -> ()
    // The variables an expression names itself; the expressions inside it are walked from `Expr.children`.
    let own (e: Expr) =
        match e.Kind with
        | Atom a
        | MakeTag(_, a)
        | Field(a, _) -> atom a
        | Dbg(_, text) -> atom text
        | Prim(_, atoms)
        | Call(_, atoms)
        | MakeList atoms
        | CallRuntime(_, atoms, _)
        | CallImport(_, atoms)
        | MakeClosure(_, _, atoms) -> List.iter atom atoms
        | CopyArrayInto(target, at, source) -> List.iter atom [ target; at; source ]
        | MakeRecord fields -> fields |> List.iter (snd >> atom)
        | UpdateRecord(record, updates) -> atom record; updates |> List.iter (snd >> atom)
        | Assign(target, value)
        | SetField(target, _, value) -> var target; atom value
        | CallClosure(closure, args, _) -> var closure; List.iter atom args
        | Interpolate parts -> parts |> List.iter (function Hole(hole, _) -> var hole | Text _ -> ())
        | ForEach loop -> ForLoop.sourceAtoms loop |> List.iter atom
        | If(cond, _, _) -> atom cond
        | Match(scrutinees, _) -> List.iter var scrutinees
        | Dup(vars, _)
        | Drop(vars, _)
        | DropAfter(_, vars) -> List.iter var vars
        | Release(released, _) -> var released.Object
        | Reuse(token, _)
        | FreeToken(token, _) -> var token
        // A probe uses none of its locals.
        | Probe _
        | Let _
        | Do _
        | While _
        | FloatConstant _
        | Crash _ -> ()
    if not ids.IsEmpty then
        // In no particular order, from a stack, so that a chain as long as a function takes no stack to walk.
        let pending = System.Collections.Generic.Stack<Expr>([ body ])
        while pending.Count > 0 do
            let e = pending.Pop()
            own e
            for child in Expr.children e do
                pending.Push child
    Set.ofSeq used

/// The context parameter of the function the lifted lambda `lambda` is lifted from that its capture `name` is, if `name`
/// is one: a captured member, which the lambda is specialised on by the parameter's key when the function knows it
/// (#2311).
let ownerParameter (input: LowerInput) (lambda: string) (name: string) : ContextParameter option =
    match input.Lifted |> Map.tryFind lambda with
    | Some (LiftedLambda(_, _, owner, _)) ->
        input.ContextParameters
        |> Map.tryFind owner
        |> Option.bind (fun (parameters, _) -> parameters |> List.tryFind (fun parameter -> parameter.Name = name))
    | _ -> None

/// Of the captures `captured` of the lifted lambda `lambda`, the members the function that makes it knows (`knownOf`
/// says what each name it has in scope is known as), by the keys the lambda finds them by (see `ownerParameter`): the
/// lambda is specialised on them, and calls them directly (#2311). The members a lambda can't take that way, a
/// lambda's environment, are not among them.
let knownCaptures (input: LowerInput) (lambda: string) (knownOf: string -> KnownMember option) (captured: Expression list) =
    let knownCapture (capture: Expression) =
        match capture.Kind with
        | IdentifierExpr name ->
            match knownOf name, ownerParameter input lambda name with
            | Some knownMember, Some parameter when not (KnownMember.isPassed knownMember) -> Some(parameter.Key, knownMember)
            | _ -> None
        | _ -> None
    let known = captured |> List.choose knownCapture |> List.distinctBy fst
    // Such members after the lambda's other captures need not be captured, since the lambda doesn't read them from its
    // environment, and the others keep their places in it.
    let kept = captured |> List.rev |> List.skipWhile (knownCapture >> Option.isSome) |> List.rev
    known, kept
