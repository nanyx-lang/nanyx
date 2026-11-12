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

/// What the names of the instances in scope start with, in the environment. No name written in the source can, so
/// an instance never shadows a name, or is shadowed by one.
let private instancePrefix = "@instance "

/// Whether `name` is the name of a context.
let private isContextName (state: InferState) (name: Identifier) =
    state.ContextDefs.ContainsKey name || (state.TypeDefs |> Map.tryFind name |> Option.exists _.IsContext)

/// Whether `ctx` names a parameterised context: `Sum(a)`, `Show(int)`.
let internal isParameterizedContext (state: InferState) (ctx: TypeExpr) =
    match ctx.Kind with
    | TypeApply(name, _ :: _) -> isContextName state name
    | _ -> false

/// `env` with the instance of `ctxName` applied to `argTys` in scope, as `written`: bound under a name of its own, with
/// the context applied to its arguments as its type (`Sum(Point)` is `TyApply("Sum", [Point])`). Of two instances of
/// one context, the one put in scope later is nearer.
let private bindInstanceOf (env: TypeEnv) (state: InferState) (ctxName: Identifier) (argTys: Ty list) (written: TypeExpr) : TypeEnv * InferState =
    let bindingName = $"{instancePrefix}{ctxName} {state.NextId}"
    let state = { state with NextId = state.NextId + 1; InstanceContexts = state.InstanceContexts |> Map.add bindingName written }
    TypeEnv.extend bindingName (TypeEnv.mono (TyApply(ctxName, argTys))) env, state

/// `env` with the instance `ctx` in scope, if it is one of a parameterised context (see `bindInstanceOf`).
let private bindInstance (env: TypeEnv) (state: InferState) (ctx: TypeExpr) : TypeEnv * InferState =
    match ctx.Kind with
    | TypeApply(ctxName, args) when isParameterizedContext state ctx ->
        let argTys, state = args |> List.mapFold typeExprToTy state
        bindInstanceOf env state ctxName argTys ctx
    | _ -> env, state

/// `env` with each instance of a parameterised context among `contexts` in scope (see `bindInstance`).
let internal bindInstances (env: TypeEnv) (state: InferState) (contexts: TypeExpr list) : TypeEnv * InferState =
    contexts |> List.fold (fun (env, state) ctx -> bindInstance env state ctx) (env, state)

/// `ty` with each type variable named in `names` replaced by the type it maps to.
let rec private replaceNamedVars (names: Map<string, Ty>) (ty: Ty) : Ty =
    let recur = replaceNamedVars names
    match ty with
    | TyVar { Name = Some name } when names.ContainsKey name -> names.[name]
    | TyVar _
    | TyPrimitive _ -> ty
    | TyNominal(nominal, underlying, isPrivate) -> TyNominal(nominal, recur underlying, isPrivate)
    | TyAlias(name, args, underlying) -> TyAlias(name, List.map recur args, recur underlying)
    | TyFunc(arg, ret, contexts) -> TyFunc(recur arg, recur ret, contexts |> List.map recur |> FunctionContexts.flatten)
    | TyRecord fields -> TyRecord(fields |> Map.map (fun _ fieldTy -> recur fieldTy))
    | TyOpenRecord(fields, row) -> TyOpenRecord(fields |> Map.map (fun _ fieldTy -> recur fieldTy), row)
    | TyTag(name, payload) -> TyTag(name, Option.map recur payload)
    | TyApply(name, args) -> TyApply(name, List.map recur args)
    | TyUnion items -> TyUnion(List.map recur items)

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
    | TyAlias(_, gs, gu), TyAlias(_, ss, su) when gs.Length = ss.Length -> all ((gu, su) :: List.zip gs ss)
    | TyNominal(_, gu, _), TyNominal(_, su, _) -> recur gu su
    | TyRecord gf, TyRecord sf -> all (fieldPairs gf sf)
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
        && Unifier.unify [ a, b, Equal, SourceSpan.none ] |> Result.isOk)

/// The members of the context `ctxName` with its parameters bound to `argTys`, each with its type.
let private contextMemberTypes (state: InferState) (ctxName: Identifier) (argTys: Ty list) : (Identifier * Ty) list option =
    match state.TypeDefs |> Map.tryFind ctxName with
    | Some info when info.Parameters.Length = argTys.Length ->
        match Unifier.unalias info.Underlying with
        | TyRecord fields -> Some (fields |> Map.toList |> List.map (fun (name, ty) -> name, Unifier.applyToParameters info.Parameters argTys ty))
        | _ -> None
    | _ -> None

