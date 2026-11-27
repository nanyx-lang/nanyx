/// A `match` on a `Type(a)`, which inspects the type it stands for (see `TypeDescriptions`). Each arm is a tag of the
/// type's description, and an arm that says what `a` is narrows it: in the `#int` arm the checker knows `a` is `int`,
/// in the `#list(elem)` arm that it is a list of a type it never learns, which `elem` stands for. Narrowing is only for
/// a `Type` match, not for matches in general: an arm is checked with `a` written as what the arm says it is in the
/// types of what is in scope and in the type the match is expected to give, so `value: a` is an `int` in the `#int`
/// arm, and the arm gives an `int` where the match gives an `a`.
module internal NyxCompiler.TypeMatches

open Parser.Program
open NyxCompiler.InferState
open NyxCompiler.TypeValues

/// What an arm of a `Type(a)` match finds out: the type its tag's payload has, if it has one, and what `a` is, if the
/// tag says.
type private Case = { Payload: Ty option; Narrowed: Ty option }

/// What the arm of the tag `tag` finds of the type `standsFor`, if `tag` is one of a description's. A collection's
/// element types are each a type of its own, which `elem` stands for.
let private caseOf (state: InferState) (standsFor: Ty) (tag: string) : (Case * InferState) option =
    match builtInDescriptions |> List.tryFind (fst >> (=) tag), collectionDescriptions |> List.tryFind (fun (name, _, _) -> name = tag) with
    | Some (_, ty), _ -> Some ({ Payload = None; Narrowed = Some ty }, state)
    | None, Some (_, count, collection) ->
        let elements, next =
            List.init count id
            |> List.mapFold (fun current _ -> TypeDescriptions.skolem (if count = 1 then "b" else "k") current) state
        let payload =
            match elements with
            | [ element ] -> ofType element
            | _ -> TypeResolution.tupleRecordType (elements |> List.map ofType)
        Some ({ Payload = Some payload; Narrowed = Some (collection elements) }, next)
    | None, None ->
        match tag with
        | t when t = functionTag -> Some ({ Payload = None; Narrowed = None }, state)
        | t when t = recordTag -> Some ({ Payload = Some (TyApply(recordInfo, [ standsFor ])); Narrowed = None }, state)
        | t when t = unionTag -> Some ({ Payload = Some (TyApply(unionInfo, [ standsFor ])); Narrowed = None }, state)
        | t when t = nominalTag -> Some ({ Payload = Some (TyApply(nominalInfo, [])); Narrowed = None }, state)
        | _ -> None

/// Whether `ty` is the type `target` that a match narrows: the same variable, or the same type the checker never learns.
let private isTarget (target: Ty) (ty: Ty) =
    match target, ty with
    | TyVar t, TyVar v -> t.Id = v.Id
    | _ -> target = ty

/// `ty` with `target` written as `narrowed` wherever it is.
let rec private narrowIn (target: Ty) (narrowed: Ty) (ty: Ty) : Ty =
    if isTarget target ty then narrowed else Ty.map (narrowIn target narrowed) ty

/// Whether `ty` holds `target` somewhere.
let rec private holds (target: Ty) (ty: Ty) = isTarget target ty || Ty.children ty |> List.exists (holds target)

/// `env` with the names whose types hold `target` given them with it written as `narrowed`. Only a name whose scheme
/// has free variables can hold the type a match narrows, which is one of the function's own variables or a type the
/// checker never learns that a variable was solved to.
let private narrowEnv (state: InferState) (target: Ty) (narrowed: Ty) (env: TypeEnv) : TypeEnv =
    env.Unquantified
    |> Map.fold (fun (current: TypeEnv) name _ ->
        match TypeEnv.tryFind name current with
        | Some scheme ->
            let resolved = resolveSoFar state scheme.Type
            if holds target resolved then TypeEnv.extend name { scheme with Type = narrowIn target narrowed resolved } current
            else current
        | None -> current) env

