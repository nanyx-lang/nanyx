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
    let private varsInOrder (ty: Ty) : TyVar list =
        let rec collect ty =
            match ty with
            | TyVar v -> [ v ]
            | TyPrimitive _
            | TyNominal _ -> []
            | TyAlias(_, args, _)
            | TyApply(_, args)
            | TyUnion args -> args |> List.collect collect
            | TyFunc(arg, ret) -> collect arg @ collect ret
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
            | TyPrimitive name
            | TyNominal(name, _, _)
            | TyAlias(name, [], _) -> Some (TypeName name)
            | TyAlias(name, args, _)
            | TyApply(name, args) -> all args |> Option.map (fun args -> TypeApply(name, args))
            | TyFunc(arg, ret) ->
                match typeExprOf names arg, typeExprOf names ret with
                | Some arg, Some ret -> Some (TypeFunc(arg, ret))
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

    /// `instances` with the call `pending` names finding its context as `instance`.
    let private withInstance (pending: PendingContextInstance) (instance: ContextInstance) (instances: Map<NodeId, ContextInstance option list>) =
        let found = instances |> Map.tryFind pending.Call |> Option.defaultValue (List.replicate pending.Count None)
        instances |> Map.add pending.Call (found |> List.updateAt pending.Index (Some instance))

    /// Finds the instances the calls in the unannotated top-level def `name` (declared as `symbol`) left to be found
    /// once it was inferred, now that it is generalised as `scheme`. A call whose arguments turned out to be variables
    /// of the def's own type finds the instance the def then requires itself; one whose arguments turned out to be
    /// types finds the instance `synthesize` finds or builds for them, if it can (or none, for a call it reports as
    /// failed itself). A call that requires `Eq` of a type with one of the def's variables in it, `Eq(list(a))`, makes
    /// the def require `Eq` of the variable. The def is registered as requiring
    /// the contexts it was found to, and the annotation that would have said so is returned.
    let resolvePending
        (state: InferState)
        (name: Identifier)
        (symbol: SymbolId option)
        (scheme: TypeScheme)
        (synthesize: PendingContextInstance -> Ty list -> InferState -> Result<ContextInstance option * InferState, Diagnostic list> option)
        : Result<TypeExpr option * InferState, Diagnostic list> =
        let pending, others = state.PendingContextInstances |> List.partition (fun p -> p.Def = name)
        let state = { state with PendingContextInstances = others }
        let resolved = pending |> List.rev |> List.map (fun p -> p, p.ArgTys |> List.map (resolveSoFar state))
        // The variables of the def's own type that a call's arguments are, if they all are.
        let ownVars (argTys: Ty list) =
            let vars = argTys |> List.map (function TyVar v when scheme.Quantified.Contains v.Id -> Some v | _ -> None)
            if vars |> List.forall Option.isSome then Some (vars |> List.map Option.get) else None
        let key (ctxName: Identifier, vars: TyVar list) = ctxName, vars |> List.map _.Id
        // `Eq` of a type with more to it than a variable is `Eq` of each of the def's variables it compares values of.
        let reduced (p: PendingContextInstance) (argTys: Ty list) =
            if p.Context <> EqualityOperators.context then []
            else
                argTys
                |> List.collect EqualityOperators.comparedVars
                |> List.filter (fun v -> scheme.Quantified.Contains v.Id)
                |> List.map (fun v -> p.Context, [ v ])
        let required =
            resolved
            |> List.collect (fun (p, argTys) ->
                match ownVars argTys with
                | Some vars -> [ p.Context, vars ]
                | None -> reduced p argTys)
            |> List.distinctBy key
        let written = if required.IsEmpty then None else annotation scheme.Type required
        resolved
        |> List.fold (fun result (p, argTys) ->
            result
            |> Result.bind (fun (current: InferState) ->
                match ownVars argTys, written with
                // A call to a local function is passed nothing: the def requiring the context is all it needs.
                | Some _, Some _ when p.ChecksOnly -> Ok current
                | Some vars, Some (contexts, _) ->
                    let context = contexts.[required |> List.findIndex (fun required -> key required = key (p.Context, vars))]
                    Ok { current with ContextInstances = withInstance p (InstanceInScope context) current.ContextInstances }
                // A comparison in a def whose type cannot be written is the compiler's own, as it is in a local function.
                | Some _, None when not (EqualityOperators.isPending p || p.ChecksOnly) -> Error p.Errors
                | _ ->
                    match synthesize p argTys current with
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