/// The instance of `ctxName` applied to `argTys` nearest in scope in `env`, as written where it is put in scope: one of
/// `ctxName` itself, else one of a context that has all its members (see `ContextShapes`).
let private instanceInScope (env: TypeEnv) (state: InferState) (ctxName: Identifier) (argTys: Ty list) : TypeExpr option =
    let ofContext (ctxName: Identifier) =
        let prefix = $"{instancePrefix}{ctxName} "
        env
        |> Map.toSeq
        |> Seq.filter (fun (name, _) -> name.StartsWith prefix)
        |> Seq.choose (fun (name, scheme) ->
            match resolveSoFar state scheme.Type with
            | TyApply(_, instanceArgs) when instanceArgs.Length = argTys.Length && List.forall2 sameType instanceArgs argTys ->
                Some (int (name.Substring prefix.Length), name)
            | _ -> None)
        |> Seq.sortByDescending fst
        |> Seq.tryHead
        |> Option.bind (fun (_, name) -> state.InstanceContexts |> Map.tryFind name)
    ofContext ctxName
    |> Option.orElse (ContextShapes.wider state (fun other -> contextMemberTypes state other argTys) ctxName |> List.tryPick ofContext)

/// The member `memberName` of the instance nearest in scope in `env` of a context for the type variable `operandTy`
/// (its first argument) that has that member, as written where it is put in scope, with the member's type there. An
/// operator on values of a type variable calls it (see `OperatorCalls.definition`).
let internal memberOfInstanceInScope (env: TypeEnv) (state: InferState) (memberName: Identifier) (operandTy: Ty) : (TypeExpr * TypeScheme) option =
    match resolveSoFar state operandTy with
    | TyVar operand ->
        env
        |> Map.toSeq
        |> Seq.filter (fun (name, _) -> name.StartsWith instancePrefix)
        |> Seq.choose (fun (name, scheme) ->
            match resolveSoFar state scheme.Type with
            | TyApply(ctxName, (TyVar forVar :: _ as argTys)) when forVar.Id = operand.Id ->
                let prefix = $"{instancePrefix}{ctxName} "
                contextMemberTypes state ctxName argTys
                |> Option.bind (List.tryFind (fst >> (=) memberName))
                |> Option.map (fun (_, memberTy) ->
                    let symbol = state.TypeDefs |> Map.tryFind ctxName |> Option.bind (fun info -> info.Members |> Map.tryFind memberName)
                    int (name.Substring prefix.Length), name, { TypeEnv.mono memberTy with Symbol = symbol })
            | _ -> None)
        |> Seq.sortByDescending (fun (order, _, _) -> order)
        |> Seq.tryHead
        |> Option.bind (fun (_, name, scheme) -> state.InstanceContexts |> Map.tryFind name |> Option.map (fun written -> written, scheme))
    | _ -> None

/// Whether an instance of a context applied to `argTys` can be built from attached definitions: whether the type it
/// is for, its first argument, has a name definitions can be attached to (not a variable, or a record written out).
/// The others may still be open: the definitions' types settle them, as `int.fromString` makes a
/// `FromString(int, e)` a `FromString(int, #invalidInt)`.
let private canSynthesize (argTys: Ty list) =
    match argTys with
    | forType :: _ -> not (List.isEmpty (attachedTypeNames forType))
    | [] -> false