/// `state` with the type the match that ends `body`, a lambda's, gives expected to be `expected`, the lambda's
/// declared result, so that its arms are checked against it narrowed (see `infer`), and so the record or tuple literal
/// that ends it, so that its fields are checked against the record's (see `ExpectedLiterals.intoRecordFields`), and the
/// square-bracket literal that ends it, so that it builds the result's type (see `ExpectedLiterals.expectLiteralResult`).
let rec expectResult (body: Expression) (expected: Ty) (state: InferState) : InferState =
    match body.Kind with
    | Match _
    | RecordExpr _
    | TupleExpr _ -> ExpectedLiterals.expect expected state body
    | Block statements ->
        match SyntaxTree.blockValue statements with
        | Some last -> expectResult last expected state
        | None -> state
    | _ -> ExpectedLiterals.expectLiteralResult body expected state

/// The error for a pattern of a `Type` match that isn't one of its description's tags.
let private wrongPattern (span: SourceSpan) (detail: string) =
    Diagnostics.at DiagnosticCodes.TypeDescriptionPattern span
        $"{detail}: a match on a `Type` is matched by the tags of the type's description, such as `#int`, `#list(elem)` or `#record(r)`, or `else`"

/// What the pattern `pattern`, of the payload of the type `ty`, binds, in `env` and `state`, for the arm whose body is
/// `armBody`. A payload is matched by a name or `_`, and a map's by two.
let rec private bindPayload (armBody: Expression) (pattern: Pattern) (ty: Ty) (env: TypeEnv, state: InferState) =
    match pattern.Kind, ty with
    | WildcardPattern, _ -> Ok (env, state)
    | IdentifierPattern name, _ ->
        let symbol, declared = declareSymbol ArmBody armBody.Id name PatternVariable false (Some armBody.Span.End) state
        Ok (TypeEnv.declare name symbol (TypeEnv.mono ty) env, recordSymbolType symbol ty declared)
    | TuplePattern(items, false), TyRecord(fields, _) when items.Length = fields.Count ->
        List.zip items (List.init items.Length (fun index -> fields.[TypeResolution.tupleFieldName index]))
        |> List.fold (fun result (item, itemTy) -> result |> Result.bind (bindPayload armBody item itemTy)) (Ok (env, state))
    | _ -> Error [ wrongPattern pattern.Span "A description's payload is matched by a name or `_`" ]

