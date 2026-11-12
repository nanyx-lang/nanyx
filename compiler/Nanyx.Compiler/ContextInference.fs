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
            | TyNominal _ -> []
            | TyAlias(_, args, _)
            | TyApply(_, args)
            | TyUnion args -> args |> List.collect collect
            | TyFunc(arg, ret, contexts) -> (Unifier.writtenContexts contexts |> List.collect collect) @ collect arg @ collect ret
            | TyRecord fields -> fields |> Map.toList |> List.collect (snd >> collect)
            | TyOpenRecord(fields, row) -> (fields |> Map.toList |> List.collect (snd >> collect)) @ [ row ]
            | TyTag(_, payload) -> payload |> Option.map collect |> Option.defaultValue []
        collect ty |> List.distinctBy _.Id

    /// A name for each type variable of `ty`, by its id: `a`, `b`, `c`, ..., in the order they appear, then `a1`, `b1`.
    let varNames (ty: Ty) : Map<int, string> =
        varsInOrder ty
        |> List.mapi (fun index v ->
            let letter = string "abcdefghijklmnopqrstuvwxyz".[index % 26]
            v.Id, (if index < 26 then letter else $"{letter}{index / 26}"))
        |> Map.ofList

    /// `ty` as an annotation writes it, with its variables named by `names`. `None` for a type an annotation cannot
    /// write: an open record, a type that failed to check, or a variable `names` does not name.
    let rec typeExprOf (names: Map<int, string>) (ty: Ty) : TypeExpr option =
        let all (tys: Ty list) =
            let written = tys |> List.map (typeExprOf names)
            if written |> List.forall Option.isSome then Some (written |> List.map Option.get) else None
        let kind =
            match ty with
            | TyVar v -> names |> Map.tryFind v.Id |> Option.map TypeVar
            | TyPrimitive "unit" -> Some TypeUnit
            | TyPrimitive "?error" -> None
            | TyNominal(nominal, _, _) -> Some (TypeName nominal.Name)
            | TyPrimitive name
            | TyAlias(name, [], _) -> Some (TypeName name)
            | TyAlias(name, args, _)
            | TyApply(name, args) -> all args |> Option.map (fun args -> TypeApply(name, args))
            | TyFunc(arg, ret, contexts) ->
                // A context is written by its name alone when it has no arguments: `Console`, not `Console()`.
                let context ty =
                    match ty with
                    | TyApply(name, []) -> Some (TypeExpr.synthetic (TypeName name))
                    | _ -> typeExprOf names ty
                let written = Unifier.writtenContexts contexts |> List.map context
                match typeExprOf names arg, typeExprOf names ret with
                | Some arg, Some ret when written |> List.forall Option.isSome ->
                    match written |> List.map Option.get with
                    | [] -> Some (TypeFunc(arg, ret))
                    | contexts -> Some (TypeWithContext(contexts, TypeExpr.synthetic (TypeFunc(arg, ret))))
                | _ -> None
            // A tuple, such as a function's several parameters, is a record of the fields `0`, `1`, ...
            | TyRecord fields when not fields.IsEmpty && Set.ofSeq fields.Keys = Set.ofList (List.init fields.Count string) ->
                all (List.init fields.Count (fun index -> fields.[string index])) |> Option.map TypeTuple
            | TyRecord fields ->
                let fieldNames, fieldTys = fields |> Map.toList |> List.unzip
                all fieldTys
                |> Option.map (fun written ->
                    List.map2 (fun name fieldTy -> TypeField(name, false, false, Some fieldTy, None)) fieldNames written |> TypeRecord)
            | TyOpenRecord _ -> None
            | TyTag(name, None) -> Some (TypeTag(name, None))
            | TyTag(name, Some payload) -> typeExprOf names payload |> Option.map (fun payload -> TypeTag(name, Some payload))
            | TyUnion [] -> None
            | TyUnion items -> all items |> Option.map TypeUnion
        kind |> Option.map TypeExpr.synthetic

    /// The annotation an unannotated def of the type `defTy` would write to require `contexts`, each a context's name
    /// with the variables of `defTy` it is required at: the contexts, each applied to its variables, and the type
    /// without them. `None` when the type is one an annotation cannot write.
    let annotation (defTy: Ty) (contexts: (Identifier * TyVar list) list) : (TypeExpr list * TypeExpr) option =
        let names = varNames defTy
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
        let subst = fst (Unifier.solve state.Constraints)
        let written =
            state.TypeVars
            |> Map.toList
            |> List.choose (fun (name, v) ->
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
            | TyNominal _ -> []
            | TyAlias(_, args, _)
            | TyApply(_, args) -> args |> List.collect collect
            | TyUnion items -> items |> List.collect (function TyVar v -> [ v ] | item -> collect item)
            | TyFunc(arg, ret, _) -> collect arg @ collect ret
            | TyRecord fields
            | TyOpenRecord(fields, _) -> fields |> Map.toList |> List.collect (snd >> collect)
            | TyTag(_, payload) -> payload |> Option.map collect |> Option.defaultValue []
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
            let subst = fst (Unifier.solve state.Constraints)
            let unbound =
                used
                |> List.map (Unifier.apply subst)
                |> FunctionContexts.flatten
                |> Unifier.writtenContexts
                |> List.choose (function TyVar v -> Some v | _ -> None)
                |> List.distinctBy _.Id
            let taken = (varsInOrder (Unifier.apply subst defTy) |> List.choose _.Name) @ List.ofSeq state.TypeVars.Keys |> Set.ofList
            let letters = [ for letter in "cdefghijklmnopqrstuvwxyzab" do if not (taken.Contains (string letter)) then string letter ]
            let names, _, named =
                unbound
                |> List.fold (fun (names, letters, current: InferState) var ->
                    match var.Name, letters with
                    | Some name, _ -> names @ [ name ], letters, current
                    | None, letter :: rest ->
                        let namedVar = { Id = current.NextId; Name = Some letter }
                        let next =
                            { current with
                                NextId = current.NextId + 1
                                TypeVars = current.TypeVars |> Map.add letter namedVar
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
        let resolved = pending |> List.rev |> List.map (fun p -> p, p.ArgTys |> List.map (resolveSoFar state))
        // The variables of the def's own type that a call's arguments are, if they all are.
        let ownVars (argTys: Ty list) =
            let vars = argTys |> List.map (function TyVar v when scheme.Quantified.Contains v.Id -> Some v | _ -> None)
            if vars |> List.forall Option.isSome then Some (vars |> List.map Option.get) else None
        // The rests of the unions in the def's own type, which an operator or a call that only checks its context is
        // not passed one for: a call to the def checks it instead (see `checkRestsAtCalls`).
        let rests = unionRests scheme.Type |> List.map _.Id |> Set.ofList
        let ofRests (p: PendingContextInstance) (vars: TyVar list) = checksRests p && vars |> List.forall (fun v -> rests.Contains v.Id)
        let key (ctxName: Identifier, vars: TyVar list) = ctxName, vars |> List.map _.Id
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
                match ownVars argTys with
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
                match ownVars argTys, written with
                | Some vars, _ when ofRests p vars -> Ok current
                // A call to a local function is passed nothing: the def requiring the context is all it needs.
                | Some _, Some _ when p.ChecksOnly -> Ok current
                | Some vars, Some (contexts, _) ->
                    let context = contexts.[required |> List.findIndex (fun required -> key required = key (p.Context, vars))]
                    Ok { current with ContextInstances = withInstance p (InstanceInScope context) current.ContextInstances }
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