/// The instance of `ctxName` applied to `argTys` built from the definitions attached to the type it is for, its first
/// argument, as `Point.0` and `Point.+` make a `Sum(Point)`: each member's name and the definition it is. The
/// definitions' types must be the members' types, which settles any other argument still open. `calleeName` requires
/// the context at `span`.
let private synthesizeInstance (env: TypeEnv) (state: InferState) (calleeName: Identifier) (ctxName: Identifier) (argTys: Ty list) (span: SourceSpan) : Result<(string * string) list * InferState, Diagnostic list> =
    let ctxText = $"""{ctxName}({argTys |> List.map Unifier.tyToString |> String.concat ", "})"""
    let requirer = InterpolationHoles.requirer calleeName argTys
    let fail message = Error [ Diagnostics.at DiagnosticCodes.MissingContextMembers span message ]
    match argTys, contextMemberTypes state ctxName argTys with
    | forType :: _, Some members ->
        // The name a definition for the type is attached under: `Point`, `$Money`, `int`.
        let attachTo = attachedTypeNames forType |> List.tryHead |> Option.defaultValue (Unifier.tyToString forType)
        let found = members |> List.map (fun (memberName, memberTy) -> memberName, memberTy, tryResolveAttachedFunction env forType memberName)
        match found |> List.filter (fun (_, _, definition) -> definition.IsNone) |> List.map (fun (memberName, _, _) -> $"'{attachTo}.{memberName}'") with
        | [] ->
            found
            |> List.fold (fun result (memberName, memberTy, definition) ->
                result
                |> Result.bind (fun (bound, current: InferState) ->
                    let definition = definition.Value
                    let definitionTy, next = instantiate current (TypeEnv.tryFind definition env).Value
                    match Unifier.unify ((memberTy, definitionTy, Equal, span) :: next.Constraints) with
                    | Ok _ -> Ok (bound @ [ memberName, definition ], addConstraintAt span memberTy definitionTy next)
                    // The constraints so far fail without this one: that is an error reported where it is, not here.
                    | Error _ when Result.isError (Unifier.unify next.Constraints) ->
                        Ok (bound @ [ memberName, definition ], addConstraintAt span memberTy definitionTy next)
                    | Error _ ->
                        fail $"{requirer} requires the context '{ctxText}', but '{definition}' has the type '{Unifier.tyToString (resolveSoFar next definitionTy)}', where '{memberName}' must be '{Unifier.tyToString (resolveSoFar next memberTy)}'")) (Ok ([], state))
        | _ when InterpolationHoles.isHole calleeName -> fail (InterpolationHoles.noInstance forType)
        | missing ->
            let notDefined = if missing.Length = 1 then "is not defined: define it" else "are not defined: define them"
            fail $"{requirer} requires the context '{ctxText}', which is not loaded here, and {Diagnostics.listed missing} {notDefined}, or load an instance with `use`"
    | _ -> fail $"{requirer} requires the context '{ctxText}', which is not loaded here: load an instance with `use`"

/// The type of each of `args`, the arguments of a parameterised context a function requires, at a call where the
/// function has the type `calleeTy`. `signature` is the type the function declares, which `args` name its type
/// variables from; it is read afresh, and is the callee's type at the call. Without one, the type variables are
/// matched by name against `generalTy`, the callee's type before it was instantiated. Each is given resolved as far as
/// the constraints so far tell, and as read: a type resolved now may have a union rest the unifier made up, which is
/// numbered afresh each time the constraints are solved, so one to constrain later is the type as read.
let private contextArgTysAtCall (state: InferState) (signature: TypeExpr option) (generalTy: Ty) (calleeTy: Ty) (args: TypeExpr list) : Ty list * Ty list * InferState =
    // Read with no variables in scope, so each variable is a fresh one, shared by the signature and the arguments.
    let mutable current = { state with TypeVars = Map.empty }
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
            // `ContextVariables.ofNamedValue`).
            let withoutOwnContexts =
                match Unifier.unalias calleeTy with
                | TyFunc(input, output, _) -> TyFunc(input, output, [])
                | other -> other
            current <- addConstraint signatureTy withoutOwnContexts current
            argTys
        | None ->
            let names = namedVarsIn generalTy calleeTy
            args |> List.map (read >> replaceNamedVars names)
    let current = { current with TypeVars = state.TypeVars }
    argTys |> List.map (resolveSoFar current), argTys, current

