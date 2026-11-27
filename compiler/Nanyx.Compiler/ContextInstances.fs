/// Instances of parameterised contexts: binding the ones in scope, and finding, deriving or building the instance
/// each call of a function that requires one is passed.
module NyxCompiler.ContextInstances

open Parser.Program
open NyxCompiler
open NyxCompiler.InferState
open NyxCompiler.TypeResolution
open NyxCompiler.Contexts

// Instances of parameterised contexts.
//
// A function that requires `[Sum(a)]` is passed an instance of `Sum` for whatever `a` is at each call. The call finds
// it, nearest first, among the instances in scope (each `use` of a `Sum(T)`, or of a value of `Sum`'s shape, around
// the call, and each context the type of the function around the call declares), or else builds it from the
// definitions attached to the type, as
// `Point.0` and `Point.+` make a `Sum(Point)`. Where it was found is kept by the call's node, for the backend.

/// Whether `name` is the name of a context.
let private isContextName (state: InferState) (name: Identifier) =
    state.ContextDefs.ContainsKey name || (state.TypeDefs |> Map.tryFind name |> Option.exists _.IsContext)

/// Whether `ctx` names a parameterised context: `Sum(a)`, `Show(int)`.
let internal isParameterizedContext (state: InferState) (ctx: TypeExpr) =
    match ctx.Kind with
    | TypeApply(name, _ :: _) -> isContextName state name
    | _ -> false

/// `env` with the instance of `ctxName` applied to `argTys` in scope, as `written`, put there by `provider`, the `use`
/// or the signature (see `InstanceBinding`). Of two instances of one context, the one put in scope later is nearer.
let private bindInstanceOf (env: TypeEnv) (provider: ContextProvider) (ctxName: Identifier) (argTys: Ty list) (written: TypeExpr) : TypeEnv =
    TypeEnv.withInstance { Context = TyApply(ctxName, argTys); Written = written; Provider = provider } env

/// `env` with the instance `ctx` in scope, if it is one of a parameterised context, as `providerOf ctx` puts it there
/// (see `bindInstanceOf`).
let private bindInstance (env: TypeEnv) (state: InferState) (providerOf: TypeExpr -> ContextProvider) (ctx: TypeExpr) : TypeEnv * InferState =
    match ctx.Kind with
    | TypeApply(ctxName, args) when isParameterizedContext state ctx ->
        let argTys, state = args |> List.mapFold typeExprToTy state
        bindInstanceOf env (providerOf ctx) ctxName argTys ctx, state
    | _ -> env, state

/// `env` with each instance of a parameterised context among `contexts`, the contexts a def's signature declares, in
/// scope (see `bindInstance`).
let internal bindInstances (env: TypeEnv) (state: InferState) (contexts: TypeExpr list) : TypeEnv * InferState =
    contexts |> List.fold (fun (env, state) ctx -> bindInstance env state (fun ctx -> FromSignature ctx.Span) ctx) (env, state)

/// `ty` with each type variable named in `names` replaced by the type it maps to.
let rec private replaceNamedVars (names: Map<string, Ty>) (ty: Ty) : Ty =
    let recur = replaceNamedVars names
    match ty with
    | TyVar { Name = Some name } when names.ContainsKey name -> names.[name]
    | TyFunc(arg, ret, contexts) -> TyFunc(recur arg, recur ret, contexts |> List.map recur |> FunctionContexts.flatten)
    | TyContexts contexts -> TyContexts(contexts |> List.map recur |> FunctionContexts.flatten)
    | _ -> Ty.map recur ty