/// The match `expr` on `scrutinee`, a `Type(standsFor)`, with `arms`, each checked by `inferExpr`. Where `standsFor` is
/// a type variable, or a type the checker never learns, an arm whose tag says what it is narrows it (see the module's
/// summary). The match gives the type it is expected to give, if it is in a place that says (see `expectResult`), and
/// otherwise what its arms give. A match that leaves out a tag and has no `else` is the usual error.
let infer
    (inferExpr: TypeEnv -> InferState -> Expression -> Result<TypedExpr * InferState, Diagnostic list>)
    (env: TypeEnv)
    (state: InferState)
    (expr: Expression)
    (scrutinees: Expression list)
    (standsFor: Ty)
    (arms: MatchArm list)
    : Result<TypedExpr * InferState, Diagnostic list> =
    let target =
        match resolveSoFar state standsFor with
        | TyVar _
        | TypeDescriptions.Skolem _ as target -> Some target
        | _ -> None
    let expected = ExpectedLiterals.expectedOrBlockResultOf state expr
    let resultTy, state =
        match expected with
        | Some expected -> expected, state
        | None -> freshVar state
    let scrutineeTy = ofType standsFor
    let arm (result: Result<TypedMatchArm list * Set<string> * bool * InferState, Diagnostic list>) ((patterns, body): MatchArm) =
        result
        |> Result.bind (fun (typedArms, covered, catchAll, current) ->
            let pattern = List.head patterns
            let found =
                match pattern.Kind with
                | WildcardPattern
                | ElsePattern -> Ok (None, env, current, true, None)
                | IdentifierPattern _ ->
                    bindPayload body pattern scrutineeTy (env, current) |> Result.map (fun (env, next) -> None, env, next, true, None)
                | TagPattern(tag, payload) ->
                    match caseOf current standsFor tag, payload with
                    | None, _ -> Error [ wrongPattern pattern.Span $"'#{tag}' is not a tag of a type's description" ]
                    | Some ({ Payload = None }, _), Some written ->
                        Error [ wrongPattern written.Span $"'#{tag}' has no payload" ]
                    | Some ({ Payload = Some _ }, _), None ->
                        Error [ wrongPattern pattern.Span $"'#{tag}' has a payload: match it as '#{tag}(...)'" ]
                    | Some (case, next), written ->
                        let bound =
                            match written, case.Payload with
                            | Some written, Some payloadTy -> bindPayload body written payloadTy (env, next)
                            | _ -> Ok (env, next)
                        bound |> Result.map (fun (env, next) -> Some tag, env, next, false, Some case)
                | _ -> Error [ wrongPattern pattern.Span "This pattern can't match a type's description" ]
            found
            |> Result.bind (fun (tag, armEnv, next, isCatchAll, case) ->
                let narrowed = case |> Option.bind _.Narrowed
                // What the arm says the type is, kept at its pattern for the backend, which picks the arm where the
                // type is known (see `CoreIrLowerTypeMatch`).
                let next =
                    match narrowed with
                    | Some ty -> { next with ExprTypes = next.ExprTypes |> Map.add pattern.Id ty }
                    | None -> next
                // The arm sees what is in scope, and gives what the match gives, with the type narrowed in them.
                let armEnv, armResult =
                    match target, narrowed with
                    | Some target, Some narrowed ->
                        narrowEnv next target narrowed armEnv, narrowIn target narrowed (resolveSoFar next resultTy)
                    | _ -> armEnv, resultTy
                let next = ExpectedLiterals.expect armResult next body
                // The types of the elements the arm's tag stands for are only known in the arm.
                let opened = case |> Option.bind _.Payload |> Option.map TypeDescriptions.skolemsIn |> Option.defaultValue []
                inferExpr armEnv next body
                |> Result.bind (fun (typedBody, next) ->
                    let next = Visibility.addDeclaredConstraintAt body.Span typedBody.Type armResult next
                    match TypeDescriptions.escaping next opened resultTy body.Span with
                    | Some error -> Error [ error ]
                    | None -> Ok (typedBody, next))
                |> Result.map (fun (typedBody, next) ->
                    typedArms @ [ [ { Pattern = pattern; Type = scrutineeTy } ], typedBody ],
                    (match tag with Some tag -> covered.Add tag | None -> covered),
                    catchAll || isCatchAll,
                    next)))
    match arms |> List.tryFind (fun (patterns, _) -> patterns.Length <> 1) with
    | Some (patterns, _) -> Error [ wrongPattern (List.head patterns).Span "A match on a `Type` matches one value" ]
    | None ->
        arms
        |> List.fold arm (Ok ([], Set.empty, false, state))
        |> Result.map (fun (typedArms, covered, catchAll, current) ->
            let missing = descriptionTags |> List.filter (covered.Contains >> not)
            let current =
                if catchAll || missing.IsEmpty then current
                else
                    let listed = missing |> List.map (sprintf "#%s") |> String.concat ", "
                    let error =
                        Diagnostics.at DiagnosticCodes.NonExhaustiveMatch (MatchHead.span expr scrutinees arms)
                            $"The type's description may be {listed}: add arms for them, or `else`"
                    { current with UnhandledMatches = (expr.Id, [ error ]) :: current.UnhandledMatches }
            mkTypedExpr expr resultTy None None (Some typedArms), current)