/// Each parameterised context a value of the type `valueTy` is an instance of, applied to the types it is one for: a
/// record with exactly the context's members, at types that settle each of its parameters. `use (`+` = ...)`, of a
/// record whose type does not name a context, finds that it is an `Add(int)` by the record's shape.
let private contextsOfValue (state: InferState) (valueTy: Ty) : TypeExpr list =
    match Unifier.unalias (resolveSoFar state valueTy) with
    | TyRecord fields ->
        [ for KeyValue(ctxName, info) in state.TypeDefs do
            if info.IsContext && not info.Parameters.IsEmpty then
                let paramTys, scratch = info.Parameters |> List.mapFold (fun current _ -> freshVar current) state
                match contextMemberTypes scratch ctxName paramTys with
                | Some members when Set.ofList (List.map fst members) = Set.ofSeq fields.Keys ->
                    let scratch = members |> List.fold (fun current (name, memberTy) -> addConstraint memberTy fields.[name] current) scratch
                    if Unifier.unify scratch.Constraints |> Result.isOk then
                        let argTys = paramTys |> List.map (resolveSoFar scratch)
                        if argTys |> List.forall (TypeEnv.freeTyVars >> Set.isEmpty) then
                            match argTys |> List.map (ContextInference.typeExprOf Map.empty) with
                            | args when args |> List.forall Option.isSome ->
                                yield TypeExpr.synthetic (TypeApply(ctxName, args |> List.map Option.get))
                            | _ -> ()
                | _ -> () ]
    | _ -> []

/// `env` with the instance a `use` of the value of type `valueTy` loads in scope, when it is one of a parameterised
/// context: a value whose type is the context applied to types, as `def addInt: Sum(int) = (...)` makes `addInt`
/// (recorded by the node of `value`, see `TypedModule.UseContexts`), or else a record of a context's shape (see
/// `contextsOfValue`).
let internal useInstance (env: TypeEnv) (state: InferState) (value: Expression) (valueTy: Ty) : TypeEnv * InferState =
    match resolveSoFar state valueTy with
    | TyAlias(ctxName, (_ :: _ as argTys), _)
    | TyApply(ctxName, (_ :: _ as argTys)) when isContextName state ctxName ->
        let context = TyApply(ctxName, argTys)
        match ContextInference.typeExprOf (ContextInference.varNames context) context with
        | Some written ->
            let env, state = bindInstanceOf env state ctxName argTys written
            env, { state with UseContexts = state.UseContexts |> Map.add value.Id written }
        | None -> env, state
    | _ -> bindInstances env state (contextsOfValue state valueTy)

/// Where a call that requires `ctxName` at `argTys` finds it when no instance is in scope, if the context is a
/// well-known one (see `WellKnownContexts.atCall`). `hasInstance` says whether an instance of a type variable is in scope.
/// A type that holds one that attaches `==` and no `hash` has no `Hash` (see `WellKnownContexts.disagreeing`).
let private wellKnownInstanceAtCall (env: TypeEnv) (ctxName: Identifier) (argTys: Ty list) (hasInstance: Ty -> bool) =
    match argTys, WellKnownContexts.tryFind ctxName with
    // Without the prelude there is no instance of the compiler's own to pass.
    | [ ty ], Some wellKnown when (TypeEnv.tryFind wellKnown.BuiltInFunction env).IsSome ->
        let attaches (part: Ty) (memberName: Identifier) =
            not (WellKnownContexts.isBuiltIn part) && (tryResolveAttachedFunction env part memberName).IsSome
        match WellKnownContexts.disagreeing wellKnown attaches ty with
        | Some reason -> Some (wellKnown, WellKnownContexts.NoInstance reason)
        | None -> Some (wellKnown, WellKnownContexts.atCall wellKnown (attaches ty wellKnown.Member) hasInstance ty)
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
let internal partInstanceIn (env: TypeEnv) (state: InferState) (ctxName: Identifier) (ty: Ty) : TypeExpr option =
    instanceInScope env state ctxName [ ty ]

/// Whether comparing two values of the structure `ty` compares a part with an `==` of its own (see
/// `StructuralInstances.comparesParts`): one attached to the part's type, an `Eq` of it in scope that `partInstance`
/// finds, or the `Eq` of a type variable that `hasInstance` says is in scope.
let internal comparesOwnParts (env: TypeEnv) (rigid: Set<int>) (partInstance: Ty -> TypeExpr option) (hasInstance: TyVar -> bool) (ty: Ty) : bool =
    ty
    |> StructuralInstances.comparesParts rigid (fun part ->
        match part with
        | TyVar var -> hasInstance var
        | _ -> (tryResolveAttachedFunction env part EqualityOperators.equals).IsSome || (partInstance part).IsSome)