/// The type each type variable of `general` that `keyOf` gives a key stands for in `specific`, a type of the same
/// shape: a function's type as it is declared, against the same type instantiated at a call.
let rec private varsIn (keyOf: TyVar -> 'key option) (general: Ty) (specific: Ty) : Map<'key, Ty> =
    let recur = varsIn keyOf
    let all (pairs: (Ty * Ty) list) =
        pairs |> List.fold (fun acc (g, s) -> recur g s |> Map.fold (fun acc key ty -> acc |> Map.add key ty) acc) Map.empty
    let fieldPairs (generalFields: Map<string, Ty>) (specificFields: Map<string, Ty>) =
        [ for KeyValue(name, g) in generalFields do
            match specificFields |> Map.tryFind name with
            | Some s -> yield g, s
            | None -> () ]
    match general, specific with
    | TyVar v, _ -> keyOf v |> Option.map (fun key -> Map.ofList [ key, specific ]) |> Option.defaultValue Map.empty
    | TyFunc(ga, gr, _), TyFunc(sa, sr, _) -> all [ ga, sa; gr, sr ]
    | TyApply(_, gs), TyApply(_, ss) when gs.Length = ss.Length -> all (List.zip gs ss)
    | TyScope g, TyScope s -> recur g s
    | TyAlias(_, gs, gu), TyAlias(_, ss, su) when gs.Length = ss.Length -> all ((gu, su) :: List.zip gs ss)
    | TyNominal(_, _, gu, _), TyNominal(_, _, su, _) -> recur gu su
    | TyMeasure(_, g), TyMeasure(_, s) -> recur g s
    | TyRecord(gf, _), TyRecord(sf, _) -> all (fieldPairs gf sf)
    | TyOpenRecord(gf, _), TyOpenRecord(sf, _) -> all (fieldPairs gf sf)
    | TyTag(_, Some g), TyTag(_, Some s) -> recur g s
    | TyUnion gs, TyUnion ss when gs.Length = ss.Length -> all (List.zip gs ss)
    | _ -> Map.empty

/// The type each named type variable of `general` stands for in `specific` (see `varsIn`).
let private namedVarsIn (general: Ty) (specific: Ty) : Map<string, Ty> = varsIn _.Name general specific

/// Whether the types `a` and `b`, resolved as far as they can be, are the same type.
let private sameType (a: Ty) (b: Ty) =
    a = b
    || ((TypeEnv.freeTyVars a).IsEmpty && (TypeEnv.freeTyVars b).IsEmpty
        && Solving.unify [ a, b, Equal, SourceSpan.none ] |> Result.isOk)

/// The members of the context `ctxName` with its parameters bound to `argTys`, each with its type. `argTys` may leave
/// out the context's associated types (see `AssociatedTypes`), which then stay its own parameters.
let private contextMemberTypes (state: InferState) (ctxName: Identifier) (argTys: Ty list) : (Identifier * Ty) list option =
    match state.TypeDefs |> Map.tryFind ctxName with
    | Some info when info.Parameters.Length = argTys.Length || info.Parameters.Length - info.AssociatedTypes.Length = argTys.Length ->
        let parameters = List.truncate argTys.Length info.Parameters
        match Unifier.unalias info.Underlying with
        | TyRecord(fields, _) -> Some (fields |> Map.toList |> List.map (fun (name, ty) -> name, Unifier.applyToParameters parameters argTys ty))
        | _ -> None
    | _ -> None

/// The instance nearest in scope in `env` that `choose` gives something for, given its context's name and arguments
/// resolved as far as the constraints so far tell, with what it gives.
let private nearestInstance (env: TypeEnv) (state: InferState) (choose: Identifier -> Ty list -> 'a option) : (InstanceBinding * 'a) option =
    env.Instances
    |> List.tryPick (fun instance ->
        match resolveSoFar state instance.Context with
        | TyApply(ctxName, argTys) -> choose ctxName argTys |> Option.map (fun chosen -> instance, chosen)
        | _ -> None)

/// The arguments of `argTys`, those of the context `ctxName`, that choose its instance: all but its associated types,
/// which the instance fixes (see `AssociatedTypes`).
let private choosingArguments (state: InferState) (ctxName: Identifier) (argTys: Ty list) : Ty list =
    let associated = state.TypeDefs |> Map.tryFind ctxName |> Option.map _.AssociatedTypes.Length |> Option.defaultValue 0
    List.truncate (argTys.Length - min associated argTys.Length) argTys

/// The instance of `ctxName` applied to `argTys` nearest in scope in `env`, with its arguments. It is chosen by the
/// arguments but the context's associated types, which it fixes (see `AssociatedTypes`), and which `argTys` may leave
/// out, as a narrower context's arguments do (see `ContextShapes`).
let private nearestOf (env: TypeEnv) (state: InferState) (ctxName: Identifier) (argTys: Ty list) : (InstanceBinding * Ty list) option =
    let chosenBy =
        state.TypeDefs
        |> Map.tryFind ctxName
        |> Option.map (fun info -> info.Parameters.Length - info.AssociatedTypes.Length)
        |> Option.defaultValue argTys.Length
    nearestInstance env state (fun name instanceArgs ->
        if name = ctxName && (instanceArgs.Length = argTys.Length || argTys.Length = chosenBy) && instanceArgs.Length >= chosenBy
           && List.forall2 sameType (List.truncate chosenBy instanceArgs) (List.truncate chosenBy argTys) then Some instanceArgs
        else None)

/// The instance of `ctxName` applied to `argTys` nearest in scope in `env`, as written where it is put in scope: one of
/// `ctxName` itself, else one of a context that has all its members (see `ContextShapes`), with what put it there.
let private instanceInScope (env: TypeEnv) (state: InferState) (ctxName: Identifier) (argTys: Ty list) : (TypeExpr * ContextProvider) option =
    let ofContext (ctxName: Identifier) =
        nearestOf env state ctxName argTys |> Option.map (fun (instance, _) -> instance.Written, instance.Provider)
    ofContext ctxName
    |> Option.orElse (ContextShapes.wider state (fun other -> contextMemberTypes state other argTys) ctxName |> List.tryPick ofContext)

/// `state` where a call that requires `ctxName` at `argTys` finds the instance nearest in scope in `env`: each of the
/// context's associated types is the instance's (see `nearestOf`).
let private withAssociatedOfInstance (env: TypeEnv) (state: InferState) (span: SourceSpan) (ctxName: Identifier) (argTys: Ty list) (argTysAsRead: Ty list) =
    match nearestOf env state ctxName argTys with
    | Some (_, instanceArgs) ->
        let associated = state.TypeDefs |> Map.tryFind ctxName |> Option.map _.AssociatedTypes.Length |> Option.defaultValue 0
        List.zip instanceArgs argTysAsRead
        |> List.skip (argTys.Length - associated)
        |> List.fold (fun current (instanceTy, argTy) -> addConstraintAt span instanceTy argTy current) state
    | None -> state

/// The member `memberName` of the instance nearest in scope in `env` of a context for the type variable `operandTy`
/// (its first argument) that has that member, as written where it is put in scope, with the member's type there. An
/// operator on values of a type variable calls it (see `OperatorCalls.definition`).
let internal memberOfInstanceInScope (env: TypeEnv) (state: InferState) (memberName: Identifier) (operandTy: Ty) : (TypeExpr * TypeScheme) option =
    match resolveSoFar state operandTy with
    | TyVar operand ->
        nearestInstance env state (fun ctxName argTys ->
            match argTys with
            | TyVar forVar :: _ when forVar.Id = operand.Id ->
                contextMemberTypes state ctxName argTys
                |> Option.bind (List.tryFind (fst >> (=) memberName))
                |> Option.map (fun (_, memberTy) ->
                    let symbol = state.TypeDefs |> Map.tryFind ctxName |> Option.bind (fun info -> info.Members |> Map.tryFind memberName)
                    { TypeEnv.mono memberTy with Symbol = symbol })
            | _ -> None)
        |> Option.map (fun (instance, scheme) -> instance.Written, scheme)
    | _ -> None

/// Whether an instance of a context applied to `argTys` can be built from attached definitions: whether the type it
/// is for, its first argument, has a name definitions can be attached to (not a variable, or a record written out).
/// The others may still be open: the definitions' types settle them, as `int.fromString` makes the associated
/// `failure` of a `FromString(int)` the tag `#invalidInt`. The `Default` of `#some(a) | #none` is built too, from the
/// prelude's definition (see `NoneDefault`).
let private canSynthesize (ctxName: Identifier) (argTys: Ty list) =
    match argTys with
    | forType :: _ -> not (List.isEmpty (attachedTypeNames forType)) || (NoneDefault.tryDefinitions ctxName argTys).IsSome
    | [] -> false

/// The type of each of `args`, the arguments of a parameterised context a function requires, at a call where the
/// function has the type `calleeTy`. `signature` is the type the function declares, which `args` name its type
/// variables from; it is read afresh, and is the callee's type at the call. Without one, the type variables are
/// matched by name against `generalTy`, the callee's type before it was instantiated. Each is given resolved as far as
/// the constraints so far tell, and as read: a type resolved now may have a union rest the unifier made up, which is
/// numbered afresh each time the constraints are solved, so one to constrain later is the type as read. `shared` are
/// the variables the call's other contexts were read with, so that a variable only the contexts name, an associated
/// type such as `elem` in `[Iterable(c), ToString(elem)]`, is one variable across them; the variables read are given
/// back for the next.
let private contextArgTysAtCall (state: InferState) (shared: Map<TypeVarKey, TyVar>) (signature: TypeExpr option) (generalTy: Ty) (calleeTy: Ty) (args: TypeExpr list) : Ty list * Ty list * Map<TypeVarKey, TyVar> * InferState =
    // Read with only the call's own variables in scope, so each other variable is a fresh one, shared by the
    // signature and the arguments. A type the signature names that only an aliased import brings in is written there
    // by its own name (`a -> K` for `m.K`), so it is read as such (#2380).
    let mutable current = { state with TypeVars = shared; HiddenTypeNames = Set.empty }
    let read (typeExpr: TypeExpr) =
        let ty, next = typeExprToTy current typeExpr
        current <- next
        ty
    let argTys =
        match signature with
        | Some signature ->
            let signatureTy = read signature
            let argTys = args |> List.map read
            // A function named as a value requires its own contexts, which its signature leaves out (see
            // `ContextVariables.ofNamedValue`). The signature a local def was inferred to have writes those its value
            // runs in, `[Console] a -> ()` for `{ x -> println(describe(x)) }`, which are not what the arguments name.
            let withoutOwnContexts ty =
                match Unifier.unalias ty with
                | TyFunc(input, output, _) -> TyFunc(input, output, [])
                | other -> other
            current <- addConstraint (withoutOwnContexts signatureTy) (withoutOwnContexts calleeTy) current
            argTys
        | None ->
            let names = namedVarsIn generalTy calleeTy
            args |> List.map (read >> replaceNamedVars names)
    let read = current.TypeVars
    let current = { current with TypeVars = state.TypeVars; HiddenTypeNames = state.HiddenTypeNames }
    argTys |> List.map (resolveSoFar current), argTys, read, current

/// `env` with the instance a `use` of the value of type `valueTy` loads in scope, when it is one of a parameterised
/// context: a value whose type is the context applied to types, as `use Sum(int)(...)` makes (recorded by the node of
/// `value`, see `TypedModule.UseContexts`). A `use` loads only a context's constructor (see `UseDischarge.notLoadable`),
/// so no other value's type is read for the contexts it fits. A number literal in it that nothing has settled is of its
/// default type (see `NumericLiterals`).
let internal useInstance (env: TypeEnv) (state: InferState) (value: Expression) (valueTy: Ty) : TypeEnv * InferState =
    let state = NumericLiterals.settleWithin [ valueTy ] state
    match resolveSoFar state valueTy with
    | TyAlias(ctxName, (_ :: _ as argTys), _)
    | TyApply(ctxName, (_ :: _ as argTys)) when isContextName state ctxName ->
        let context = TyApply(ctxName, argTys)
        match ContextInference.typeExprOf (ContextInference.varNames context) context with
        | Some written ->
            bindInstanceOf env (FromUse value.Id) ctxName argTys written,
            { state with
                UseContexts = state.UseContexts |> Map.add value.Id written
                UseContextTypes = state.UseContextTypes |> Map.add value.Id context }
        | None -> env, state
    | _ -> env, state

/// The instance each `use` loads (see `useInstance`), written again where the rest of the module settled a type it left
/// open when the `use` was checked: a builder's `use Emit(emit = { y -> ... })`, whose element type only the block it
/// runs settles, loads an `Emit(int)`, and the backend finds its members by that (#2550). One still open once the
/// module is inferred, with `subst`, is written as it was.
let internal settledUseContexts (state: InferState) (subst: Subst) : Map<NodeId, TypeExpr> =
    state.UseContexts
    |> Map.map (fun id written ->
        match state.UseContextTypes |> Map.tryFind id |> Option.map (Unifier.applySolved subst) with
        | Some settled when (TypeEnv.freeTyVars settled).IsEmpty -> ContextInference.typeExprOf Map.empty settled |> Option.defaultValue written
        | _ -> written)

/// Where a call that requires `ctxName` at `argTys` finds it when no instance is in scope, if the context is a
/// well-known one (see `WellKnownContexts.atCall`). `hasInstance` says whether an instance of a type is in scope: a part
/// with one, or with the member attached, is compared through it. A type that holds one that attaches `==` and no
/// `hash` has no `Hash` (see `WellKnownContexts.disagreeing`).
let private wellKnownInstanceAtCall (env: TypeEnv) (ctxName: Identifier) (argTys: Ty list) (hasInstance: Ty -> bool) =
    match argTys, WellKnownContexts.tryFind ctxName with
    // Without the prelude there is no instance of the compiler's own to pass.
    | [ ty ], Some wellKnown when (TypeEnv.tryFind wellKnown.BuiltInFunction env).IsSome ->
        let attaches (part: Ty) (memberName: Identifier) =
            not (WellKnownContexts.isBuiltIn part) && (tryResolveAttachedFunction env part memberName).IsSome
        match WellKnownContexts.disagreeing wellKnown attaches ty with
        | Some reason -> Some (wellKnown, WellKnownContexts.NoInstance reason)
        | None ->
            let partHasOwn (part: Ty) = attaches part wellKnown.Member || hasInstance part
            Some (wellKnown, WellKnownContexts.atCall wellKnown (attaches ty wellKnown.Member) hasInstance partHasOwn ty)
    | _ -> None

/// The type among `argTys`, the arguments of the context `ctxName`, that an instance may be derived for from its
/// parts (see `StructuralInstances`): the one a derivable context is for, when no definition attached to it is the
/// context's member. A type with the compiler's own instance keeps that one.
let private derivableType (env: TypeEnv) (ctxName: Identifier) (argTys: Ty list) : Ty option =
    match argTys with
    | [ ty ] when StructuralInstances.derives ctxName
                  && (tryResolveAttachedFunction env ty InterpolationHoles.memberName).IsNone
                  && not (InterpolationHoles.hasBuiltInInstance ty) -> Some ty
    | _ -> None

/// The instance of `ctxName` in scope in `env` at the part `ty` of a structure, for one that is not a type variable
/// (see `StructuralInstances.abstractParts`).
let internal partInstanceIn (env: TypeEnv) (state: InferState) (ctxName: Identifier) (ty: Ty) : (TypeExpr * ContextProvider) option =
    instanceInScope env state ctxName [ ty ]

/// Whether `ty` has an instance of `ctxName`: one in scope in `env`, or one made of a definition attached to it for each
/// of the context's members, as `int.+` is for `Num`'s `+`.
let internal hasInstance (env: TypeEnv) (state: InferState) (ctxName: Identifier) (ty: Ty) : bool =
    (instanceInScope env state ctxName [ ty ]).IsSome
    || (match contextMemberTypes state ctxName [ ty ] with
        | Some members -> members |> List.forall (fun (memberName, _) -> (tryResolveAttachedFunction env ty memberName).IsSome)
        | None -> false)

/// Whether `part` has a `memberName` (`==`, `hash` or `compare`) of its own: one attached to its type, if not a built-in
/// one, an instance of it in scope that `partInstance` finds, or, for a type variable, the instance `hasInstance` says
/// is in scope.
let private hasOwnMember
    (env: TypeEnv)
    (memberName: Identifier)
    (partInstance: Ty -> (TypeExpr * ContextProvider) option)
    (hasInstance: TyVar -> bool)
    (part: Ty)
    : bool =
    match part with
    | TyVar var -> hasInstance var
    // A built-in type's attached `hash` is the compiler's own (see `WellKnownContexts.isBuiltIn`).
    | _ when WellKnownContexts.isBuiltIn part -> (partInstance part).IsSome
    | _ -> (tryResolveAttachedFunction env part memberName).IsSome || (partInstance part).IsSome

/// Whether comparing, hashing or ordering values of the structure `ty` reads a part with a `memberName` of its own
/// (see `hasOwnMember` and `StructuralInstances.comparesParts`).
let internal comparesOwnParts
    (env: TypeEnv)
    (rigid: Set<int>)
    (memberName: Identifier)
    (partInstance: Ty -> (TypeExpr * ContextProvider) option)
    (hasInstance: TyVar -> bool)
    (ty: Ty)
    : bool =
    ty |> StructuralInstances.comparesParts rigid (hasOwnMember env memberName partInstance hasInstance)

/// `state` with the comparison `call`, at `span`, of two values of the type `ty` calling the `==` the type has of its
/// own, if it has one: the `==` attached to it, or, for a structure that compares a part with an `==` of its own,
/// the one derived for it (see `StructuralInstances.comparison`, which `rigid` and `instanceOf` are for). Inside the
/// attached `==` itself the comparison stays the compiler's own, which is how it compares the values underneath.
let internal derivedComparison (env: TypeEnv) (rigid: Set<int>) (instanceOf: TyVar -> ContextInstance option) (call: NodeId) (span: SourceSpan) (ty: Ty) (state: InferState) : InferState option =
    // A variable nothing solves, as the element of an empty list's type, holds no value: it is compared as `unit`, which
    // is what the backend makes it, so that a map or a set beside it is still compared by its entries rather than by the
    // compiler's own comparison, which compares a map's entries in the order they were added (#2522). A variable of the
    // function around, `rigid`, is its caller's to choose, and one with an instance is compared through it.
    let rec settled (ty: Ty) =
        match ty with
        | TyVar var when not (rigid.Contains var.Id) && (instanceOf var).IsNone -> TyPrimitive "unit"
        | _ -> Ty.map settled ty
    let ty = settled ty
    let calling (callee: Identifier, next: InferState) =
        { next with
            CalleeTypes = next.CalleeTypes |> Map.add call (TyFunc(inputTypeFromArgs [ ty; ty ], TyPrimitive "bool", []))
            OperatorCalls = next.OperatorCalls |> Map.add call callee }
    let partInstance = partInstanceIn env state EqualityOperators.context
    match tryResolveAttachedFunction env ty EqualityOperators.equals, state.ContextScope with
    | Some attached, DefScope(name, _) when name = attached -> None
    | Some attached, _ -> Some (calling (attached, state))
    | None, _ when comparesOwnParts env rigid EqualityOperators.equals partInstance (instanceOf >> Option.isSome) ty ->
        StructuralInstances.comparison rigid partInstance instanceOf call span ty state |> Option.map calling
    | None, _ -> None

/// The instance of `Eq`, `Hash` or `Ord` (`wellKnown`, found as `instance`) the call `call` to `calleeName`, at `span`
/// in `env`, finds for the structure `ty` in place of the compiler's own, when `ty` compares, hashes or orders a part with
/// an `==`, `hash` or `compare` of its own, or, for `Eq`, holds an array: the derived definition, as the context's
/// member. An order is derived only for a tuple or a list whose every part has one (#2748). A
/// variable of `ty` is read with the instance `instanceOf` finds for it, and `rigid` holds the variables of the
/// function around the call (see `StructuralInstances.instance`). A type with variables in it that holds an array
/// and has no derived `==` is an error, since the compiler's own cannot compare it, and so is one whose parts' own
/// `hash` cannot be read, a union open to the caller's tags, since the compiler's own would not read them.
let private derivedEquality
    (env: TypeEnv)
    (state: InferState)
    (rigid: Set<int>)
    (instanceOf: TyVar -> ContextInstance option)
    (wellKnown: WellKnownContexts.WellKnownContext)
    (instance: WellKnownContexts.CallInstance)
    (calleeName: Identifier)
    (call: NodeId)
    (span: SourceSpan)
    (ty: Ty)
    : Result<ContextInstance * InferState, Diagnostic list> option =
    let partInstance = partInstanceIn env state wellKnown.Context
    let isHash = wellKnown.Context = WellKnownContexts.hashing.Context
    let isOrd = wellKnown.Context = WellKnownContexts.ordering.Context
    if (wellKnown.Context <> EqualityOperators.context && not isHash && not isOrd) || instance <> WellKnownContexts.BuiltIn then None
    else
        let hasOwn = hasOwnMember env wellKnown.Member partInstance (instanceOf >> Option.isSome)
        // An order is derived only for a tuple or a list whose every part has one, its own or the compiler's.
        let readsParts =
            comparesOwnParts env rigid wellKnown.Member partInstance (instanceOf >> Option.isSome) ty
            && (not isOrd || (wellKnown.Lacks hasOwn ty).IsNone)
        let derived =
            if readsParts then
                StructuralInstances.instance rigid partInstance instanceOf false call span wellKnown.Context ty state
                |> Option.bind Result.toOption
            else None
        match derived with
        | Some derived -> Some (Ok derived)
        | None when isHash && readsParts ->
            Some (Error (WellKnownContexts.error wellKnown.Context ty span (WellKnownContexts.noInstanceAtCall wellKnown calleeName ty "its parts' own hash cannot be read where the caller chooses more of its tags")))
        | None when isOrd -> None
        | None when not (TypeEnv.freeTyVars ty).IsEmpty && StructuralInstances.holdsCollection ty ->
            Some (Error (WellKnownContexts.error wellKnown.Context ty span (EqualityOperators.collectionAtGenericCall calleeName ty)))
        | None -> None

/// The instance in scope in `env` of `ctxName` of the type variable `var`, as a derived instance is passed it.
let private variableInstanceIn (env: TypeEnv) (state: InferState) (ctxName: Identifier) (var: TyVar) : ContextInstance option =
    instanceInScope env state ctxName [ TyVar var ] |> Option.map InstanceInScope

/// The instance of `ctxName` at `argTys` derived from the type's parts for `call`, a call to `calleeName` at `span`
/// in `env`, if one can be (see `StructuralInstances.instance`, which `rigid` and `instanceOf` are for). `annotatedDef`
/// is the def around the call, if it writes its type.
let private derivedInstance
    (env: TypeEnv)
    (state: InferState)
    (rigid: Set<int>)
    (instanceOf: TyVar -> ContextInstance option)
    (annotatedDef: Identifier option)
    (calleeName: Identifier)
    (call: NodeId)
    (span: SourceSpan)
    (ctxName: Identifier)
    (argTys: Ty list)
    : Result<ContextInstance * InferState, Diagnostic list> option =
    derivableType env ctxName argTys
    |> Option.bind (fun ty -> StructuralInstances.instance rigid (partInstanceIn env state ctxName) instanceOf (InterpolationHoles.isHole calleeName) call span ctxName ty state)
    |> Option.map (Result.mapError (fun failure ->
        let missingContext message = Diagnostics.at DiagnosticCodes.MissingContextMembers span message |> Diagnostics.about (WrittenContexts.missingText state ctxName argTys)
        match failure with
        | StructuralInstances.NoInstanceOf missing when InterpolationHoles.isHole calleeName ->
            [ missingContext (InterpolationHoles.undeclared state (TyVar missing) annotatedDef) |> InterpolationHoles.addToStringFix state (TyVar missing) annotatedDef ]
        | StructuralInstances.NoInstanceOf missing ->
            [ missingContext (InterpolationHoles.undeclaredPart state calleeName argTys.Head (TyVar missing) annotatedDef)
              |> InterpolationHoles.addToStringFix state (TyVar missing) annotatedDef ]
        | StructuralInstances.HoldsFunction held -> [ missingContext (InterpolationHoles.holdsFunction calleeName argTys held) ]
        | StructuralInstances.OpenToCaller -> [ missingContext (InterpolationHoles.openToCaller calleeName argTys.Head annotatedDef) ]))

/// The definition `qualifier.field` names, as `math.max` or `int.toString` do, with its scheme: none when it reads a
/// field of the value `qualifier`. A local value named as a built-in type, as `list` in `{ list -> list.length }`, is
/// not the type. A top-level def of that name is not a local, so `map.empty` is the map's still where a program defines
/// a `map`, and so is a library function's that is checked in the program's module (#1841).
let internal qualifiedDefinition (env: TypeEnv) (state: InferState) (qualifier: Identifier) (field: Identifier) : (Identifier * TypeScheme) option =
    let qualifiedName = $"{qualifier}.{field}"
    let isLocalValue =
        match TypeEnv.tryFind qualifier env with
        | Some scheme ->
            match scheme.Symbol |> Option.bind (fun id -> state.Symbols |> Map.tryFind id) with
            | Some symbol -> symbol.Kind <> TopLevelValue
            | None -> true
        | None -> false
    if isBuiltInTypeName qualifier && isLocalValue then None
    else TypeEnv.tryFind qualifiedName env |> Option.map (fun scheme -> qualifiedName, scheme)

/// The context `ctxName` at the arguments of the instance of it nearest in scope in `env`, when that is for a type with
/// no variables in it, not `argTys`. One for a variable, as a def that declares the context has, may be the one.
let private otherInstanceInScope (env: TypeEnv) (state: InferState) (ctxName: Identifier) (argTys: Ty list) : Ty option =
    nearestInstance env state (fun name instanceArgs -> if name = ctxName then Some instanceArgs else None)
    |> Option.filter (fun (_, instanceArgs) -> instanceArgs |> List.forall (TypeEnv.freeTyVars >> Set.isEmpty))
    |> Option.filter (fun _ -> (instanceInScope env state ctxName argTys).IsNone)
    |> Option.map (fun (_, instanceArgs) -> TyApply(ctxName, instanceArgs))

/// `state` where a call that requires `ctxName` at `argTys` settles the nearest instance of that context in scope in
/// `env`, when its arguments, as far as the constraints so far tell, still hold variables that `argTys` fit: a lambda
/// checked against `[c, Emit(b)] () -> ()`, where `b` is a builder's own and still open, has the instance `Emit(b)` in
/// scope, and a call inside it to a function that requires `Emit(int)` makes `b` an `int`, as calling the member `emit`
/// with an `int` would (#2566). `None` when the nearest instance of the context is settled already, or the call doesn't
/// fit it: a nearer one is never passed over for one further out. A variable a def's annotation writes is rigid, so
/// settling one here is the error that check reports.
let private settleInstanceInScope (env: TypeEnv) (state: InferState) (ctxName: Identifier) (argTys: Ty list) (span: SourceSpan) : InferState option =
    nearestInstance env state (fun name instanceArgs -> if name = ctxName then Some instanceArgs else None)
    |> Option.bind (fun (_, instanceArgs) ->
        let pairs = if instanceArgs.Length = argTys.Length then List.zip instanceArgs argTys else []
        if not pairs.IsEmpty
           && instanceArgs |> List.exists (TypeEnv.freeTyVars >> Set.isEmpty >> not)
           && Solving.unify ((pairs |> List.map (fun (instanceArg, argTy) -> instanceArg, argTy, Equal, span)) @ Solving.agreeing state.Constraints) |> Result.isOk
        then Some (pairs |> List.fold (fun current (instanceArg, argTy) -> addConstraintAt span instanceArg argTy current) state)
        else None)

/// The error for a call of `name`, at `span`, that requires `ctxName` at a type nothing settles, where the instance of
/// it in scope is `other`, for another type.
let private unsettledBesideOther (name: Identifier) (ctxName: Identifier) (other: Ty) (span: SourceSpan) : Diagnostic =
    Diagnostics.at DiagnosticCodes.MissingContextMembers span
        $"'{name}' requires the context '{ctxName}' at a type nothing here settles, and the instance in scope is a '{Unifier.tyToString other}': write the type the call gives, or load an instance of '{ctxName}' for it with `use`"
    |> Diagnostics.about ctxName

/// `state` with what each associated type of the context `ctxName`, which the callee of `call` requires written with
/// `args`, is at the call, `argTys`, by the name the callee's signature gives it (see `TypedModule.AssociatedArguments`).
/// One the signature fixes, `[Iterable(c, elem = int)]`, is recorded by the context's name for it, which the types of
/// the context's members name it by.
let private recordAssociatedArguments (state: InferState) (call: NodeId) (ctxName: Identifier) (args: TypeExpr list) (argTys: Ty list) =
    match tryFindTypeDef ctxName state with
    | Some info when not info.AssociatedTypes.IsEmpty && args.Length = argTys.Length ->
        let named =
            List.zip3 (List.skip (args.Length - info.AssociatedTypes.Length) args) info.AssociatedTypes (List.skip (args.Length - info.AssociatedTypes.Length) argTys)
            |> List.map (fun (arg, associated, ty) -> match arg.Kind with TypeVar name -> name, ty | _ -> associated, ty)
        let recorded = state.AssociatedArguments |> Map.tryFind call |> Option.defaultValue Map.empty
        { state with AssociatedArguments = state.AssociatedArguments |> Map.add call (named |> List.fold (fun acc (name, ty) -> acc |> Map.add name ty) recorded) }
    | _ -> state

/// The names of the associated types of the context `ctxName` and the arguments it is applied to for them, of `argTys`:
/// the arguments after those an instance is chosen by (see `AssociatedTypes`).
/// The associated types of the context `ctxName` that an instance at `argTys` in `env` leaves unfixed, by name: each
/// whose argument has a type variable that neither the arguments it is chosen by have nor the instances in scope, as a
/// signature's `elem` is. `madeUp` keeps only a variable no signature names, as a `use`'s `_` is.
let private unfixedAssociated (env: TypeEnv) (state: InferState) (madeUp: bool) (ctxName: Identifier) (argTys: Ty list) : string list =
    match WrittenContexts.associatedArguments state ctxName argTys with
    | [] -> []
    | associated ->
        let chosenBy = argTys |> List.truncate (argTys.Length - associated.Length) |> List.map (resolveSoFar state)
        let inScope = TypeEnv.freeEnvVars env |> Seq.map (fun id -> TypeEnv.freeTyVars (resolveSoFar state (TyVar (TyVar.ordinary id None)))) |> Set.unionMany
        let fixedVars = chosenBy |> List.map TypeEnv.freeTyVars |> Set.unionMany |> Set.union inScope
        let rec open' (ty: Ty) =
            match ty with
            | TyVar v -> not (fixedVars.Contains v.Id) && (not madeUp || v.Name.IsNone)
            | _ -> Ty.exists open' ty
        associated |> List.filter (snd >> resolveSoFar state >> open') |> List.map fst

/// The error for the instance of `ctxName` at `argTys` that leaves the associated types `names` unfixed, at `span`: one
/// a `use` loads (`inUse`) says what it is by name, and one built from a type's definitions with an attached alias.
let private unfixedError (state: InferState) (inUse: bool) (ctxName: Identifier) (argTys: Ty list) (forType: string) (names: string list) (span: SourceSpan) =
    let quoted = names |> List.map (fun name -> $"'{name}'")
    let types = if names.Length = 1 then "type" else "types"
    let fix =
        if inUse then
            let positional = argTys |> List.truncate (argTys.Length - (WrittenContexts.associatedArguments state ctxName argTys).Length) |> List.map (resolveSoFar state >> Unifier.tyToString)
            $"""fix it by name, as in `use {ctxName}({String.concat ", " (positional @ [ $"{names.Head} = ..." ])})(...)`"""
        else $"declare it, as in `type {forType}.{names.Head} = ...`"
    Diagnostics.at DiagnosticCodes.UnfixedAssociatedType span
        $"The instance '{WrittenContexts.instanceText state ctxName argTys}' leaves its associated {types} {Diagnostics.listed quoted} unfixed: no member's type says what it is, so {fix}"

/// `state` with each associated type of the instance of `ctxName` at `argTys` for the type `attachTo` that the type
/// declares with an attached alias, `type Bag.elem = int`, fixed as it says.
let private withAttachedAssociated (state: InferState) (attachTo: string) (ctxName: Identifier) (argTys: Ty list) (span: SourceSpan) =
    WrittenContexts.associatedArguments state ctxName argTys
    |> List.fold (fun current (name, ty) ->
        match current.TypeDefs |> Map.tryFind $"{attachTo}.{name}" with
        | Some info when info.Parameters.IsEmpty && not info.IsContext -> addConstraintAt span ty info.Underlying current
        | _ -> current) state

/// What the member `memberName` of the instance of `ctxName` at `argTys`, defined by `definition` of the type
/// `definitionTy`, would fix the associated type among `opened` it disagrees on as, where its other members fixed it
/// otherwise: the associated type's name, what they fixed it as, and what the definition would.
let private fixedTwoWays (state: InferState) (ctxName: Identifier) (argTys: Ty list) (opened: Set<string>) (memberName: string) (definitionTy: Ty) (span: SourceSpan) =
    let associated = WrittenContexts.associatedArguments state ctxName argTys
    let chosen = argTys.Length - associated.Length
    let fresh, current =
        associated
        |> List.mapFold (fun current (name, ty) -> if opened.Contains name then freshVar current else ty, current) state
    match contextMemberTypes current ctxName (List.truncate chosen argTys @ fresh) |> Option.bind (List.tryFind (fst >> (=) memberName)) with
    | Some (_, memberTy) ->
        match Solving.unify ((memberTy, definitionTy, Equal, span) :: Solving.agreeing current.Constraints) with
        | Ok subst ->
            List.zip associated fresh
            |> List.tryPick (fun ((name, ty), freshTy) ->
                let earlier = resolveSoFar state ty
                let own = Unifier.apply subst freshTy
                if opened.Contains name && Solving.unify [ earlier, own, Equal, span ] |> Result.isError then Some (name, earlier, own) else None)
        | Error _ -> None
    | None -> None

/// The instance of `ctxName` applied to `argTys` built from the definitions attached to the type it is for, its first
/// argument, as `Point.0` and `Point.+` make a `Sum(Point)`, or from the prelude's for the `Default` of
/// `#some(a) | #none`: each member's name and the definition it is. The definitions' types must be the members' types,
/// which settles any other argument still open. `calleeName` requires the context at `span`. A context of one member
/// whose definition requires parameterised contexts of its own, as `Queue.==` requires `Eq(a)`, is that definition
/// passed the instances a call to it at the member's type finds (`InstanceDerived`), as an instance derived from a
/// type's parts is. The context's associated types are what the definitions' types fix them as, and what an alias
/// attached to the type, `type Bag.elem = int`, says; one they leave open is an error, as is one they fix two ways.
let rec private synthesizeInstance (env: TypeEnv) (state: InferState) (calleeName: Identifier) (ctxName: Identifier) (argTys: Ty list) (span: SourceSpan) : Result<ContextInstance * InferState, Diagnostic list> =
    let ctxText = WrittenContexts.instanceText state ctxName argTys
    let requirer = InterpolationHoles.requirer calleeName argTys
    let fail message = Error [ Diagnostics.at DiagnosticCodes.MissingContextMembers span message |> Diagnostics.about ctxText ]
    match argTys, contextMemberTypes state ctxName argTys with
    | forType :: _, Some members ->
        // The name a definition for the type is attached under: `Point`, `Money`, `int`.
        let attachTo = attachedTypeNames forType |> List.tryHead |> Option.defaultValue (Unifier.tyToStringIn state.TypeDefs forType)
        let definitionOf memberName =
            match NoneDefault.tryDefinitions ctxName argTys with
            | Some definitions -> definitions |> List.tryFind (fst >> (=) memberName) |> Option.map snd |> Option.filter (fun name -> (TypeEnv.tryFind name env).IsSome)
            | None -> tryResolveAttachedFunction env forType memberName
        let found = members |> List.map (fun (memberName, memberTy) -> memberName, memberTy, definitionOf memberName)
        // The associated types the call leaves to the instance, which its members fix.
        let opened = unfixedAssociated env state false ctxName argTys |> Set.ofList
        match found |> List.filter (fun (_, _, definition) -> definition.IsNone) |> List.map (fun (memberName, _, _) -> $"'{attachTo}.{memberName}'") with
        | [] ->
            found
            |> List.fold (fun result (memberName, memberTy, definition) ->
                result
                |> Result.bind (fun (bound, current: InferState) ->
                    let definition = definition.Value
                    let definitionTy, next = instantiate current (TypeEnv.tryFind definition env).Value
                    // An error earlier in the module is set aside (see `Solving.agreeing`).
                    match Solving.unify ((memberTy, definitionTy, Equal, span) :: Solving.agreeing next.Constraints) with
                    | Ok _ -> Ok (bound @ [ memberName, definition, definitionTy ], addConstraintAt span memberTy definitionTy next)
                    | Error _ ->
                        match fixedTwoWays next ctxName argTys opened memberName definitionTy span with
                        | Some (name, earlier, own) ->
                            Error [ Diagnostics.at DiagnosticCodes.AssociatedTypeFixedTwice span
                                        $"The instance '{WrittenContexts.instanceText state ctxName argTys}' fixes its associated type '{name}' two ways: '{definition}' makes it '{Unifier.tyToStringIn state.TypeDefs own}', where it is already '{Unifier.tyToStringIn state.TypeDefs earlier}'" ]
                        | None ->
                            fail $"{requirer} requires the context '{ctxText}', but '{definition}' has the type '{Unifier.tyToStringIn state.TypeDefs (resolveSoFar next definitionTy)}', where '{memberName}' must be '{Unifier.tyToStringIn state.TypeDefs (resolveSoFar next memberTy)}'"))
                (Ok ([], withAttachedAssociated state attachTo ctxName argTys span))
            |> Result.bind (fun (bound, next) ->
                match unfixedAssociated env next false ctxName argTys |> List.filter opened.Contains with
                | [] -> Ok (bound, next)
                | unfixed -> Error [ unfixedError state false ctxName argTys attachTo unfixed span ])
            |> Result.bind (fun (bound, next) ->
                let synthesized = InstanceSynthesized (bound |> List.map (fun (memberName, definition, _) -> memberName, definition))
                match bound with
                | [ memberName, definition, definitionTy ] ->
                    // The contexts the definition requires are found as for a call to it, at a node of their own.
                    let at = Expression.at span (IdentifierExpr definition)
                    resolveInstancesAt false env next at definition (Some definitionTy) span
                    |> Result.map (fun next ->
                        let foundLater = next.PendingContextInstances |> List.exists (fun pending -> pending.Call = at.Id)
                        if next.ContextInstances.ContainsKey at.Id || foundLater then InstanceDerived(memberName, definition, at.Id), next
                        else synthesized, next)
                | _ -> Ok (synthesized, next))
        | _ when InterpolationHoles.isHole calleeName -> fail (InterpolationHoles.noInstance forType)
        | missing ->
            let notDefined = if missing.Length = 1 then "is not defined: define it" else "are not defined: define them"
            fail $"{requirer} requires the context '{ctxText}', which is not loaded here, and {Diagnostics.listed missing} {notDefined}, or load an instance with `use`"
    | _ -> fail $"{requirer} requires the context '{ctxText}', which is not loaded here: load an instance with `use`"

/// Finds where `call`, the node where `name` is called at the type `calleeTy` (or, when `reference`, named as a value
/// of that type), finds each parameterised context the callee requires (see `TypedModule.ContextInstances`), once its
/// arguments have settled what the context is for. A context for a type inference has not worked out yet is left to
/// be found by its members' names, as one that is not parameterised is. A function named as a value whose type is not
/// settled there finds its instance later, as such a call does (see `resolveReferenceInstances`).
and private resolveInstancesAt (reference: bool) (env: TypeEnv) (state: InferState) (call: Expression) (name: Identifier) (calleeTy: Ty option) (calleeSpan: SourceSpan) : Result<InferState, Diagnostic list> =
    let contexts = contextRequirementsOf env state name |> Option.defaultValue []
    // A call with an argument that never gives a value (of type `undefined`) never happens: its parameter's type is
    // left open, and it has every context, as a call with an argument that failed to check has.
    let neverHappens =
        match call.Kind with
        | FunctionCall(_, _, args) when not reference ->
            args |> List.exists (fun arg -> state.ExprTypes |> Map.tryFind arg.Id |> Option.exists (resolveSoFar state >> Unifier.isBottom))
        | _ -> false
    match TypeEnv.tryFind name env, calleeTy with
    | Some _, Some _ when neverHappens && not (InterpolationHoles.isHole name) && contexts |> List.exists (isParameterizedContext state) ->
        let found =
            contexts
            |> List.map (fun ctx ->
                match ctx.Kind with
                | TypeApply(ctxName, _) when ctxName = InterpolationHoles.context && (TypeEnv.tryFind InterpolationHoles.builtInFunction env).IsSome ->
                    Some (InstanceSynthesized InterpolationHoles.builtInMembers)
                | _ -> None)
        Ok (if found |> List.forall Option.isNone then state else { state with ContextInstances = state.ContextInstances |> Map.add call.Id found })
    | Some scheme, Some calleeTy when contexts |> List.exists (isParameterizedContext state) ->
        // The variables the call's contexts are read with, one variable for each name across them.
        let shared = ref Map.empty
        contexts
        |> List.fold (fun result ctx ->
            result
            |> Result.bind (fun (found, current: InferState) ->
                match ctx.Kind with
                | TypeApply(ctxName, args) when isParameterizedContext current ctx ->
                    let signature = contextSignatureOf env current name
                    let before = shared.Value
                    let argTys, argTysAsRead, read, current = contextArgTysAtCall current before signature scheme.Type calleeTy args
                    // A number literal nothing has settled yet is of its default type here (see `NumericLiterals`), and so
                    // is one a pending `/` divides, whose fraction the argument may be (see `Fractions.settleIn`).
                    let argTys, argTysAsRead, read, current =
                        match Fractions.settleIn argTys current with
                        | Some literalsSettled -> contextArgTysAtCall literalsSettled before signature scheme.Type calleeTy args
                        | None -> argTys, argTysAsRead, read, current
                    shared.Value <- read
                    // A dimension's `Add` and `Sub` are its storage's (see `Units.contexts`).
                    let argTys, argTysAsRead = Units.storageArguments current ctxName argTys, Units.storageArguments current ctxName argTysAsRead
                    let settled = choosingArguments current ctxName argTys |> List.forall (TypeEnv.freeTyVars >> Set.isEmpty)
                    let current = recordAssociatedArguments current call.Id ctxName args argTysAsRead
                    // A well-known context of a type with no member attached is the compiler's own (see
                    // `WellKnownContexts.resolveCall`). `Eq` of a structure with a part that has an `==` of its own
                    // is derived from its parts.
                    // Until the def around the call is inferred, any variable of the type may turn out to stand for one
                    // its callers choose, so a union open to more tags is left to the compiler's own.
                    let rigid = argTys |> List.map TypeEnv.freeTyVars |> Set.unionMany
                    // The instance in scope whose open arguments the call settles, found with what it settles.
                    let settledInScope (current: InferState) =
                        settleInstanceInScope env current ctxName argTys calleeSpan
                        |> Option.bind (fun next ->
                            instanceInScope env next ctxName argTys
                            |> Option.map (fun inScope ->
                                found @ [ Some (InstanceInScope inScope) ], withAssociatedOfInstance env next calleeSpan ctxName argTys argTysAsRead))
                    let wellKnown =
                        if reference && not settled then None
                        else
                            wellKnownInstanceAtCall env ctxName argTys (fun ty -> (instanceInScope env current ctxName [ ty ]).IsSome)
                            |> Option.bind (fun (wellKnown, instance) ->
                                match derivedEquality env current rigid (variableInstanceIn env current ctxName) wellKnown instance name call.Id calleeSpan argTys.Head with
                                | Some derived -> Some (derived |> Result.map (fun (derived, next) -> found @ [ Some derived ], next))
                                | None -> WellKnownContexts.resolveCall wellKnown env current call name calleeSpan found contexts.Length argTys.Head instance)
                    // A structure with nothing left to settle has the instance derived from its parts.
                    let derived () =
                        if settled then
                            derivedInstance env current Set.empty (fun _ -> None) None name call.Id calleeSpan ctxName argTys
                            |> Option.map (Result.map (fun (instance, next) -> found @ [ Some instance ], next))
                        else None
                    match instanceInScope env current ctxName argTys, wellKnown |> Option.orElseWith derived with
                    | Some inScope, _ ->
                        Ok (found @ [ Some (InstanceInScope inScope) ], withAssociatedOfInstance env current calleeSpan ctxName argTys argTysAsRead)
                    // A function named as a value at a type not settled yet, whose context is not in scope by its
                    // members' names, finds its instance once something settles the type, as a call does: in an
                    // unannotated def once the def is inferred, and anywhere else once the module is, such as where
                    // only the type a def writes settles it (`def u: Point = apply(des, "ab")`).
                    | None, _ when reference && not settled ->
                        match ensureContextAvailable (Some call.Id) true env current name [ ctx ] (Some (WrittenContexts.missingText current ctxName argTys)) calleeSpan, current.ContextScope with
                        | Ok _, _ -> Ok (found @ [ None ], current)
                        | Error errors, scope when not (snd (Solving.solve current.Constraints) |> List.exists (snd >> SourceSpan.contains call.Span)) ->
                            let def, annotatedDef =
                                match scope with
                                | DefScope(defName, true) -> defName, None
                                | DefScope(defName, false) -> InterpolationHoles.moduleEnd, Some defName
                                | AmbientScope
                                | ScriptValueScope _
                                | ValueScope _ -> InterpolationHoles.moduleEnd, None
                            let pending =
                                { Def = def; Call = call.Id; Callee = name; Index = found.Length; Count = contexts.Length
                                  Context = ctxName; ArgTys = argTysAsRead; Span = calleeSpan; Errors = errors; ChecksOnly = false }
                            Ok (found @ [ None ],
                                { current with
                                    PendingContextInstances = pending :: current.PendingContextInstances
                                    PendingReferences = current.PendingReferences.Add call.Id
                                    HoleScopes = current.HoleScopes |> Map.add call.Id (env, annotatedDef) })
                        | Error _, _ -> Ok (found @ [ None ], current)
                    // A value that failed to check, or one made of such a value (a list of them), has every context, so
                    // that the call is not an error too, and so does one that never gives a value (of type `undefined`). It has the compiler's own `ToString`, so the backend still has
                    // a member to pass. A hole of one is written by the backend, which reports the error where the hole is.
                    | None, _ when not (InterpolationHoles.isHole name) && argTys |> List.exists (fun ty -> Unifier.holdsError ty || Unifier.isBottom ty) ->
                        if ctxName = InterpolationHoles.context && (TypeEnv.tryFind InterpolationHoles.builtInFunction env).IsSome then
                            Ok (found @ [ Some (InstanceSynthesized InterpolationHoles.builtInMembers) ], current)
                        else Ok (found @ [ None ], current)
                    | None, Some resolved -> resolved
                    // A structure with more to settle finds its instance once its type is known, as a hole does: when
                    // the def around it is inferred, if that def does not write its type, and otherwise once the module
                    // is inferred.
                    | None, None when not settled && (derivableType env ctxName argTys |> Option.bind (StructuralInstances.variables Set.empty)).IsSome ->
                        match current.ContextScope with
                        | _ when InterpolationHoles.isHole name ->
                            InterpolationHoles.resolveLater env current call ctxName argTys calleeSpan found contexts.Length
                        | scope ->
                            let def, annotatedDef =
                                match scope with
                                | DefScope(defName, true) -> defName, None
                                | DefScope(defName, false) -> InterpolationHoles.moduleEnd, Some defName
                                | AmbientScope
                                | ScriptValueScope _
                                | ValueScope _ -> InterpolationHoles.moduleEnd, None
                            let errors =
                                ContextInference.varsInOrder argTys.Head
                                |> List.truncate 1
                                |> List.map (fun var ->
                                    Diagnostics.at DiagnosticCodes.MissingContextMembers calleeSpan (InterpolationHoles.undeclaredPart current name argTys.Head (TyVar var) annotatedDef)
                                    |> Diagnostics.about (WrittenContexts.missingText current ctxName argTys)
                                    |> InterpolationHoles.addToStringFix current (TyVar var) annotatedDef)
                            let pending =
                                { Def = def; Call = call.Id; Callee = name; Index = found.Length; Count = contexts.Length
                                  Context = ctxName; ArgTys = argTys; Span = calleeSpan; Errors = errors; ChecksOnly = false }
                            Ok (found @ [ None ],
                                { current with
                                    PendingContextInstances = pending :: current.PendingContextInstances
                                    HoleScopes = current.HoleScopes |> Map.add call.Id (env, annotatedDef) })
                    // A hole of a built-in type is written by the backend, not by the prelude's `int.toString` and the
                    // others, which are written with one.
                    | None, None when InterpolationHoles.isHole name && argTys |> List.forall InterpolationHoles.hasBuiltInInstance ->
                        Ok (found @ [ Some InstanceBuiltIn ], current)
                    // A function is passed the compiler's own `ToString` of a tag as a function that writes it.
                    | None, None when InterpolationHoles.passesBuiltIn env ctxName argTys ->
                        Ok (found @ [ Some (InstanceSynthesized InterpolationHoles.builtInMembers) ], current)
                    // Every type has the compiler's own `Typed` (see `TypeValues`).
                    | None, None when TypeValues.passesBuiltIn env ctxName argTys ->
                        Ok (found @ [ Some (InstanceSynthesized TypeValues.builtInMembers) ], current)
                    | None, None when canSynthesize ctxName argTys ->
                        match synthesizeInstance env current name ctxName argTys calleeSpan, settledInScope current with
                        | Ok (instance, next), _ -> Ok (found @ [ Some instance ], next)
                        | Error _, Some settled -> Ok settled
                        | Error errors, None -> Error errors
                    | None, None when InterpolationHoles.isHole name ->
                        InterpolationHoles.resolveLater env current call ctxName argTys calleeSpan found contexts.Length
                    | None, None when (settledInScope current).IsSome -> Ok (settledInScope current).Value
                    | None, None ->
                        // An instance of the context at other arguments puts its members in scope by their names, but
                        // it is not one for a type the call leaves open: that is found once the type is known.
                        let available =
                            match otherInstanceInScope env current ctxName argTys with
                            | Some other when not settled -> Error [ unsettledBesideOther name ctxName other calleeSpan ]
                            | _ -> ensureContextAvailable (Some call.Id) true env current name [ ctx ] (Some (WrittenContexts.missingText current ctxName argTys)) calleeSpan
                        let argumentFailed = lazy (Solving.failsWithin call.Span current.Constraints)
                        match available, current.ContextScope with
                        | Ok next, _ -> Ok (found @ [ None ], next)
                        // In an unannotated def, a type the call leaves open may be settled by the rest of the body,
                        // or be one of the def's own variables, which the def then requires the context at.
                        | Error errors, DefScope(defName, true) when argTys |> List.exists (TypeEnv.freeTyVars >> Set.isEmpty >> not) ->
                            let pending =
                                { Def = defName; Call = call.Id; Callee = name; Index = found.Length; Count = contexts.Length
                                  Context = ctxName; ArgTys = argTysAsRead; Span = calleeSpan; Errors = errors; ChecksOnly = false }
                            Ok (found @ [ None ], { current with PendingContextInstances = pending :: current.PendingContextInstances })
                        // Anywhere else, the type the call's result is expected to have may settle it, such as the
                        // type a def writes, which is constrained once its value is inferred: it is found once the
                        // module is, as a structure's is, and is the same error then if nothing has. Arguments that
                        // do not fit the callee are the error instead (see `inferExpr`).
                        | Error errors, scope when
                            argTys |> List.exists (TypeEnv.freeTyVars >> Set.isEmpty >> not)
                            && not argumentFailed.Value ->
                            let annotatedDef =
                                match scope with
                                | DefScope(defName, _) -> Some defName
                                | AmbientScope
                                | ScriptValueScope _
                                | ValueScope _ -> None
                            let pending =
                                { Def = InterpolationHoles.moduleEnd; Call = call.Id; Callee = name; Index = found.Length; Count = contexts.Length
                                  Context = ctxName; ArgTys = argTysAsRead; Span = calleeSpan; Errors = errors; ChecksOnly = false }
                            Ok (found @ [ None ],
                                { current with
                                    PendingContextInstances = pending :: current.PendingContextInstances
                                    HoleScopes = current.HoleScopes |> Map.add call.Id (env, annotatedDef) })
                        // Where an argument failed to check, as `1.5s / 1ms` does, its storages a `float` and an `int`,
                        // that is the error, reported where it is: the context of a type it left unsettled is not (#2852).
                        // A `for` loop's `__toSeq(xs)` is left to fail: `inferExpr` reads it as the call it stands for (see `PipeTargets`).
                        | Error _, _ when
                            name <> ForSyntax.toSeq
                            && argumentFailed.Value ->
                            Ok (found @ [ None ], current)
                        | Error errors, _ -> Error errors
                | _ -> Ok (found @ [ None ], current))) (Ok ([], state))
        |> Result.map (fun (found, current) ->
            if found |> List.forall Option.isNone then current
            else { current with ContextInstances = current.ContextInstances |> Map.add call.Id found })
    | _ -> Ok state

/// Whether the type an instance of `ctxName` at `argTys` is for, its first argument, has an instance of its own, which no
/// `use` can replace: one built from its attached definitions, derived from its parts, or the compiler's own. A type
/// with variables in it has none of its own, since what the variables are is not decided where the `use` is. `call`
/// and `span` are the `use`'s, which nothing is recorded for: what finding the instance would add is dropped.
let private hasOwnInstance (env: TypeEnv) (state: InferState) (call: NodeId) (span: SourceSpan) (ctxName: Identifier) (argTys: Ty list) : bool =
    match argTys with
    | forType :: _ when (TypeEnv.freeTyVars forType).IsEmpty ->
        match wellKnownInstanceAtCall env ctxName argTys (fun _ -> false) with
        | Some (_, (WellKnownContexts.Attached | WellKnownContexts.BuiltIn)) -> true
        | Some _ -> false
        | None ->
            (ctxName = InterpolationHoles.context && InterpolationHoles.hasBuiltInInstance forType)
            || InterpolationHoles.passesBuiltIn env ctxName argTys
            || (derivedInstance env state Set.empty (fun _ -> None) None ctxName call span ctxName argTys |> Option.exists Result.isOk)
            || (canSynthesize ctxName argTys && synthesizeInstance env state ctxName ctxName argTys span |> Result.isOk)
    | _ -> false

/// What a type's own instance of `ctxName` is called in the error for a `use` that would replace it: the member, for a
/// context of one, and the context's name for any other.
let private ownInstanceText (state: InferState) (ctxName: Identifier) (argTys: Ty list) =
    match contextMemberTypes state ctxName argTys with
    | Some [ memberName, _ ] -> $"`{memberName}`"
    | _ -> $"its own {ctxName}"

/// What else to do for a different instance of `ctxName` than a type's own, in the error for a `use` that would replace it.
let private ownInstanceAdvice (ctxName: Identifier) =
    if ctxName = EqualityOperators.context then
        "for a different equality, wrap the value in a protected type with its own `==`, or pass the comparison to a `...By` function"
    elif ctxName = WellKnownContexts.ordering.Context then
        "for a different order, wrap the value in a protected type with its own `compare`, or pass the comparison to a `...By` function"
    elif ctxName = WellKnownContexts.hashing.Context then
        "for a different hash, wrap the value in a protected type with its own `==` and `hash`"
    elif ctxName = InterpolationHoles.context then
        "to write it differently, call a function in the hole, or wrap the value in a protected type with its own `toString`"
    else
        $"for a different {ctxName}, wrap the value in a protected type with its own definitions"

/// The errors for `value`, loaded with `use` into `env` to give `withUse`, for each instance it loads for a type that has
/// its own (see `hasOwnInstance`): a `use` can't replace a type's own instance, so `1 == 2` reads `int`'s `==`
/// wherever it is written, and a set built in one place is read with the same `Hash` in another. So is each instance it
/// loads whose members leave one of the context's associated types unfixed (see `AssociatedTypes`).
let internal ownInstanceErrors (env: TypeEnv) (withUse: TypeEnv) (state: InferState) (value: Expression) : Diagnostic list =
    let loaded = withUse.Instances |> List.filter (fun instance -> not (env.Instances |> List.contains instance))
    let unfixed =
        loaded
        |> List.choose (fun instance ->
            match resolveSoFar state instance.Context with
            | TyApply(ctxName, (forType :: _ as argTys)) ->
                match unfixedAssociated env state true ctxName argTys with
                | [] -> None
                | names -> Some (unfixedError state true ctxName argTys (attachedTypeNames forType |> List.tryHead |> Option.defaultValue "T") names value.Span)
            | _ -> None)
    let replaced =
        loaded
        |> List.choose (fun instance ->
            match resolveSoFar state instance.Context with
            | TyApply(ctxName, (forType :: _ as argTys)) when hasOwnInstance env state value.Id value.Span ctxName argTys ->
                let message =
                    $"'{WellKnownContexts.display forType}' already has {ownInstanceText state ctxName argTys}, which a `use` can't replace: {ownInstanceAdvice ctxName}"
                Some (Diagnostics.at DiagnosticCodes.OwnInstanceReplaced value.Span message)
            | _ -> None)
    unfixed @ replaced |> List.distinctBy _.Message

/// The instance of the well-known context `wellKnown` the operator `op` at `span` reads on values of a type, other than
/// the compiler's own (see `WellKnownContexts.InstanceOf`): one in scope, or else one built from the member attached to
/// a type that is not a built-in one (see `WellKnownContexts.isBuiltIn`).
let internal wellKnownInstance (wellKnown: WellKnownContexts.WellKnownContext) (op: string) (span: SourceSpan) : WellKnownContexts.InstanceOf = fun env state ty ->
    match instanceInScope env state wellKnown.Context [ ty ] with
    | Some inScope -> Ok (Some (InstanceInScope inScope), state)
    | None when not (WellKnownContexts.isBuiltIn ty) && (tryResolveAttachedFunction env ty wellKnown.Member).IsSome ->
        synthesizeInstance env state op wellKnown.Context [ ty ] span
        |> Result.map (fun (instance, next) -> Some instance, next)
    | None -> Ok (None, state)

/// The instance of `Ord` the ordering operator `op` at `span` reads (see `wellKnownInstance`): for a tuple or a list
/// with no variables left in it, a part of which has a `compare` of its own, the one derived from its parts (see
/// `derivedEquality`), so that `<` on a `(Version, int)` orders by `Version.compare` (#2748).
let internal orderingInstance (op: string) (span: SourceSpan) : WellKnownContexts.InstanceOf = fun env state ty ->
    match wellKnownInstance WellKnownContexts.ordering op span env state ty with
    | Ok (None, _) when (TypeEnv.freeTyVars ty).IsEmpty ->
        match derivedEquality env state Set.empty (fun _ -> None) WellKnownContexts.ordering WellKnownContexts.BuiltIn op (NodeId.next ()) span ty with
        | Some (Ok (instance, next)) -> Ok (Some instance, next)
        | Some (Error errors) -> Error errors
        | None -> Ok (None, state)
    | found -> found

/// `state` without the calls left to find their instances later (see `PendingContextInstance`) that were left between
/// `before` and `firstPass`, when an expression inferred again since has left the same ones again: a pipe's value, which
/// is inferred once on its own and again as the call's first argument.
let internal withoutRepeatedPending (before: PendingContextInstance list) (firstPass: PendingContextInstance list) (state: InferState) =
    let added (earlier: PendingContextInstance list) (later: PendingContextInstance list) = List.truncate (later.Length - earlier.Length) later
    let first = added before firstPass
    let again = added firstPass state.PendingContextInstances |> List.map (fun p -> p.Call, p.Index) |> Set.ofList
    let repeated = first |> List.filter (fun p -> again.Contains (p.Call, p.Index))
    if repeated.IsEmpty then state
    else { state with PendingContextInstances = state.PendingContextInstances |> List.filter (fun p -> not (repeated |> List.exists (fun r -> obj.ReferenceEquals(r, p)))) }

/// `resolveInstancesAt` for `call`, a call to `name` that inference has just typed.
let internal resolveCallInstances (env: TypeEnv) (state: InferState) (call: Expression) (name: Identifier) (calleeSpan: SourceSpan) : Result<InferState, Diagnostic list> =
    resolveInstancesAt false env state call name (state.CalleeTypes |> Map.tryFind call.Id) calleeSpan

/// `resolveInstancesAt` for `value` when it names a function as a value, once something has settled its type, and
/// for each such function a record or tuple literal holds: `use Log(log = println)` gives `println` as a member at
/// `string -> ()`, with `ToString(string)`. The backend passes the function as a closure over the instances it finds here.
let rec internal resolveValueInstances (env: TypeEnv) (state: InferState) (value: Expression) : Result<InferState, Diagnostic list> =
    let resolve name = resolveInstancesAt true env state value name (state.ExprTypes |> Map.tryFind value.Id) value.Span
    let resolveEach (items: Expression list) =
        items |> List.fold (fun result item -> result |> Result.bind (fun current -> resolveValueInstances env current item)) (Ok state)
    match value.Kind with
    | IdentifierExpr name -> resolve name
    | MemberAccess(Kind (IdentifierExpr qualifier), field, _) ->
        match qualifiedDefinition env state qualifier field with
        | Some (name, _) -> resolve name
        | None -> Ok state
    | TupleExpr items -> resolveEach items
    | RecordExpr fields -> fields |> List.map (function NamedField(_, item) | PositionalField item -> item) |> resolveEach
    | _ -> Ok state

/// `resolveValueInstances` for each argument of `call`, once the call has settled its type: `apply(println, 7)` passes
/// `println` with `ToString(int)`, and `fold(xs, 0, math.max)` passes `math.max` with `Ord(int)`.
let internal resolveReferenceInstances (env: TypeEnv) (state: InferState) (call: Expression) : Result<InferState, Diagnostic list> =
    match call.Kind with
    | FunctionCall(_, _, args) ->
        match args with
        | [ Kind (TupleExpr items) ] -> items
        | _ -> args
        |> List.fold (fun result arg -> result |> Result.bind (fun current -> resolveValueInstances env current arg)) (Ok state)
    | _ -> Ok state

/// The lambda that `value`, the value of a local def that writes no type, is read as when it names a function that
/// requires a parameterised context: `def f = println` is `def f = { __p0 -> println(__p0) }`, a function whose calls
/// find the context, so the def requires it at its own type, as `ContextInference.resolveLocal` makes it. The lambda
/// keeps `value`'s node. Named as it is, the function would have nothing to find the context at.
let internal pointFreeAsLambda (env: TypeEnv) (state: InferState) (value: Expression) : Expression option =
    match value.Kind with
    | IdentifierExpr name when contextRequirementsOf env state name |> Option.exists (List.exists (isParameterizedContext state)) ->
        let arity =
            match TypeEnv.tryFind name env |> Option.map (_.Type >> Unifier.unalias) with
            | Some (TyFunc(TyPrimitive "unit", _, _)) -> 0
            | Some (TyFunc(TyRecord(fields, _), _, _)) when not fields.IsEmpty && Set.ofSeq fields.Keys = Set.ofList (List.init fields.Count string) -> fields.Count
            | Some (TyFunc _) -> 1
            | _ -> 0
        if arity = 0 then None
        else
            let parameters = List.init arity (fun index -> $"__p{index}")
            let call = Expression.from value (FunctionCall(name, Some value.Span, parameters |> List.map (IdentifierExpr >> Expression.from value)))
            Some { value with Kind = Lambda(parameters |> List.map (fun parameter -> parameter, None), call, false) }
    | _ -> None

/// The lambda that `value` is read as when it names, as a value, a local function that requires a parameterised
/// context, as `pointFreeAsLambda` reads it: `xs \list.map(f)` passes `{ __p0 -> f(__p0) }`, whose call finds the
/// instance at the type `list.map` settles. The local function's closure takes the context's members, which nothing
/// would pass it named on its own.
let internal localFunctionAsLambda (env: TypeEnv) (state: InferState) (value: Expression) : Expression option =
    match value.Kind with
    | IdentifierExpr name ->
        match TypeEnv.tryFind name env |> Option.bind _.Symbol |> Option.bind (fun symbol -> state.Symbols |> Map.tryFind symbol) with
        | Some symbol when symbol.Kind = LocalValue -> pointFreeAsLambda env state value
        | _ -> None
    | _ -> None

/// `ContextInference.resolvePending` for the def `name`, building an instance from the definitions attached to the
/// type it is for, or from the type's parts, as a call does. A hole finds its instance as
/// `InterpolationHoles.resolvePending` says.
let internal resolvePendingInstances (env: TypeEnv) (state: InferState) (name: Identifier) (symbol: SymbolId option) (scheme: TypeScheme) : Result<TypeExpr option * InferState, Diagnostic list> =
    // A function named as a value at a type still open is found no instance, as before anything settled it: it is
    // never called at that type, as `dbg(twice)` isn't (see `CoreIrLowerInstances.neverCalled` in the backend).
    let neverCalled (pending: PendingContextInstance) =
        state.PendingReferences.Contains pending.Call
        && choosingArguments state pending.Context pending.ArgTys |> List.exists (resolveSoFar state >> TypeEnv.freeTyVars >> Set.isEmpty >> not)
    let state =
        { state with PendingContextInstances = state.PendingContextInstances |> List.filter (fun pending -> pending.Def <> name || not (neverCalled pending)) }
    let arithmetic, state = ArithmeticInference.require state name scheme
    let operators (isPending: PendingContextInstance -> bool) = state.PendingContextInstances |> List.filter (fun pending -> pending.Def = name && isPending pending)
    let comparisons, orderings = operators EqualityOperators.isPending, operators OrderingOperators.isPending
    let defScheme def = TypeEnv.tryFind def env
    // A call that requires a context of a structure with the def's variables in it, whose instance is derived from its
    // parts, makes the def require the context of each.
    let derivedVars (pending: PendingContextInstance) (argTys: Ty list) =
        derivableType env pending.Context argTys
        |> Option.bind (StructuralInstances.variables scheme.Quantified)
        |> Option.defaultValue []
    ContextInference.resolvePending state name symbol scheme derivedVars (fun ownInstance pending argTys current ->
        let scopeEnv, annotatedDef = current.HoleScopes |> Map.tryFind pending.Call |> Option.defaultValue (env, None)
        let declaredVars = annotatedDef |> Option.bind defScheme |> Option.map _.Quantified |> Option.defaultValue Set.empty
        let rigid = Set.union scheme.Quantified declaredVars
        let hasInstanceIn env ty = (instanceInScope env current pending.Context [ ty ]).IsSome
        // The instance of each variable of a structure: the one in scope where the call is, or the one the def around
        // it was just found to require. A variable of neither is one a local function is generic in (or one nothing
        // settles, as in `[]`): it is written with the compiler's own `ToString` at each type the function is
        // specialised at, as a hole of the variable itself is (see `InterpolationHoles.resolvePending`).
        let instanceOf (var: TyVar) =
            match instanceInScope scopeEnv current pending.Context [ TyVar var ] |> Option.orElseWith (fun () -> ownInstance pending.Context var |> Option.map (fun written -> written, FromInference)) with
            | Some inScope -> Some (InstanceInScope inScope)
            | None when not (rigid.Contains var.Id)
                         && pending.Context = InterpolationHoles.context
                         && (TypeEnv.tryFind InterpolationHoles.builtInFunction env).IsSome ->
                Some (InstanceSynthesized InterpolationHoles.builtInMembers)
            | None -> None
        let derived (env: TypeEnv) =
            derivedInstance env current rigid instanceOf annotatedDef pending.Callee pending.Call pending.Span pending.Context argTys
        let attached env =
            if canSynthesize pending.Context argTys then
                Some (synthesizeInstance env current pending.Callee pending.Context argTys pending.Span)
            else None
        let synthesize env =
            derived env |> Option.orElseWith (fun () -> attached env)
        if InterpolationHoles.isHole pending.Callee then
            Some (InterpolationHoles.resolvePending (fun env -> instanceInScope env current) synthesize defScheme current pending argTys)
        elif EqualityOperators.isPending pending then
            let derive (ty: Ty) =
                derivedComparison scopeEnv rigid instanceOf pending.Call pending.Span ty current
            Some (EqualityOperators.resolvePending hasInstanceIn derive defScheme current pending argTys)
        elif OrderingOperators.isPending pending then
            Some (OrderingOperators.resolvePending (orderingInstance pending.Callee pending.Span) hasInstanceIn defScheme current pending argTys)
        // A value that turned out never to give one has every context, as at a call (see `resolveCallInstances`).
        elif argTys |> List.exists Unifier.isBottom then
            if pending.Context = InterpolationHoles.context && (TypeEnv.tryFind InterpolationHoles.builtInFunction env).IsSome then
                Some (Ok (Some (InstanceSynthesized InterpolationHoles.builtInMembers), current))
            else Some (Ok (None, current))
        else
            // The def now requires a well-known context of each of its variables the call compares values of. A call left
            // to the end of the module needs an instance for each variable of the type the def around it writes, but
            // for a rest of a union in it, which a call that only checks its context leaves to each call to that def
            // (see `ContextInference.checkRestsAtCalls`).
            let declaredRests =
                if pending.ChecksOnly then
                    annotatedDef |> Option.bind defScheme |> Option.map (_.Type >> ContextInference.unionRests >> List.map _.Id >> Set.ofList)
                else None
                |> Option.defaultValue Set.empty
            let hasInstance (ty: Ty) =
                match ty with
                | TyVar v when declaredVars.Contains v.Id -> declaredRests.Contains v.Id || hasInstanceIn scopeEnv ty
                | TyVar _ -> true
                | _ -> hasInstanceIn scopeEnv ty
            match wellKnownInstanceAtCall env pending.Context argTys hasInstance with
            | Some (_, WellKnownContexts.Open _) -> Some (Error pending.Errors)
            | Some (wellKnown, WellKnownContexts.NoInstance reason) ->
                Some (Error (WellKnownContexts.error wellKnown.Context (List.head argTys) pending.Span (WellKnownContexts.noInstanceAtCall wellKnown pending.Callee (List.head argTys) reason)))
            | Some (wellKnown, WellKnownContexts.BuiltIn) ->
                match derivedEquality scopeEnv current rigid instanceOf wellKnown WellKnownContexts.BuiltIn pending.Callee pending.Call pending.Span (List.head argTys) with
                | Some derived -> Some (derived |> Result.map (fun (instance, next) -> Some instance, next))
                | None -> Some (Ok (Some (InstanceSynthesized (WellKnownContexts.builtInMembers wellKnown)), current))
            | _ when InterpolationHoles.passesBuiltIn env pending.Context argTys ->
                Some (Ok (Some (InstanceSynthesized InterpolationHoles.builtInMembers), current))
            | _ when TypeValues.passesBuiltIn env pending.Context argTys ->
                Some (Ok (Some (InstanceSynthesized TypeValues.builtInMembers), current))
            | _ ->
                // An instance in scope where the call is, now that its type is known, as one written there is found
                // when the call's arguments settle it.
                match instanceInScope scopeEnv current pending.Context argTys with
                | Some inScope when argTys |> List.forall (TypeEnv.freeTyVars >> Set.isEmpty) -> Some (Ok (Some (InstanceInScope inScope), current))
                | _ -> synthesize scopeEnv |> Option.map (Result.map (fun (instance, next) -> Some instance, next)))
    |> Result.map (fun (annotation, resolved) ->
        let inputOf ty = inputTypeFromArgs [ ty; ty ]
        annotation,
        resolved
        |> EqualityOperators.callMembers inputOf comparisons
        |> OrderingOperators.callHelpers inputOf orderings
        |> ArithmeticInference.callMembers inputOf arithmetic)

/// Checks `call`, a call to `name` or `name` named as a value, which inference has just typed `calleeTy`, when `name`
/// needs well-known contexts of variables of its type that it is passed nothing for: a local function's (see
/// `InferState.LocalComparisons`), and the rests of the unions a function compares (see `InferState.CheckedRests`).
/// Each needs that context of the type it is at `call` (see `WellKnownContexts.checkLocalCall`). A type not known yet,
/// as that of a function passed as an argument often is, is checked once the def around it is inferred.
let internal checkComparisonsAt (env: TypeEnv) (state: InferState) (call: Expression) (name: Identifier) (calleeSpan: SourceSpan) (calleeTy: Ty) : Result<InferState, Diagnostic list> =
    match TypeEnv.tryFind name env with
    | Some ({ Symbol = Some symbol } as scheme) when state.LocalComparisons.ContainsKey symbol || state.CheckedRests.ContainsKey symbol ->
        ContextInference.checkedAtCalls state symbol scheme
        |> Map.toList
        |> List.fold (fun result (ctxName, used) ->
            varsIn (fun v -> if used.Contains v.Id then Some v.Id else None) scheme.Type calleeTy
            |> Map.values
            |> Seq.fold (fun result ty ->
                result
                |> Result.bind (fun (current: InferState) ->
                    // A number literal nothing has settled yet is of its default type here (see `NumericLiterals`).
                    let current = NumericLiterals.settleWithin [ ty ] current
                    let ty = resolveSoFar current ty
                    let hasInstance ty = (instanceInScope env current ctxName [ ty ]).IsSome
                    match wellKnownInstanceAtCall env ctxName [ ty ] hasInstance with
                    | Some (wellKnown, _) when wellKnown.Context = EqualityOperators.context && StructuralInstances.holdsCollection ty ->
                        Error (WellKnownContexts.error wellKnown.Context ty calleeSpan (EqualityOperators.collectionPassedNothing name ty))
                    | Some (wellKnown, instance) -> WellKnownContexts.checkLocalCall wellKnown env current call name calleeSpan ty instance
                    | None -> Ok current)) result) (Ok state)
    | _ -> Ok state

/// `state` with what provides each parameterised context that a call of the module finds an instance of in scope
/// recorded for the call, before the contexts found by their members' names (see `TypedModule.ContextProviders`): the
/// context as it is written where it is put in scope, as a type over variables of its own that keep its names.
let internal recordInstanceProviders (state: InferState) : InferState =
    state.ContextInstances
    |> Map.fold (fun (current: InferState) call instances ->
        let provided, next =
            instances
            |> List.choose (function
                | Some (InstanceInScope({ Kind = TypeApply(ctxName, args) }, provider)) -> Some (ctxName, args, provider)
                | _ -> None)
            |> List.mapFold (fun (reading: InferState) (ctxName, args, provider) ->
                let argTys, read = args |> List.mapFold typeExprToTy { reading with TypeVars = Map.empty }
                { Context = TyApply(ctxName, argTys); Provider = provider }, { reading with NextId = read.NextId }) current
        if provided.IsEmpty then current
        else
            let recorded = next.ContextProviders |> Map.tryFind call |> Option.defaultValue []
            { next with ContextProviders = next.ContextProviders |> Map.add call (provided @ recorded) }) state

/// Builds, before the def `name` is generalised, the instances of contexts of more than one parameter that its calls
/// left to be found and that are now known to be for a type with a name, from that type's attached definitions, as a
/// call does. Building one can settle the context's other arguments, as a `FromString(int)` built from
/// `int.fromString` makes its `failure` the `#invalidInt` it gives back, and the def's type must have them settled, not be
/// generic in them. The others are left to `resolvePendingInstances`.
let internal synthesizeSettledInstances (env: TypeEnv) (state: InferState) (name: Identifier) : Result<InferState, Diagnostic list> =
    let settled, others =
        state.PendingContextInstances
        |> List.partition (fun pending ->
            pending.Def = name
            && pending.ArgTys.Length > 1
            && canSynthesize pending.Context (pending.ArgTys |> List.map (resolveSoFar state)))
    settled
    |> List.rev
    |> List.fold (fun result pending ->
        result
        |> Result.bind (fun (current: InferState) ->
            // Only the type the instance is for is resolved. The others are constrained as the call has them: resolved,
            // one may be a union rest the unifier made up, which is numbered afresh each time the constraints are solved.
            let argTys = resolveSoFar current pending.ArgTys.Head :: pending.ArgTys.Tail
            synthesizeInstance env current pending.Callee pending.Context argTys pending.Span
            |> Result.map (fun (instance, next) ->
                { next with ContextInstances = ContextInference.withInstance pending instance next.ContextInstances })))
        (Ok { state with PendingContextInstances = others })
