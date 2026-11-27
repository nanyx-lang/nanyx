namespace NyxCompiler

open Parser.Program
open NyxCompiler.InferState

/// The parameterised contexts an unannotated def requires, inferred from its body. A def that writes no type and calls
/// `describe: [Show(a)] a -> string` with a value of one of its own type variables requires `Show` at that variable
/// itself, as if its type were written `[Show(a)] a -> string` too. Inference works it out once the def's type is
/// known (see `PendingContextInstance`), and the def is then given the annotation it would have written, so a call to
/// it and the backend treat it as they treat an annotated one.
module internal ContextInference =
    /// The type variables of `ty`, each once, in the order they appear. A nominal's underlying type is not included,
    /// nor an alias's (its arguments are).
    let varsInOrder (ty: Ty) : TyVar list =
        let rec collect ty =
            match ty with
            | TyVar v -> [ v ]
            | TyPrimitive _
            | TyParam _
            | TyNominal _
            | FunctionContexts.ScopedContext _ -> []
            | FunctionContexts.CellType(held, _) -> collect held
            | TyAlias(_, args, _)
            | TyApply(_, args)
            | TyRecursive(_, args)
            | TySelf args
            | TyUnion args -> args |> List.collect collect
            | TyFunc(arg, ret, contexts) ->
                (Unifier.writtenContexts contexts |> List.map FunctionContexts.withoutScope |> List.collect collect) @ collect arg @ collect ret
            | TyContexts contexts -> Unifier.writtenContexts contexts |> List.map FunctionContexts.withoutScope |> List.collect collect
            | TyRecord(fields, _) -> fields |> Map.toList |> List.collect (snd >> collect)
            | TyOpenRecord(fields, row) -> (fields |> Map.toList |> List.collect (snd >> collect)) @ [ row ]
            | TyTag(_, payload) -> payload |> Option.map collect |> Option.defaultValue []
            | TyScope scope
            | TyMeasure(_, scope) -> collect scope
        collect ty |> List.distinctBy _.Id

    /// The type variables of `ty` in the order `varsInOrder` gives them, then those in the arguments of its nominal
    /// types, which that leaves out: the `a` of `Box(a)`, and the `d` of `[d] seq(b)`, whose contexts are an argument
    /// of `Sequence`.
    let rec varsWithNominalArgs (ty: Ty) : TyVar list =
        let rec nominalArgs ty =
            match ty with
            | TyNominal(_, args, _, _) -> args
            | _ -> Ty.children ty |> List.collect nominalArgs
        varsInOrder ty @ (nominalArgs ty |> List.collect varsWithNominalArgs) |> List.distinctBy _.Id

    /// A name for each of `vars`, by its id: `a`, `b`, `c`, ..., in order, then `a1`, `b1`.
    let private namedInOrder (vars: TyVar list) : Map<int, string> =
        vars
        |> List.mapi (fun index v ->
            let letter = string "abcdefghijklmnopqrstuvwxyz".[index % 26]
            v.Id, (if index < 26 then letter else $"{letter}{index / 26}"))
        |> Map.ofList

    /// A name for each type variable of `ty`, by its id: `a`, `b`, `c`, ..., in the order they appear, then `a1`, `b1`.
    let varNames (ty: Ty) : Map<int, string> = namedInOrder (varsInOrder ty)

    /// `ty` as an annotation writes it, with its variables named by `names`, a nominal type by `nominalName` of it and
    /// an alias by `aliasName` of the name it was defined under. `None` for a type an annotation cannot write: a type
    /// that failed to check, or a variable `names` does not name.
    let rec typeExprWith (nominalName: NominalId -> string) (aliasName: string -> string) (names: Map<int, string>) (ty: Ty) : TypeExpr option =
        let typeExprOf = typeExprWith nominalName aliasName
        let all (tys: Ty list) =
            let written = tys |> List.map (typeExprOf names)
            if written |> List.forall Option.isSome then Some (written |> List.map Option.get) else None
        let writtenFields (fields: Map<string, Ty>) =
            let fieldNames, fieldTys = fields |> Map.toList |> List.unzip
            all fieldTys
            |> Option.map (List.map2 (fun name fieldTy -> TypeField(name, false, Some fieldTy, None)) fieldNames)
        let kind =
            match ty with
            | TyVar v -> names |> Map.tryFind v.Id |> Option.map TypeVar
            | TyPrimitive "unit" -> Some TypeUnit
            | TyPrimitive "?error" -> None
            // A nominal type's own arguments are written when `names` names each variable in them, `Box(int)` or
            // `Box(a)`, and left out when it doesn't: `varsInOrder` leaves out the variables of one, so only `names`
            // that `varsWithNominalArgs` gives names them. Its scope and the contexts it carries are not written.
            | TyNominal(nominal, args, _, _) ->
                let args = args |> List.filter (function FunctionContexts.ScopedContext _ | FunctionContexts.Carried _ -> false | _ -> true)
                if args.IsEmpty then Some (TypeName(nominalName nominal))
                else all args |> Option.map (fun args -> TypeApply(nominalName nominal, args)) |> Option.orElse (Some (TypeName(nominalName nominal)))
            | TyPrimitive name -> Some (TypeName name)
            // A measured value of one dimension is written as the dimension applied to its storage, `Time(int)`, and one of
            // a derived dimension as its parts so, multiplied and divided: `Length(float) / Time(float)`. One with nothing
            // to divide, `1 / Time`, has no type to write.
            | TyMeasure([ id, 1 ], storage) -> typeExprOf names storage |> Option.map (fun storage -> TypeApply(nominalName id, [ storage ]))
            | TyMeasure(dimension, storage) ->
                match Dimension.parts dimension, typeExprOf names storage with
                | (first :: above, below), Some storage ->
                    let part (id: NominalId) = TypeExpr.synthetic (TypeApply(nominalName id, [ storage ]))
                    let joined (combine: TypeExpr * TypeExpr -> TypeExprKind) (start: TypeExpr) (ids: NominalId list) =
                        ids |> List.fold (fun written id -> TypeExpr.synthetic (combine (written, part id))) start
                    let product = joined TypeProduct (part first) above
                    Some (joined TypeQuotient product below).Kind
                | _ -> None
            | TyAlias(name, [], _) -> Some (TypeName(aliasName name))
            | FunctionContexts.CellType(held, _) -> typeExprOf names held |> Option.map (fun held -> TypeApply(CellPassingSyntax.typeName, [ held ]))
            | TyAlias(name, args, _) -> all args |> Option.map (fun args -> TypeApply(aliasName name, args))
            | TyApply(name, args) -> all args |> Option.map (fun args -> TypeApply(name, args))
            | TyFunc(arg, ret, contexts) ->
                // A context is written by its name alone when it has no arguments: `Console`, not `Console()`.
                let context ty =
                    match ty with
                    | FunctionContexts.PlainContext name -> Some (TypeExpr.synthetic (TypeName name))
                    | _ -> typeExprOf names ty
                let written = Unifier.writtenContexts contexts |> List.map context
                match typeExprOf names arg, typeExprOf names ret with
                | Some arg, Some ret when written |> List.forall Option.isSome ->
                    match written |> List.map Option.get with
                    | [] -> Some (TypeFunc(arg, ret))
                    | contexts -> Some (TypeWithContext(contexts, TypeExpr.synthetic (TypeFunc(arg, ret))))
                | _ -> None
            // A tuple, such as a function's several parameters, is a record of the fields `0`, `1`, ...
            | TyRecord(fields, _) when not fields.IsEmpty && Set.ofSeq fields.Keys = Set.ofList (List.init fields.Count string) ->
                all (List.init fields.Count (fun index -> fields.[string index])) |> Option.map TypeTuple
            | TyRecord(fields, _) -> writtenFields fields |> Option.map TypeRecord
            // `(price: b, ...a)`, its other fields the row variable `a`.
            | TyOpenRecord(fields, row) ->
                match writtenFields fields, names |> Map.tryFind row.Id with
                | Some written, Some row -> Some (TypeOpenRecord(written, Some row))
                | _ -> None
            | TyTag(name, None) -> Some (TypeTag(name, None))
            | TyTag(name, Some payload) -> typeExprOf names payload |> Option.map (fun payload -> TypeTag(name, Some payload))
            | TyUnion [] -> None
            | TyUnion items -> all items |> Option.map TypeUnion
            // Only inside what a recursive alias stands for, which is written by the alias's name.
            | TyRecursive _
            | TySelf _
            | TyParam _ -> None
            // A scoped context's scope is never written, nor are the contexts a type carries as an argument.
            | TyScope _
            | TyContexts _ -> None
        kind |> Option.map TypeExpr.synthetic

    /// `ty` as an annotation in the module that defines each type it names writes it (see `typeExprWith`).
    let typeExprOf (names: Map<int, string>) (ty: Ty) : TypeExpr option =
        typeExprWith (fun nominal -> nominal.Name) id names ty

    /// The annotation an unannotated def of the type `defTy` would write to require `contexts`, each a context's name
    /// with the variables of `defTy` it is required at: the contexts, each applied to its variables, and the type
    /// without them. `None` when the type is one an annotation cannot write. A context's associated type is an argument
    /// after its own (see `AssociatedTypes`), which need not be in `defTy`: one that isn't is named after its variables.
    /// A nominal type's arguments are written, `[Eq(a)] Box(a) -> bool`, so a call reads them as the def's variables,
    /// not as the type's own parameters.
    let annotation (defTy: Ty) (contexts: (Identifier * TyVar list) list) : (TypeExpr list * TypeExpr) option =
        let inType = namedInOrder (varsWithNominalArgs defTy)
        let names =
            contexts
            |> List.collect snd
            |> List.distinctBy _.Id
            |> List.filter (fun v -> not (inType.ContainsKey v.Id))
            |> List.fold (fun (names: Map<int, string>) v ->
                let index = names.Count
                let letter = string "abcdefghijklmnopqrstuvwxyz".[index % 26]
                names |> Map.add v.Id (if index < 26 then letter else $"{letter}{index / 26}")) inType
        let written =
            contexts
            |> List.map (fun (ctxName, vars) ->
                vars
                |> List.map (fun v -> names |> Map.tryFind v.Id |> Option.map (TypeVar >> TypeExpr.synthetic))
                |> fun args ->
                    if args |> List.forall Option.isSome then Some (TypeExpr.synthetic (TypeApply(ctxName, args |> List.map Option.get)))
                    else None)
        match typeExprOf names defTy with
        | Some signature when written |> List.forall Option.isSome -> Some (written |> List.map Option.get, signature)
        | _ -> None

    /// The type of a def generalised as `scheme` that writes the contexts it requires but not its type, as it would
    /// have written it: `a, a -> a` for `` def add: [Add(a)] = { + } ``. A variable of the type that one written in
    /// the contexts turned out to be has that one's name, and each other a letter no written one has. A call reads
    /// the contexts' arguments from it, as it does from a type that is written. `None` when the type is one an
    /// annotation cannot write.
    let writtenType (state: InferState) (scheme: TypeScheme) : TypeExpr option =
        let subst = fst (Solving.solve state.Constraints)
        let written =
            state.TypeVars
            |> Map.toList
            |> List.choose (fun (key, v) ->
                let name = match key with WrittenVar name -> name | ScopeOf context -> context
                match Unifier.apply subst (TyVar v) with
                | TyVar solved when scheme.Quantified.Contains solved.Id -> Some (solved.Id, name)
                | _ -> None)
            |> Map.ofList
        let taken = written |> Map.values |> Set.ofSeq
        let others = varsInOrder scheme.Type |> List.filter (fun v -> not (written.ContainsKey v.Id))
        let letters = [ for letter in 'a' .. 'z' do if not (taken.Contains (string letter)) then string letter ]
        if others.Length > letters.Length then None
        else
            let names = List.zip others (List.truncate others.Length letters) |> List.fold (fun names (v, letter) -> names |> Map.add v.Id letter) written
            typeExprOf names scheme.Type

    /// The rests of the unions anywhere in `ty`, each once: `r` in `#none | r -> bool`.
    let unionRests (ty: Ty) : TyVar list =
        let rec collect ty =
            match ty with
            | TyVar _
            | TyPrimitive _
            | TyParam _
            | TyNominal _
            | TyContexts _ -> []
            | TyAlias(_, args, _)
            | TyApply(_, args)
            | TyRecursive(_, args)
            | TySelf args -> args |> List.collect collect
            | TyUnion items -> items |> List.collect (function TyVar v -> [ v ] | item -> collect item)
            | TyFunc(arg, ret, _) -> collect arg @ collect ret
            | TyRecord(fields, _)
            | TyOpenRecord(fields, _) -> fields |> Map.toList |> List.collect (snd >> collect)
            | TyTag(_, payload) -> payload |> Option.map collect |> Option.defaultValue []
            | TyScope scope
            | TyMeasure(_, scope) -> collect scope
        collect ty |> List.distinctBy _.Id

    /// Whether `pending` is passed nothing for its context when it is of a rest of a union in the def's type: a
    /// well-known context's operator, which is the compiler's own, or a call that `ChecksOnly`. Any other call is passed
    /// the instance it finds.
    let private checksRests (pending: PendingContextInstance) =
        pending.ChecksOnly || WellKnownContexts.isOperatorPending pending

    /// `state` with the rests of the unions in the type of the def `name`, generalised as `scheme` and declared as
    /// `symbol`, that `pendings` need a well-known context of, by the context: the def is passed nothing for them, and a
    /// call to it checks the context of what they are there (see `InferState.CheckedRests`). `pendings` are the def's
    /// own and those left to the end of the module that are written in it (see `InferState.HoleScopes`).
    let checkRestsAtCalls (name: Identifier) (symbol: SymbolId option) (scheme: TypeScheme) (pendings: PendingContextInstance list) (state: InferState) =
        let rests = unionRests scheme.Type |> List.map _.Id
        let writtenIn (pending: PendingContextInstance) =
            pending.Def = name
            || (state.HoleScopes |> Map.tryFind pending.Call |> Option.bind snd) = Some name
        let checkedRests =
            pendings
            |> List.filter (fun pending -> writtenIn pending && checksRests pending && (WellKnownContexts.tryFind pending.Context).IsSome)
            |> List.collect (fun pending ->
                pending.ArgTys
                |> List.collect (resolveSoFar state >> WellKnownContexts.checkedVars)
                |> List.filter (fun v -> scheme.Quantified.Contains v.Id)
                |> List.choose (fun v -> rests |> List.tryFindIndex ((=) v.Id))
                |> List.map (fun place -> pending.Context, place))
            |> List.groupBy fst
            |> List.map (fun (ctxName, places) -> ctxName, places |> List.map snd |> List.distinct |> List.sort)
            |> Map.ofList
        match symbol with
        | Some symbol when not checkedRests.IsEmpty -> { state with CheckedRests = state.CheckedRests |> Map.add symbol checkedRests }
        | _ -> state

    /// The variables of the type of the function `symbol`, declared as `scheme`, that a call to it needs a well-known
    /// context of but does not pass, by the context: those of a local function (see `InferState.LocalComparisons`),
    /// and the rests of the unions it compares (see `InferState.CheckedRests`).
    let checkedAtCalls (state: InferState) (symbol: SymbolId) (scheme: TypeScheme) : Map<Identifier, Set<int>> =
        let rests = unionRests scheme.Type
        let checkedRests =
            state.CheckedRests
            |> Map.tryFind symbol
            |> Option.defaultValue Map.empty
            |> Map.map (fun _ places -> places |> List.choose (fun place -> List.tryItem place rests) |> List.map _.Id |> Set.ofList)
        state.LocalComparisons
        |> Map.tryFind symbol
        |> Option.defaultValue Map.empty
        |> Map.fold (fun all ctxName used -> all |> Map.add ctxName (Set.union used (all |> Map.tryFind ctxName |> Option.defaultValue Set.empty))) checkedRests

    /// The context variables the unannotated top-level def `defName`, of type `defTy`, was found to use (see
    /// `ContextVariables.require`) that nothing bound, each given a name as an annotation would have written it, and
    /// `state` with the names given. `def apply = { f, x -> f(x) }` is `[c] ([c] a -> b), a -> b`. Each name is a letter
    /// from `c` on that the def's type does not use already.
    let nameContextVariables (defName: Identifier) (defTy: Ty) (state: InferState) : Identifier list * InferState =
        match state.InferredContextVars |> Map.tryFind defName with
        | None -> [], state
        | Some used ->
            let state = { state with InferredContextVars = state.InferredContextVars |> Map.remove defName }
            let subst = fst (Solving.solve state.Constraints)
            let unbound =
                used
                |> List.map (Unifier.apply subst)
                |> FunctionContexts.flatten
                |> Unifier.writtenContexts
                |> List.choose (function TyVar v -> Some v | _ -> None)
                |> List.distinctBy _.Id
            let taken = (varsInOrder (Unifier.apply subst defTy) |> List.choose _.Name) @ (state.TypeVars.Keys |> Seq.choose (function WrittenVar name -> Some name | ScopeOf _ -> None) |> List.ofSeq) |> Set.ofList
            let letters = [ for letter in "cdefghijklmnopqrstuvwxyzab" do if not (taken.Contains (string letter)) then string letter ]
            let names, _, named =
                unbound
                |> List.fold (fun (names, letters, current: InferState) var ->
                    match var.Name, letters with
                    | Some name, _ -> names @ [ name ], letters, current
                    | None, letter :: rest ->
                        let namedVar = TyVar.ordinary current.NextId (Some letter)
                        let next =
                            { current with
                                NextId = current.NextId + 1
                                TypeVars = current.TypeVars |> Map.add (WrittenVar letter) namedVar
                                Constraints = (TyVar var, TyVar namedVar, Equal, SourceSpan.none) :: current.Constraints }
                        names @ [ letter ], rest, next
                    | None, [] -> names, [], current) ([], letters, state)
            names, named

    /// `instances` with the call `pending` names finding its context as `instance`.
    let internal withInstance (pending: PendingContextInstance) (instance: ContextInstance) (instances: Map<NodeId, ContextInstance option list>) =
        let found = instances |> Map.tryFind pending.Call |> Option.defaultValue (List.replicate pending.Count None)
        instances |> Map.add pending.Call (found |> List.updateAt pending.Index (Some instance))

    /// Finds the instances the calls in the unannotated top-level def `name` (declared as `symbol`) left to be found
    /// once it was inferred, now that it is generalised as `scheme`. A call whose arguments turned out to be variables
    /// of the def's own type finds the instance the def then requires itself; one whose arguments turned out to be
    /// types finds the instance `synthesize` finds or builds for them, if it can (or none, for a call it reports as
    /// failed itself). A call that requires a well-known context of a type with one of the def's variables in it,
    /// `Eq(list(a))`, makes the def require it of the variable, and so does one that requires a context whose instance for that type is
    /// derived from its parts: `derivedVars` gives the variables such an instance requires the context of. `synthesize`
    /// is also given the instance the def now requires for each of its own variables, by the context's name. The def
    /// is registered as requiring the contexts it was found to, and the annotation that would have said so is returned.
    let resolvePending
        (state: InferState)
        (name: Identifier)
        (symbol: SymbolId option)
        (scheme: TypeScheme)
        (derivedVars: PendingContextInstance -> Ty list -> TyVar list)
        (synthesize: (Identifier -> TyVar -> TypeExpr option) -> PendingContextInstance -> Ty list -> InferState -> Result<ContextInstance option * InferState, Diagnostic list> option)
        : Result<TypeExpr option * InferState, Diagnostic list> =
        let state = checkRestsAtCalls name symbol scheme state.PendingContextInstances state
        let pending, others = state.PendingContextInstances |> List.partition (fun p -> p.Def = name)
        let state = { state with PendingContextInstances = others }
        // How many of a context's arguments are its own parameters, before its associated types (see `AssociatedTypes`).
        let writtenOf (ctxName: Identifier) =
            TypeResolution.tryFindTypeDef ctxName state
            |> Option.map (fun info -> info.Parameters.Length - info.AssociatedTypes.Length)
            |> Option.defaultValue System.Int32.MaxValue
        let key (ctxName: Identifier, vars: TyVar list) = ctxName, vars |> List.truncate (writtenOf ctxName) |> List.map _.Id
        // The variables of the def's own type that a call's arguments are, if they all are, and the variables its
        // context's associated types are at, which the instance fixes, and so need not be the def's own.
        let ownVars (ctxName: Identifier) (argTys: Ty list) =
            let written = writtenOf ctxName
            let vars =
                argTys
                |> List.mapi (fun index ty ->
                    match ty with
                    | TyVar v when index >= written || scheme.Quantified.Contains v.Id -> Some v
                    | _ -> None)
            if vars |> List.forall Option.isSome then Some (vars |> List.map Option.get) else None
        // Two calls that require one context of the same variables require it at the same associated types.
        let state =
            pending
            |> List.choose (fun p -> ownVars p.Context (p.ArgTys |> List.map (resolveSoFar state)) |> Option.map (fun vars -> key (p.Context, vars), p.ArgTys))
            |> List.groupBy fst
            |> List.fold (fun current ((ctxName, _), calls) ->
                let associated (argTys: Ty list) = List.skip (min (writtenOf ctxName) argTys.Length) argTys
                match calls |> List.map (snd >> associated) with
                | first :: others when others |> List.forall (fun other -> other.Length = first.Length) ->
                    others |> List.fold (fun next other -> List.fold2 (fun next a b -> addConstraint a b next) next first other) current
                | _ -> current) state
        let resolved = pending |> List.rev |> List.map (fun p -> p, p.ArgTys |> List.map (resolveSoFar state))
        // The rests of the unions in the def's own type, which an operator or a call that only checks its context is
        // not passed one for: a call to the def checks it instead (see `checkRestsAtCalls`).
        let rests = unionRests scheme.Type |> List.map _.Id |> Set.ofList
        let ofRests (p: PendingContextInstance) (vars: TyVar list) = checksRests p && vars |> List.forall (fun v -> rests.Contains v.Id)
        // A well-known context of a type with more to it than a variable is that context of each of the def's
        // variables it compares values of: `Eq(list(a))` is `Eq(a)`.
        let reduced (p: PendingContextInstance) (argTys: Ty list) =
            let vars =
                if (WellKnownContexts.tryFind p.Context).IsSome then argTys |> List.collect WellKnownContexts.comparedVars
                else derivedVars p argTys
            vars
            |> List.filter (fun v -> scheme.Quantified.Contains v.Id && not (ofRests p [ v ]))
            |> List.map (fun v -> p.Context, [ v ])
        let required =
            resolved
            |> List.collect (fun (p, argTys) ->
                match ownVars p.Context argTys with
                | Some vars when ofRests p vars -> []
                | Some vars -> [ p.Context, vars ]
                | None -> reduced p argTys)
            |> List.distinctBy key
        let written = if required.IsEmpty then None else annotation scheme.Type required
        let ownInstance (ctxName: Identifier) (var: TyVar) =
            match written, required |> List.tryFindIndex (fun required -> key required = (ctxName, [ var.Id ])) with
            | Some (contexts, _), Some index -> Some contexts.[index]
            | _ -> None
        resolved
        |> List.fold (fun result (p, argTys) ->
            result
            |> Result.bind (fun (current: InferState) ->
                match ownVars p.Context argTys, written with
                | Some vars, _ when ofRests p vars -> Ok current
                // A call to a local function is passed nothing: the def requiring the context is all it needs.
                | Some _, Some _ when p.ChecksOnly -> Ok current
                | Some vars, Some (contexts, _) ->
                    let context = contexts.[required |> List.findIndex (fun required -> key required = key (p.Context, vars))]
                    Ok { current with ContextInstances = withInstance p (InstanceInScope(context, FromInference)) current.ContextInstances }
                // An operator in a def whose type cannot be written is the compiler's own, as it is in a local function.
                | Some _, None when not (WellKnownContexts.isOperatorPending p || p.ChecksOnly) -> Error p.Errors
                | _ ->
                    match synthesize ownInstance p argTys current with
                    | Some synthesized ->
                        synthesized
                        |> Result.map (function
                            | Some instance, next when not p.ChecksOnly -> { next with ContextInstances = withInstance p instance next.ContextInstances }
                            | _, next -> next)
                    | None -> Error p.Errors)) (Ok state)
        |> Result.map (fun current ->
            match written with
            | Some (contexts, signature) ->
                let contextTys = required |> List.map (fun (ctxName, vars) -> TyApply(ctxName, vars |> List.map TyVar))
                let registered = registerContextRequirement current name contexts (Some signature)
                Some (TypeExpr.synthetic (TypeWithContext(contexts, signature))),
                match symbol with
                | Some symbol -> { registered with InferredContextTypes = registered.InferredContextTypes |> Map.add symbol contextTys }
                | None -> registered
            | None -> None, current)

    /// The error, at `span`, for the def `name` that writes no type and whose value, of type `defTy`, is not a
    /// function, if one of `calls` in its value requires a parameterised context of a type its value leaves open:
    /// `def obj = parse("x")` with `parse: [Decode(a)] string -> a`. A value can't require a context, and its type is
    /// not settled by how it is used later, so `def person: Person = obj` doesn't find the instance: the type is passed
    /// as a type argument or written on the def.
    let unfixedValueType (state: InferState) (name: Identifier) (span: SourceSpan) (defTy: Ty) (calls: PendingContextInstance list) : Diagnostic list =
        let defTy = resolveSoFar state defTy
        match Unifier.unalias defTy with
        | TyFunc _ -> []
        | _ ->
            let open' = TypeEnv.freeTyVars defTy
            calls
            |> List.tryFind (fun p ->
                not p.ChecksOnly
                && (WellKnownContexts.tryFind p.Context).IsNone
                && not (WellKnownContexts.isOperatorPending p)
                && not (InterpolationHoles.isHole p.Callee)
                && p.ArgTys |> List.exists (resolveSoFar state >> TypeEnv.freeTyVars >> Set.intersect open' >> Set.isEmpty >> not))
            |> Option.map (fun p ->
                let message =
                    $"'{p.Callee}' requires '{p.Context}' of a type nothing in the definition of '{name}' fixes, and a later "
                    + $"use of a value doesn't fix its type: write the type, as in 'def {name}: T = ...', or pass it as a type "
                    + $"argument to a function that takes one, as in '{p.Callee}(type T, ...)'"
                Diagnostics.at DiagnosticCodes.ValueContextTypeOpen span message)
            |> Option.toList

    /// `state` with the local def `name`, declared as `symbol`, which writes no type and was just generalised as
    /// `scheme`, requiring the parameterised contexts the calls in its value need of its own type variables, as if its
    /// type were written: `def f = { x -> describe(x) }` requires `Show` of its parameter's type, and a call to it finds
    /// an instance for the type it is called with. `before` are the calls that were waiting for an instance before the
    /// def's value was inferred; the rest are its value's. A call of the value's whose arguments are not all variables
    /// of the def's own type is left to the def around it, and so are a well-known context's operator, as `==` (see
    /// `InferState.LocalComparisons`), and an interpolation hole's. A call to a function that requires a well-known
    /// context, as `eq(x, y)` for `eq: [Eq(a)]`, is taken (#2813). A call is taken whether the def around it writes its
    /// type or not: in one that does, it was left to the end of the module.
    let resolveLocal (name: Identifier) (symbol: SymbolId option) (scheme: TypeScheme) (before: PendingContextInstance list) (state: InferState) : InferState =
        let earlier = before |> List.map (fun p -> p.Call, p.Index) |> Set.ofList
        let candidates =
            state.PendingContextInstances
            |> List.filter (fun p ->
                not (earlier.Contains (p.Call, p.Index))
                && not p.ChecksOnly
                && not (InterpolationHoles.isHole p.Callee))
        if candidates.IsEmpty || scheme.Quantified.IsEmpty then state
        else
            let subst = fst (Solving.solve state.Constraints)
            let ownVars (p: PendingContextInstance) =
                let vars = p.ArgTys |> List.map (Unifier.applySolved subst >> function TyVar v when scheme.Quantified.Contains v.Id -> Some v | _ -> None)
                if vars |> List.forall Option.isSome then Some (p, vars |> List.map Option.get) else None
            let taken = candidates |> List.rev |> List.choose ownVars
            let key (ctxName: Identifier, vars: TyVar list) = ctxName, vars |> List.map _.Id
            let required = taken |> List.map (fun (p, vars) -> p.Context, vars) |> List.distinctBy key
            match annotation scheme.Type required with
            | Some (contexts, signature) when not taken.IsEmpty ->
                let instances =
                    taken
                    |> List.fold (fun instances (p, vars) ->
                        let context = contexts.[required |> List.findIndex (fun required -> key required = key (p.Context, vars))]
                        withInstance p (InstanceInScope(context, FromInference)) instances) state.ContextInstances
                let takenCalls = taken |> List.map (fun (p, _) -> p.Call, p.Index) |> Set.ofList
                let left = state.PendingContextInstances |> List.filter (fun p -> not (takenCalls.Contains (p.Call, p.Index)))
                // A call in a def that writes its type was left to the end of the module, in the scope it is written
                // in (see `InferState.HoleScopes`), which it no longer needs once nothing of it is left.
                let leftCalls = left |> List.map _.Call |> Set.ofList
                let registered =
                    registerLocalContextRequirement
                        { state with
                            ContextInstances = instances
                            PendingContextInstances = left
                            HoleScopes = state.HoleScopes |> Map.filter (fun call _ -> leftCalls.Contains call || not (takenCalls |> Set.exists (fst >> (=) call))) }
                        symbol name contexts (Some signature)
                // An operator on the def's own variables, as `a == b` in `def same = { a, b -> a == b }`, calls the
                // member of the instance the def is now passed, as it does in a top-level def that writes no type, so
                // that a type's own `==` is the one it calls (#2809).
                let operators = taken |> List.map fst
                let inputOf ty = TypeResolution.inputTypeFromArgs [ ty; ty ]
                let registered =
                    registered
                    |> EqualityOperators.callMembers inputOf (operators |> List.filter EqualityOperators.isPending)
                    |> OrderingOperators.callHelpers inputOf (operators |> List.filter OrderingOperators.isPending)
                match symbol with
                | Some symbol ->
                    let contextTys = required |> List.map (fun (ctxName, vars) -> TyApply(ctxName, vars |> List.map TyVar))
                    { registered with InferredContextTypes = registered.InferredContextTypes |> Map.add symbol contextTys }
                | None -> registered
            | _ -> state