/// `state` with the comparison `call`, at `span`, of two values of the type `ty` calling the `==` the type has of its
/// own, if it has one: the `==` attached to it, or, for a structure that compares a part with an `==` of its own,
/// the one derived for it (see `StructuralInstances.comparison`, which `rigid` and `instanceOf` are for). Inside the
/// attached `==` itself the comparison stays the compiler's own, which is how it compares the values underneath.
let internal derivedComparison (env: TypeEnv) (rigid: Set<int>) (instanceOf: TyVar -> ContextInstance option) (call: NodeId) (span: SourceSpan) (ty: Ty) (state: InferState) : InferState option =
    let calling (callee: Identifier, next: InferState) =
        { next with
            CalleeTypes = next.CalleeTypes |> Map.add call (TyFunc(inputTypeFromArgs [ ty; ty ], TyPrimitive "bool", []))
            OperatorCalls = next.OperatorCalls |> Map.add call callee }
    let partInstance = partInstanceIn env state EqualityOperators.context
    match tryResolveAttachedFunction env ty EqualityOperators.equals, state.ContextScope with
    | Some attached, DefScope(name, _) when name = attached -> None
    | Some attached, _ -> Some (calling (attached, state))
    | None, _ when comparesOwnParts env rigid partInstance (instanceOf >> Option.isSome) ty ->
        StructuralInstances.comparison rigid partInstance instanceOf call span ty state |> Option.map calling
    | None, _ -> None

/// The instance of `Eq` the call `call` to `calleeName`, at `span` in `env`, finds for the structure `ty` in place of
/// the compiler's own (`wellKnown` is `Eq`, found as `instance`), when `ty` compares a part with an `==` of its own, or
/// holds a map or a set: the derived definition, as the context's member. A variable of `ty` is compared with the
/// instance `instanceOf` finds for it, and `rigid` holds the variables of the function around the call (see
/// `StructuralInstances.instance`). A type with variables in it that holds a map or a set and has no derived `==` is an
/// error, since the compiler's own cannot compare it.
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
    let partInstance = partInstanceIn env state EqualityOperators.context
    if wellKnown.Context <> EqualityOperators.context || instance <> WellKnownContexts.BuiltIn then None
    else
        let derived =
            if comparesOwnParts env rigid partInstance (instanceOf >> Option.isSome) ty then
                StructuralInstances.instance rigid partInstance instanceOf false call span EqualityOperators.context ty state
                |> Option.bind Result.toOption
            else None
        match derived with
        | Some derived -> Some (Ok derived)
        | None when not (TypeEnv.freeTyVars ty).IsEmpty && StructuralInstances.holdsCollection ty ->
            Some (Error (WellKnownContexts.error span (EqualityOperators.collectionAtGenericCall calleeName ty)))
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
        let message =
            match failure with
            | StructuralInstances.NoInstanceOf missing when InterpolationHoles.isHole calleeName -> InterpolationHoles.undeclared (TyVar missing) annotatedDef
            | StructuralInstances.NoInstanceOf missing -> InterpolationHoles.undeclaredPart calleeName argTys.Head (TyVar missing) annotatedDef
            | StructuralInstances.HoldsFunction held -> InterpolationHoles.holdsFunction calleeName argTys held
            | StructuralInstances.OpenToCaller -> InterpolationHoles.openToCaller calleeName argTys.Head annotatedDef
        [ Diagnostics.at DiagnosticCodes.MissingContextMembers span message ]))

/// The instance of the well-known context `wellKnown` the operator `op` at `span` reads on values of a type, other than
/// the compiler's own (see `WellKnownContexts.InstanceOf`): one in scope, or else one built from the member attached to
/// a type that is not a built-in one (see `WellKnownContexts.isBuiltIn`).
let internal wellKnownInstance (wellKnown: WellKnownContexts.WellKnownContext) (op: string) (span: SourceSpan) : WellKnownContexts.InstanceOf = fun env state ty ->
    match instanceInScope env state wellKnown.Context [ ty ] with
    | Some written -> Ok (Some (InstanceInScope written), state)
    | None when not (WellKnownContexts.isBuiltIn ty) && (tryResolveAttachedFunction env ty wellKnown.Member).IsSome ->
        synthesizeInstance env state op wellKnown.Context [ ty ] span
        |> Result.map (fun (members, next) -> Some (InstanceSynthesized members), next)
    | None -> Ok (None, state)

