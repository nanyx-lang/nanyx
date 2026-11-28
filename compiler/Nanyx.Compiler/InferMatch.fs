/// `match` expressions: the scrutinees, each arm's patterns and guard, and the arms' values. `Infer` passes in the
/// functions of its recursive chain that a match calls back into, as `Loops` takes `inferExpr`.
module internal NyxCompiler.InferMatch

open System
open Parser.Program
open NyxCompiler
open NyxCompiler.InferState
open NyxCompiler.Visibility
open NyxCompiler.BranchJoin
open NyxCompiler.TypeResolution
open NyxCompiler.Contexts
open NyxCompiler.ContextInstances

/// A `match` of `scrutinees` with `arms`.
let internal inferMatch
    (inferExpr: TypeEnv -> InferState -> Expression -> Result<TypedExpr * InferState, Diagnostic list>)
    (inferAll: TypeEnv -> InferState -> Expression list -> Result<TypedExpr list * InferState, Diagnostic list>)
    (inferLambdaWithExpected: TypeEnv -> InferState -> Expression -> (Identifier * TypeExpr option) list -> Expression -> Ty option -> Ty option -> Result<TypedExpr * InferState, Diagnostic list>)
    (env: TypeEnv) (state: InferState) (expr: Expression) (scrutinees: Expression list) (arms: MatchArm list) : Result<TypedExpr * InferState, Diagnostic list> =
    let scrutineesInferred = inferAll env state scrutinees
    // Each arm has a pattern for each value matched (`desugar` has made a lone `else` or `_` one per value, and a match
    // with an arm of one pattern for all the values one on their tuple, see `PatternSpace.ofMatch`).
    let wrongPatternCounts =
        let count n noun = if n = 1 then $"1 {noun}" else $"{n} {noun}s"
        arms
        |> List.choose (fun (patterns, _) ->
            if patterns.Length = scrutinees.Length then
                None
            else
                let span =
                    match patterns with
                    | [] -> expr.Span
                    | _ -> SourceSpan.between (List.head patterns).Span (List.last patterns).Span
                let written = count patterns.Length "pattern"
                let values = count scrutinees.Length "value"
                Some (
                    Diagnostics.at
                        DiagnosticCodes.WrongPatternCount
                        span
                        $"This arm has {written}, but the match is on {values}: an arm needs a pattern for each value, or one pattern for all of them"))
    match scrutineesInferred with
    | Error scrutineeErrors -> Error scrutineeErrors
    | Ok ([ scrutinee ], afterScrutinee) when (match resolveSoFar afterScrutinee scrutinee.Type with TypeValues.TypeValueType _ -> true | _ -> false) ->
        let standsFor = match resolveSoFar afterScrutinee scrutinee.Type with TypeValues.TypeValueType ty -> ty | other -> other
        TypeMatches.infer inferExpr env afterScrutinee expr scrutinees standsFor arms
    | Ok _ when not wrongPatternCounts.IsEmpty -> Error wrongPatternCounts
    | Ok (scrutineeExprs, afterScrutinees) ->
        let mutable current = afterScrutinees
        let scrutineeTypes = scrutineeExprs |> List.map (fun typedExpr -> typedExpr.Type)
        let mkTypedPattern pattern ty = { Pattern = pattern; Type = ty }
        let rec tryRecordFields ty =
            match ty with
            | TyRecord(fields, _) -> Some fields
            | RecursiveNominals.Nominal(_, _, underlying, _)
            | TyAlias(_, _, underlying) -> tryRecordFields underlying
            | _ -> None
        // The variables a pattern binds are declared by the body of its arm, `armBody`, and are in scope until
        // its end. As with `inferExpr`, the constraints inferring `pattern` adds are reported at `pattern`.
        let rec inferPattern (armBody: Expression) (env': TypeEnv) (state': InferState) (scrutineeTy: Ty) (pattern: Pattern) =
            let outer = state'.Origin
            let origin = if SourceSpan.isNone pattern.Span then outer else pattern.Span
            inferPatternKind armBody env' { state' with Origin = origin } scrutineeTy pattern
            |> Result.map (fun (nextEnv, next, typedPattern) -> nextEnv, { next with Origin = outer }, typedPattern)

        and inferPatternKind (armBody: Expression) (env': TypeEnv) (state': InferState) (scrutineeTy: Ty) (pattern: Pattern) =
            match pattern.Kind with
            | WildcardPattern
            | ElsePattern -> Ok (env', state', mkTypedPattern pattern scrutineeTy)
            // `()` matches the unit value, so what it is matched against is unit.
            | UnitPattern -> Ok (env', addConstraint scrutineeTy (TyPrimitive "unit") state', mkTypedPattern pattern scrutineeTy)
            // One `CustomPatterns` left names no pattern, which is reported: its payload binds its names all the same.
            | ArgumentTagPattern(_, _, payload) ->
                let payloadTy, next = freshVar state'
                payload |> Option.map (inferPattern armBody env' next payloadTy) |> Option.defaultValue (Ok (env', next, mkTypedPattern pattern scrutineeTy))
                |> Result.map (fun (nextEnv, nextState, _) -> nextEnv, nextState, mkTypedPattern pattern scrutineeTy)
            | IdentifierPattern name ->
                let symbol, declared = declareSymbol ArmBody armBody.Id name PatternVariable false (Some armBody.Span.End) state'
                Ok (TypeEnv.declare name symbol (TypeEnv.mono scrutineeTy) env', recordSymbolType symbol scrutineeTy declared, mkTypedPattern pattern scrutineeTy)
            // A string literal matching a `char` is one (see `CharLiterals`).
            | LiteralPattern(StringLit text) when CharLiterals.isChar state' scrutineeTy ->
                CharLiterals.typeOf pattern.Span text
                |> Result.map (fun ty -> env', addConstraint scrutineeTy ty state', mkTypedPattern pattern scrutineeTy)
            | LiteralPattern literal ->
                let ty, recorded = NumericLiterals.typeOf state' pattern.Span None literal
                let constrained = addConstraint scrutineeTy ty recorded
                Ok (env', constrained, mkTypedPattern pattern scrutineeTy)
            // A predicate on the value matched. It sees what is in scope at the match, not what its own arm binds:
            // an arm's names are bound only once all its patterns have matched.
            | GuardPattern predicate ->
                match predicate.Kind with
                | Lambda([ argument ], body, _) ->
                    inferLambdaWithExpected env state' predicate [ argument ] body (Some scrutineeTy) (Some (primitiveType "bool"))
                    |> recordExprType
                    |> Result.map (fun (_, nextState) -> env', nextState, mkTypedPattern pattern scrutineeTy)
                | _ ->
                    Error [
                        Diagnostics.at
                            DiagnosticCodes.GuardNotAPredicate
                            predicate.Span
                            "A guard pattern is a predicate on the value matched, a lambda of one parameter such as `{ < 0 }` or `{ x -> x % 2 == 0 }`" ]
            // Every part matches the same value, and the names each binds are in scope after it.
            | AndPattern parts ->
                let repeated =
                    parts
                    |> List.collect (Pattern.boundNames >> List.distinct)
                    |> List.countBy id
                    |> List.filter (fun (_, count) -> count > 1)
                    |> List.map fst
                match repeated with
                | name :: _ ->
                    Error [
                        Diagnostics.at
                            DiagnosticCodes.NameBoundTwiceInPattern
                            pattern.Span
                            $"More than one part of this and-pattern binds it: bind it in one part only"
                      |> Diagnostics.about name ]
                | [] ->
                    let step result part =
                        result
                        |> Result.bind (fun (partEnv, partState) ->
                            inferPattern armBody partEnv partState scrutineeTy part
                            |> Result.map (fun (nextEnv, nextState, _) -> nextEnv, nextState))
                    List.fold step (Ok (env', state')) parts
                    |> Result.map (fun (nextEnv, nextState) -> nextEnv, nextState, mkTypedPattern pattern scrutineeTy)
            | RangePattern(startExpr, endExpr) ->
                match inferExpr env' state' startExpr with
                | Error err -> Error err
                | Ok (startTyped, nextState) ->
                    match inferExpr env' nextState endExpr with
                    | Error err -> Error err
                    | Ok (endTyped, finalState) ->
                        let constrained =
                            finalState
                            |> addConstraint scrutineeTy startTyped.Type
                            |> addConstraint startTyped.Type endTyped.Type
                        Ok (env', constrained, mkTypedPattern pattern scrutineeTy)
            | TuplePattern(patterns, hasRest) ->
                let itemTypes, nextState = freshVars patterns state'

                // A name alone matches the field it names, where the value is already known to be a record with
                // one; the other elements match fields by position.
                let knownFields =
                    if patterns |> List.exists (function Kind (IdentifierPattern _) -> true | _ -> false) then
                        recordFieldsSoFar state' scrutineeTy |> Option.map (Map.keys >> Set.ofSeq)
                    else None
                let matched =
                    let fields = knownFields |> Option.defaultValue Set.empty
                    List.zip patterns itemTypes |> List.mapi (fun index (pat, ty) -> Pattern.tupleElementField fields index pat, ty)
                // A record type is closed, so the pattern matches every field, unless it ends in `...`.
                let tupleRecord, nextState =
                    if hasRest then openRecordWith (Map.ofList matched) nextState
                    else
                        match knownFields with
                        | Some _ -> TyRecord(Map.ofList matched, FieldOrder.ofList matched), nextState
                        | None -> tupleRecordType itemTypes, nextState
                let constrained = addConstraint scrutineeTy tupleRecord nextState
                inferPatterns armBody env' constrained (List.zip patterns itemTypes)
                |> Result.map (fun (innerEnv, innerState, _) -> innerEnv, innerState, mkTypedPattern pattern tupleRecord)
            | TagPattern(tagName, payloadOpt) ->
                match payloadOpt with
                | None ->
                    let constrained = addAssignableConstraint (TyTag(tagName, None)) scrutineeTy state'
                    Ok (env', constrained, mkTypedPattern pattern scrutineeTy)
                | Some payload ->
                    // Where the scrutinee is a union that names the tag, as the arms' union does (`ArmUnion`), the
                    // payload pattern matches its payload. A fresh variable would leave the tags a nested pattern
                    // gives it to be solved after the scrutinee's own type reaches it, so two of them would meet
                    // each other first (#1808).
                    let payloadTy, nextState =
                        match Unifier.payloadOfTag tagName scrutineeTy with
                        | Some payloadTy -> payloadTy, state'
                        | None -> freshVar state'
                    let constrained = addAssignableConstraint (TyTag(tagName, Some payloadTy)) scrutineeTy nextState
                    match inferPattern armBody env' constrained payloadTy payload with
                    | Ok (nextEnv, nextState, _) ->
                        Ok (nextEnv, nextState, mkTypedPattern pattern scrutineeTy)
                    | Error err -> Error err
            | ListPattern(patterns, splatOpt) ->
                let elementTy, nextState = freshVar state'
                let constrained = addConstraint scrutineeTy (listType elementTy) nextState
                inferPatterns armBody env' constrained (patterns |> List.map (fun pat -> pat, elementTy))
                |> Result.map (fun (innerEnv, innerState, _) ->
                    match splatOpt with
                    | Some (Kind (IdentifierPattern name)) ->
                        let symbol, declared = declareSymbol ArmBody armBody.Id name PatternVariable false (Some armBody.Span.End) innerState
                        TypeEnv.declare name symbol (TypeEnv.mono (listType elementTy)) innerEnv,
                        recordSymbolType symbol (listType elementTy) declared,
                        mkTypedPattern pattern scrutineeTy
                    | _ -> innerEnv, innerState, mkTypedPattern pattern scrutineeTy)
            | ListSplatMiddle(prefix, suffix) ->
                let elementTy, nextState = freshVar state'
                let constrained = addConstraint scrutineeTy (listType elementTy) nextState
                inferPatterns armBody env' constrained (prefix @ suffix |> List.map (fun pat -> pat, elementTy))
                |> Result.map (fun (innerEnv, innerState, _) -> innerEnv, innerState, mkTypedPattern pattern scrutineeTy)
            // `Email(raw)` takes a nominal value apart: the patterns in its brackets match the value's shape (see
            // `Pattern.constructorPayload`). A private type's shape is hidden outside its home module, so only that
            // module can look inside one.
            | RecordPattern(typeName, patterns) when (tryFindTypeDef typeName state' |> Option.exists isNominal) ->
                let info = (tryFindTypeDef typeName state').Value
                let isolation = blockIsolationAt state' typeName info.Symbol pattern.Span
                if not isolation.IsEmpty then
                    Error isolation
                elif info.IsPrivate && not (inNominalHome state' (TypeDefInfo.nominalId info) pattern.Span) then
                    Error [ Diagnostics.at DiagnosticCodes.NominalConstructedOutsideItsModule pattern.Span $"A private type can only be taken apart in the module that defines it: use a function that module exports instead" |> Diagnostics.about typeName ]
                else
                    let nominalTy, underlying, _, instantiated = freshNominalType state' info
                    let constrained = addConstraint scrutineeTy nominalTy instantiated
                    match inferPattern armBody env' constrained underlying (Pattern.constructorPayload pattern patterns) with
                    | Ok (nextEnv, nextState, _) -> Ok (nextEnv, nextState, mkTypedPattern pattern scrutineeTy)
                    | Error err -> Error err
            // `Point(x, y)` on a type that names a shape: the value is that shape, which the patterns match as they
            // match a nominal type's.
            | RecordPattern(typeName, patterns) when (tryFindTypeDef typeName state' |> Option.exists (fun info -> not info.IsContext)) ->
                let info = (tryFindTypeDef typeName state').Value
                let argTys, nextState = freshVars info.Parameters state'
                let shape = Unifier.applyToParameters info.Parameters argTys info.Underlying
                match inferPattern armBody env' (addConstraint scrutineeTy shape nextState) scrutineeTy (Pattern.constructorPayload pattern patterns) with
                | Ok (nextEnv, nextState, _) -> Ok (nextEnv, nextState, mkTypedPattern pattern scrutineeTy)
                | Error err -> Error err
            | RecordPattern(typeName, patterns) ->
                let itemTypes, nextState = freshVars patterns state'

                let anyRecord, nextState = openRecordWith Map.empty nextState
                let constrained = addConstraint scrutineeTy anyRecord nextState
                inferPatterns armBody env' constrained (List.zip patterns itemTypes)
                |> Result.map (fun (innerEnv, innerState, _) -> innerEnv, innerState, mkTypedPattern pattern scrutineeTy)
            | RecordMemberPattern _ when (ProtectedShapes.hiddenRepresentation state' pattern.Span (resolveSoFar state' scrutineeTy)).IsSome ->
                let nominal = (ProtectedShapes.hiddenRepresentation state' pattern.Span (resolveSoFar state' scrutineeTy)).Value
                Error [ ProtectedShapes.takenApartOutside nominal pattern.Span ]
            | RecordMemberPattern(members, hasRest) ->
                let mutable innerEnv = env'
                let mutable innerState = state'
                let mutable innerErrors: Diagnostic list = []
                // A value whose type is still a variable, such as a list pattern's element, may already be known
                // to be a record from the constraints so far; its fields' types then say what each member matches,
                // so two elements' `(status = #failed(msg))` and `(status = #some(v))` both match a `#some(v) | #failed(msg)`.
                let knownFields =
                    match scrutineeTy with
                    | TyVar _ -> recordFieldsSoFar state' scrutineeTy
                    | _ -> tryRecordFields scrutineeTy
                let fieldHints = knownFields |> Option.defaultValue Map.empty
                let mutable fieldMap = Map.empty
                for (name, pat) in members do
                    let memberTy, nextState =
                        match fieldHints |> Map.tryFind name with
                        | Some ty -> ty, innerState
                        | None -> freshVar innerState
                    innerState <- nextState
                    match inferPattern armBody innerEnv innerState memberTy pat with
                    | Ok (nextEnv, nextState, _) ->
                        innerEnv <- nextEnv
                        innerState <- nextState
                        fieldMap <- fieldMap.Add(name, memberTy)
                    | Error err -> innerErrors <- innerErrors @ err
                if innerErrors.IsEmpty then
                    // A record type is closed, so the pattern names every field, unless it ends in `...`. Where
                    // the fields are known, they are checked rather than the value's type, which may be nominal.
                    let patternRecord, next =
                        if hasRest then openRecordWith fieldMap innerState else TyRecord(fieldMap, FieldOrder.ofList members), innerState
                    let matched = knownFields |> Option.map (fun fields -> TyRecord(fields, FieldOrder.none)) |> Option.defaultValue scrutineeTy
                    Ok (innerEnv, addConstraint matched patternRecord next, mkTypedPattern pattern scrutineeTy)
                else
                    Error innerErrors

        /// `inferPattern` for each pattern against its type in turn, each in the env and state the one before it
        /// left: what they bind and their typed patterns, or every error any of them reported.
        and inferPatterns (armBody: Expression) (env': TypeEnv) (state': InferState) (pairs: (Pattern * Ty) list) =
            let innerEnv, innerState, typed, errors =
                pairs
                |> List.fold (fun (innerEnv, innerState, typed, errors) (pat, ty) ->
                    match inferPattern armBody innerEnv innerState ty pat with
                    | Ok (nextEnv, nextState, typedPattern) -> nextEnv, nextState, typedPattern :: typed, errors
                    | Error err -> innerEnv, innerState, typed, errors @ err) (env', state', [], [])
            if errors.IsEmpty then Ok (innerEnv, innerState, List.rev typed) else Error errors

        let mutable armExprs: TypedExpr list = []
        let mutable typedArms: TypedMatchArm list = []
        let mutable errors: Diagnostic list = []
        let patternsForIndex index =
            arms
            |> List.choose (fun (patterns, _) ->
                if index < patterns.Length then Some patterns.[index] else None)
        // For the exhaustiveness check, the scrutinees' types are resolved before the arms add their constraints.
        let resolvedScrutineeTypes = ScrutineeRests.resolve env current scrutineeTypes
        // A tag whose payload only `undefined` has been given for has no values, as it won't once the def is generalised
        // (see `LowerBounds`), so the match needs no arm for it.
        let missingWitnesses =
            PatternSpace.missingCase (arms |> List.map fst) (resolvedScrutineeTypes |> List.map (LowerBounds.asSettled current >> Some))
        // An arm for a tag the scrutinee's closed union doesn't have can never match: it is reported, and the arms
        // are not typed, so the tag it names isn't added to the union as well.
        errors <- UnknownTagArms.ofArms resolvedScrutineeTypes arms
        let mutable scrutineeOverrides: Map<int, Ty> = Map.empty
        for (index, scrutineeTy) in scrutineeTypes |> List.indexed do
            // The arms describe the scrutinee's union, payloads included (`ArmUnion`). With a catch-all arm they
            // need not name every tag, so the union is left open with a rest variable for the tags the arms do not
            // name (`if #some(v) = get(n) then ...` must accept a `#some(int) | #none`). Where the arms leave out
            // a value of the scrutinee, the match is reported as non-exhaustive, so the union is left open too:
            // closed, it would narrow a call's result to the tags the arms name and report the call's argument
            // as the mistake (#681).
            let leavesValueOut =
                missingWitnesses
                |> Option.bind (List.tryItem index)
                |> Option.exists (fun witness -> witness <> PatternSpace.Anything)
            match ArmUnion.describe leavesValueOut (patternsForIndex index) current with
            | Some (unionTy, stateForUnion) ->
                scrutineeOverrides <- scrutineeOverrides |> Map.add index unionTy
                current <- addConstraint scrutineeTy unionTy stateForUnion
            | None -> ()
        // What is wrong with the match as a whole is reported at its head (`match a`), not over its arms.
        let headSpan = MatchHead.span expr scrutinees arms
        // A tag that an earlier arm handles in full (its payload and every other position are catch-alls)
        // cannot reach a later arm, so a name bound by a catch-all in a later arm has the scrutinee's union
        // without those tags; with only the rest left, it has the rest's type. That is what lets
        // `| #some(a), f -> #some(f(a)) | other, _ -> other` be `(#some(a) | r), (a -> b) -> (#some(b) | r)`.
        let tagsHandledBefore armIndex index =
            arms
            |> List.take armIndex
            |> List.choose (fun (patterns, _) -> PatternSpace.tagHandled patterns index)
            |> Set.ofList
        let restRowTy index =
            match scrutineeOverrides |> Map.tryFind index with
            | Some (TyUnion items) -> items |> List.tryPick (function TyVar _ as rest -> Some rest | _ -> None)
            | _ -> None
        let catchAllBindingTy armIndex index =
            match scrutineeOverrides |> Map.tryFind index, restRowTy index with
            | Some (TyUnion items), Some _ ->
                let handled = tagsHandledBefore armIndex index
                if handled.IsEmpty then
                    None
                else
                    match items |> List.filter (function TyTag(name, _) -> not (handled.Contains name) | _ -> true) with
                    | [ single ] -> Some single
                    | remaining -> Some (TyUnion remaining)
            | _ -> None
        let mutable rowVars =
            scrutineeTypes
            |> List.indexed
            |> List.choose (fun (index, _) -> restRowTy index)
            |> List.choose (function TyVar v -> Some v.Id | _ -> None)
            |> Set.ofList
        for (armIndex, (patterns, expr)) in List.indexed arms do
            if errors.IsEmpty then
                let effectiveScrutineeTypes =
                    scrutineeTypes
                    |> List.mapi (fun idx ty ->
                        match List.tryItem idx patterns with
                        | Some (Kind (IdentifierPattern _)) when (catchAllBindingTy armIndex idx).IsSome ->
                            (catchAllBindingTy armIndex idx).Value
                        | _ -> scrutineeOverrides |> Map.tryFind idx |> Option.defaultValue ty)
                let patternPairs = List.zip patterns effectiveScrutineeTypes
                match inferPatterns expr env current patternPairs with
                | Error err -> errors <- errors @ err
                | Ok (env', state', typedPatterns) ->
                    // So is a name bound by a catch-all inside a pattern (see `NestedCatchAlls`).
                    let env', nestedRows = NestedCatchAlls.narrow (NestedCatchAlls.tagsHandledBefore arms armIndex) env' state'
                    rowVars <- Set.union rowVars nestedRows
                    match inferExpr env' state' expr with
                    | Ok (typedExpr, next) ->
                        current <- next
                        armExprs <- typedExpr :: armExprs
                        typedArms <- typedArms @ [ (typedPatterns, typedExpr) ]
                    | Error err -> errors <- err
        if errors.IsEmpty then
            let restError = ScrutineeRests.missingCatchAll resolvedScrutineeTypes arms headSpan
            let exhaustivenessErrors =
                if restError.IsSome then
                    []
                else
                    MatchExhaustiveness.ofWitnesses arms (MatchExhaustiveness.patternColumns scrutinees) resolvedScrutineeTypes headSpan missingWitnesses
            let armTypes = armExprs |> List.rev |> List.map (fun typedExpr -> typedExpr.Type)
            let current = OddBranches.note rowVars OddBranches.Arms (List.rev armExprs) current
            let resultTy, constrained = joinBranchTypes rowVars current armTypes
            // A match that does not handle every case is reported, and typed as if it did: a debug build gives it
            // an arm that traps on the cases it leaves out.
            let unhandled = Option.toList restError @ exhaustivenessErrors
            let constrained =
                if unhandled.IsEmpty then WorkflowRests.noteOpenMatch expr.Id arms resolvedScrutineeTypes headSpan constrained
                else { constrained with UnhandledMatches = (expr.Id, unhandled) :: constrained.UnhandledMatches }
            Ok (mkTypedExpr expr resultTy None None (Some typedArms), constrained)
        else
            Error errors
