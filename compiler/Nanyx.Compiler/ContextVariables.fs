/// Context variables. A single lowercase letter in a context list, `c` in `[c] list(a), ([c] a -> b) -> list(b)`,
/// stands for whatever contexts the function given for it requires. A def's own context list binds it, and a call binds
/// it to the contexts of the argument passed for it, which the call then requires, as it would if the callee wrote them.
module internal NyxCompiler.ContextVariables

open Parser.Program
open NyxCompiler.InferState

/// The context variables the context list `contexts` names, each with where it is written.
let private namedIn (contexts: TypeExpr list) =
    contexts |> List.choose (fun ctx -> match ctx.Kind with TypeVar name -> Some (name, ctx.Span) | _ -> None)

/// The context variables the context list `contexts` names.
let names (contexts: TypeExpr list) : Set<Identifier> =
    namedIn contexts |> List.map fst |> Set.ofList

/// Each context variable written on a function type within `typeExpr`, with where it is written.
let rec private writtenIn (typeExpr: TypeExpr) : (Identifier * SourceSpan) list =
    let ofList = List.collect writtenIn
    match typeExpr.Kind with
    | TypeWithContext(contexts, inner) -> namedIn contexts @ writtenIn inner
    | TypeFunc(arg, ret) -> writtenIn arg @ writtenIn ret
    | TypeTuple items
    | TypeUnion items
    | TypeIntersection items
    | TypeApply(_, items) -> ofList items
    | TypeRecord fields ->
        fields
        |> List.collect (function
            | TypeField(_, _, _, Some t, _)
            | TypeMember(_, Some t) -> writtenIn t
            | _ -> [])
    | TypeTag(_, Some inner)
    | TypeOptional inner
    | TypeConstraint(_, inner, _)
    | TypeWhere(inner, _) -> writtenIn inner
    | _ -> []

/// Whether each context variable written on a function type in `typeOpt`, the annotation of the def `name`, is one
/// the def's own context list binds: `[c]` in `[c] list(a), ([c] a -> b) -> list(b)`.
let ensureBound (name: Identifier) (typeOpt: TypeExpr option) : Result<unit, Diagnostic list> =
    let bound, signature =
        match typeOpt with
        | Some (Kind (TypeWithContext(contexts, inner))) -> names contexts, Some inner
        | other -> Set.empty, other
    match signature |> Option.map writtenIn |> Option.defaultValue [] |> List.filter (fst >> bound.Contains >> not) |> List.distinctBy fst with
    | [] -> Ok ()
    | unbound ->
        unbound
        |> List.map (fun (var, span) ->
            Diagnostics.at DiagnosticCodes.UnboundContextVariable span
                $"The context variable '{var}' is not bound: add it to the contexts of '{name}', as in '[{var}] ...'")
        |> Error

/// The type at each context variable of `general`'s function types that `isVariable` picks, paired with what stands at
/// the same place in `specific`, a type of the same shape: a callee's type as it is declared, against the same type
/// instantiated at a call.
let rec private pairs (isVariable: TyVar -> bool) (general: Ty) (specific: Ty) : (TyVar * Ty) list =
    let all (gs: Ty list) (ss: Ty list) =
        if gs.Length = ss.Length then List.map2 (pairs isVariable) gs ss |> List.concat else []
    match general, specific with
    | TyFunc(ga, gr, gc), TyFunc(sa, sr, sc) ->
        let own =
            if gc.Length = sc.Length then
                List.zip gc sc |> List.choose (function TyVar v, s when isVariable v -> Some (v, s) | _ -> None)
            else []
        own @ pairs isVariable ga sa @ pairs isVariable gr sr
    | TyApply(_, gs), TyApply(_, ss)
    | TyAlias(_, gs, _), TyAlias(_, ss, _)
    | TyUnion gs, TyUnion ss -> all gs ss
    | TyRecord gf, TyRecord sf when Set.ofSeq gf.Keys = Set.ofSeq sf.Keys -> all (List.ofSeq gf.Values) (List.ofSeq sf.Values)
    | TyTag(_, Some g), TyTag(_, Some s) -> pairs isVariable g s
    | _ -> []

/// The contexts the context variables `declared` (a callee's own) stand for at a call: found in `general`, the callee's
/// type, at the same places in `instantiated`, its type at the call, and resolved by `resolve` as far as the types so
/// far go. A variable nothing has bound yet stands for none, and so does a variable kept only for room (see
/// `FunctionContexts.fit`).
let boundAt (resolve: Ty -> Ty) (declared: Set<Identifier>) (general: Ty) (instantiated: Ty) : Ty list =
    pairs (fun v -> v.Name |> Option.exists declared.Contains) general instantiated
    |> List.distinctBy (fst >> _.Id)
    |> List.collect (fun (_, at) -> FunctionContexts.flatten [ resolve at ])
    |> List.filter (function
        | TyVar v -> v.Name.IsSome && not (Unifier.isMadeUpRest v)
        | _ -> true)
    |> List.distinct

/// What it takes for a call to `callee` at `span` to require `contexts` (see `boundAt`) where it is, in the def or
/// at the top level `state.ContextScope` says: `requirePlain` checks a context such as `Console`, as if the callee
/// declared it; a context variable must be one the def around the call declares, or it infers it. A parameterised
/// context at a concrete type is settled where the function given for it is made, so it is not required again.
let require
    (requirePlain: InferState -> TypeExpr -> Result<InferState, Diagnostic list>)
    (callee: Identifier)
    (span: SourceSpan)
    (contexts: Ty list)
    (state: InferState)
    : Result<InferState, Diagnostic list> =
    contexts
    |> List.fold (fun result context ->
        result
        |> Result.bind (fun current ->
            match context, current.ContextScope with
            | TyApply(name, []), _ -> requirePlain current (TypeExpr.synthetic (TypeName name))
            | TyVar { Name = Some var }, DefScope(defName, false) ->
                let declared = current.ContextRequirements |> Map.tryFind defName |> Option.map names |> Option.defaultValue Set.empty
                if declared.Contains var then Ok current
                else
                    Error [ Diagnostics.at DiagnosticCodes.UndeclaredContext span
                                $"'{callee}' requires the contexts '{var}', which '{defName}' does not declare: add [{var}] to the type of '{defName}'" ]
            | _ -> Ok current)) (Ok state)