/// The instance of `Ord` the ordering operator `op` at `span` reads (see `wellKnownInstance`).
let internal orderingInstance (op: string) (span: SourceSpan) = wellKnownInstance WellKnownContexts.ordering op span

/// Finds where `call`, the node where `name` is called at the type `calleeTy` (or, when `reference`, named as a value
/// of that type), finds each parameterised context the callee requires (see `TypedModule.ContextInstances`), once its
/// arguments have settled what the context is for. A context for a type inference has not worked out yet is left to
/// be found by its members' names, as one that is not parameterised is. A function named as a value whose type is not
/// settled there finds no instance, and is not an error (see `resolveReferenceInstances`).
let private resolveInstancesAt (reference: bool) (env: TypeEnv) (state: InferState) (call: Expression) (name: Identifier) (calleeTy: Ty option) (calleeSpan: SourceSpan) : Result<InferState, Diagnostic list> =
    let contexts = state.ContextRequirements |> Map.tryFind (canonicalName env state name) |> Option.defaultValue []
    match TypeEnv.tryFind name env, calleeTy with
    | Some scheme, Some calleeTy when contexts |> List.exists (isParameterizedContext state) ->
        contexts
        |> List.fold (fun result ctx ->
            result
            |> Result.bind (fun (found, current: InferState) ->
                match ctx.Kind with
                | TypeApply(ctxName, args) when isParameterizedContext current ctx ->
                    let signature = current.ContextSignatures |> Map.tryFind (canonicalName env current name)
                    let argTys, argTysAsRead, current = contextArgTysAtCall current signature scheme.Type calleeTy args
                    let settled = argTys |> List.forall (TypeEnv.freeTyVars >> Set.isEmpty)
                    // A well-known context of a type with no member attached is the compiler's own (see
                    // `WellKnownContexts.resolveCall`). `Eq` of a structure with a part that has an `==` of its own
                    // is derived from its parts.
                    // Until the def around the call is inferred, any variable of the type may turn out to stand for one
                    // its callers choose, so a union open to more tags is left to the compiler's own.
                    let rigid = argTys |> List.map TypeEnv.freeTyVars |> Set.unionMany
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
                    | Some written, _ -> Ok (found @ [ Some (InstanceInScope written) ], current)
                    | None, _ when reference && not settled -> Ok (found @ [ None ], current)
                    // A value that failed to check has every context, so that the call is not an error too. It has the
                    // compiler's own `ToString`, so the backend still has a member to pass. A hole of one is written by
                    // the backend, which reports the error where the hole is.
                    | None, _ when not (InterpolationHoles.isHole name) && argTys |> List.contains Unifier.errorType ->
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
                                | AmbientScope -> InterpolationHoles.moduleEnd, None
                            let errors =
                                ContextInference.varsInOrder argTys.Head
                                |> List.truncate 1
                                |> List.map (fun var -> Diagnostics.at DiagnosticCodes.MissingContextMembers calleeSpan (InterpolationHoles.undeclaredPart name argTys.Head (TyVar var) annotatedDef))
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
                    | None, None when canSynthesize argTys ->
                        synthesizeInstance env current name ctxName argTys calleeSpan
                        |> Result.map (fun (members, next) -> found @ [ Some (InstanceSynthesized members) ], next)
                    | None, None when InterpolationHoles.isHole name ->
                        InterpolationHoles.resolveLater env current call ctxName argTys calleeSpan found contexts.Length
                    | None, None ->
                        match ensureContextAvailable true env current name [ ctx ] calleeSpan, current.ContextScope with
                        | Ok next, _ -> Ok (found @ [ None ], next)
                        // In an unannotated def, a type the call leaves open may be settled by the rest of the body,
                        // or be one of the def's own variables, which the def then requires the context at.
                        | Error errors, DefScope(defName, true) when argTys |> List.exists (TypeEnv.freeTyVars >> Set.isEmpty >> not) ->
                            let pending =
                                { Def = defName; Call = call.Id; Callee = name; Index = found.Length; Count = contexts.Length
                                  Context = ctxName; ArgTys = argTysAsRead; Span = calleeSpan; Errors = errors; ChecksOnly = false }
                            Ok (found @ [ None ], { current with PendingContextInstances = pending :: current.PendingContextInstances })
                        | Error errors, _ -> Error errors
                | _ -> Ok (found @ [ None ], current))) (Ok ([], state))
        |> Result.map (fun (found, current) ->
            if found |> List.forall Option.isNone then current
            else { current with ContextInstances = current.ContextInstances |> Map.add call.Id found })
    | _ -> Ok state

/// `resolveInstancesAt` for `call`, a call to `name` that inference has just typed.
let internal resolveCallInstances (env: TypeEnv) (state: InferState) (call: Expression) (name: Identifier) (calleeSpan: SourceSpan) : Result<InferState, Diagnostic list> =
    resolveInstancesAt false env state call name (state.CalleeTypes |> Map.tryFind call.Id) calleeSpan

/// `resolveInstancesAt` for each argument of `call` that names a function as a value, once the call has settled its
/// type: `apply(println, 7)` passes `println` with `ToString(int)`. The backend passes the function as a closure over
/// the instances it finds here.
let internal resolveReferenceInstances (env: TypeEnv) (state: InferState) (call: Expression) : Result<InferState, Diagnostic list> =
    match call.Kind with
    | FunctionCall(_, _, args) ->
        match args with
        | [ Kind (TupleExpr items) ] -> items
        | _ -> args
        |> List.fold (fun result arg ->
            match arg.Kind with
            | IdentifierExpr name ->
                result |> Result.bind (fun current -> resolveInstancesAt true env current arg name (current.ExprTypes |> Map.tryFind arg.Id) arg.Span)
            | _ -> result) (Ok state)
    | _ -> Ok state

/// `ContextInference.resolvePending` for the def `name`, building an instance from the definitions attached to the
/// type it is for, or from the type's parts, as a call does. A hole finds its instance as
/// `InterpolationHoles.resolvePending` says.
let internal resolvePendingInstances (env: TypeEnv) (state: InferState) (name: Identifier) (symbol: SymbolId option) (scheme: TypeScheme) : Result<TypeExpr option * InferState, Diagnostic list> =
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
            match instanceInScope scopeEnv current pending.Context [ TyVar var ] |> Option.orElseWith (fun () -> ownInstance pending.Context var) with
            | Some written -> Some (InstanceInScope written)
            | None when not (rigid.Contains var.Id)
                         && pending.Context = InterpolationHoles.context
                         && (TypeEnv.tryFind InterpolationHoles.builtInFunction env).IsSome ->
                Some (InstanceSynthesized InterpolationHoles.builtInMembers)
            | None -> None
        let derived (env: TypeEnv) =
            derivedInstance env current rigid instanceOf annotatedDef pending.Callee pending.Call pending.Span pending.Context argTys
        let attached env =
            if canSynthesize argTys then
                Some (synthesizeInstance env current pending.Callee pending.Context argTys pending.Span)
            else None
        let synthesize env =
            derived env |> Option.orElseWith (fun () -> attached env |> Option.map (Result.map (fun (members, next) -> InstanceSynthesized members, next)))
        if InterpolationHoles.isHole pending.Callee then
            Some (InterpolationHoles.resolvePending (fun env -> instanceInScope env current) synthesize defScheme current pending argTys)
        elif EqualityOperators.isPending pending then
            let derive (ty: Ty) =
                derivedComparison scopeEnv rigid instanceOf pending.Call pending.Span ty current
            Some (EqualityOperators.resolvePending hasInstanceIn derive defScheme current pending argTys)
        elif OrderingOperators.isPending pending then
            Some (OrderingOperators.resolvePending (orderingInstance pending.Callee pending.Span) hasInstanceIn defScheme current pending argTys)
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
                | _ -> true
            match wellKnownInstanceAtCall env pending.Context argTys hasInstance with
            | Some (_, WellKnownContexts.Open _) -> Some (Error pending.Errors)
            | Some (wellKnown, WellKnownContexts.NoInstance reason) ->
                Some (Error (WellKnownContexts.error pending.Span (WellKnownContexts.noInstanceAtCall wellKnown pending.Callee (List.head argTys) reason)))
            | Some (wellKnown, WellKnownContexts.BuiltIn) ->
                match derivedEquality scopeEnv current rigid instanceOf wellKnown WellKnownContexts.BuiltIn pending.Callee pending.Call pending.Span (List.head argTys) with
                | Some derived -> Some (derived |> Result.map (fun (instance, next) -> Some instance, next))
                | None -> Some (Ok (Some (InstanceSynthesized (WellKnownContexts.builtInMembers wellKnown)), current))
            | _ when InterpolationHoles.passesBuiltIn env pending.Context argTys ->
                Some (Ok (Some (InstanceSynthesized InterpolationHoles.builtInMembers), current))
            | _ -> synthesize scopeEnv |> Option.map (Result.map (fun (instance, next) -> Some instance, next)))
    |> Result.map (fun (annotation, resolved) ->
        let inputOf ty = inputTypeFromArgs [ ty; ty ]
        annotation,
        resolved
        |> EqualityOperators.callMembers inputOf comparisons
        |> OrderingOperators.callHelpers inputOf orderings
        |> ArithmeticInference.callMembers inputOf arithmetic)

/// Checks `call`, a call to `name` that inference has just typed, when `name` needs well-known contexts of variables
/// of its type that it is passed nothing for: a local function's (see `InferState.LocalComparisons`), and the rests of
/// the unions a function compares (see `InferState.CheckedRests`). Each needs that context of the type it is at the
/// call (see `WellKnownContexts.checkLocalCall`).
let internal checkCalledComparisons (env: TypeEnv) (state: InferState) (call: Expression) (name: Identifier) (calleeSpan: SourceSpan) : Result<InferState, Diagnostic list> =
    match TypeEnv.tryFind name env, state.CalleeTypes |> Map.tryFind call.Id with
    | Some ({ Symbol = Some symbol } as scheme), Some calleeTy when state.LocalComparisons.ContainsKey symbol || state.CheckedRests.ContainsKey symbol ->
        ContextInference.checkedAtCalls state symbol scheme
        |> Map.toList
        |> List.fold (fun result (ctxName, used) ->
            varsIn (fun v -> if used.Contains v.Id then Some v.Id else None) scheme.Type calleeTy
            |> Map.values
            |> Seq.fold (fun result ty ->
                result
                |> Result.bind (fun (current: InferState) ->
                    let ty = resolveSoFar current ty
                    let hasInstance ty = (instanceInScope env current ctxName [ ty ]).IsSome
                    match wellKnownInstanceAtCall env ctxName [ ty ] hasInstance with
                    | Some (wellKnown, _) when wellKnown.Context = EqualityOperators.context && StructuralInstances.holdsCollection ty ->
                        Error (WellKnownContexts.error calleeSpan (EqualityOperators.collectionPassedNothing name ty))
                    | Some (wellKnown, instance) -> WellKnownContexts.checkLocalCall wellKnown env current call name calleeSpan ty instance
                    | None -> Ok current)) result) (Ok state)
    | _ -> Ok state

/// Builds, before the def `name` is generalised, the instances of contexts of more than one parameter that its calls
/// left to be found and that are now known to be for a type with a name, from that type's attached definitions, as a
/// call does. Building one can settle the context's other arguments, as a `FromString(int, e)` built from
/// `int.fromString` makes `e` the `#invalidInt` it gives back, and the def's type must have them settled, not be
/// generic in them. The others are left to `resolvePendingInstances`.
let internal synthesizeSettledInstances (env: TypeEnv) (state: InferState) (name: Identifier) : Result<InferState, Diagnostic list> =
    let settled, others =
        state.PendingContextInstances
        |> List.partition (fun pending ->
            pending.Def = name
            && pending.ArgTys.Length > 1
            && canSynthesize (pending.ArgTys |> List.map (resolveSoFar state)))
    settled
    |> List.rev
    |> List.fold (fun result pending ->
        result
        |> Result.bind (fun (current: InferState) ->
            // Only the type the instance is for is resolved. The others are constrained as the call has them: resolved,
            // one may be a union rest the unifier made up, which is numbered afresh each time the constraints are solved.
            let argTys = resolveSoFar current pending.ArgTys.Head :: pending.ArgTys.Tail
            synthesizeInstance env current pending.Callee pending.Context argTys pending.Span
            |> Result.map (fun (members, next) ->
                { next with ContextInstances = ContextInference.withInstance pending (InstanceSynthesized members) next.ContextInstances })))
        (Ok { state with PendingContextInstances = others })
