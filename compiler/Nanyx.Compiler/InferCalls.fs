/// Calls of a function by name, `f(x)`, including the built-ins the parser makes calls of and a call of a map or an
/// array, which is a lookup. `Infer` passes in the functions of its recursive chain that a call calls back into, as
/// `Loops` takes `inferExpr`.
module internal NyxCompiler.InferCalls

open System
open Parser.Program
open NyxCompiler
open NyxCompiler.InferState
open NyxCompiler.Visibility
open NyxCompiler.BranchJoin
open NyxCompiler.TypeResolution
open NyxCompiler.Contexts
open NyxCompiler.ContextInstances

/// The function a call of a value of the type `ty` is read as, when the value is a map or an array, as far as `state`
/// knows yet: `m(k)` is `map.get(m, k)` and `xs(i)` is `array.at(xs, i)`.
let private lookupOf (state: InferState) (ty: Ty) =
    match Unifier.unalias (resolveSoFar state ty) with
    | Maps.MapType _ -> Some Maps.lookup
    | Arrays.ArrayType _ -> Some Arrays.lookup
    | _ -> None

/// `expr`, a call of the map or array `collection` with `args`: a lookup, read as the call of `lookupName`,
/// `map.get(m, args...)` or `array.at(xs, args...)` (see `lookupOf`), and recorded in `ReadAs` to be written as it. The
/// call keeps the node of `expr`, which is what its types and the instances it finds are recorded by.
let private inferLookup
    (inferExpr: TypeEnv -> InferState -> Expression -> Result<TypedExpr * InferState, Diagnostic list>)
    (env: TypeEnv) (state: InferState) (expr: Expression) (lookupName: Identifier) (collection: Expression) (spanOpt: SourceSpan option) (args: Expression list) =
    let lookup = { expr with Kind = FunctionCall(lookupName, spanOpt, collection :: args) }
    inferExpr env state lookup
    |> Result.map (fun (typed, next) -> { typed with Expr = expr }, { next with ReadAs = next.ReadAs |> Map.add expr.Id lookup })

/// A call of the function `name`, its name at `spanOpt`, with `args`.
/// `a..b` is the prelude's `seq.range(a, b)`, and `a..`, which has no end, its `seq.from(a)`: each a sequence of the
/// bounds' type, which needs its `Enumerable` (see `Seqs`). A range of `int`s is read instead as the call of the prelude's
/// counting function, which is passed no instances. The bounds are inferred once, and each call is checked with them
/// by name (see `RangeSyntax.bound`): inferred again inside the call to the counting function, a range in a range's
/// bound was inferred twice at each level it was nested (#2307).
let private inferRange
    (inferExpr: TypeEnv -> InferState -> Expression -> Result<TypedExpr * InferState, Diagnostic list>)
    (env: TypeEnv) (state: InferState) (expr: Expression) (bounds: Expression list) : Result<TypedExpr * InferState, Diagnostic list> =
    let callee, intCallee = if bounds.Length = 1 then Seqs.fromFunction, Seqs.intFromFunction else Seqs.rangeFunction, Seqs.intRangeFunction
    let inferBounds =
        bounds
        |> List.fold (fun result bound ->
            result |> Result.bind (fun (typedBounds, current) ->
                inferExpr env current bound |> Result.map (fun (typed, next) -> typedBounds @ [ typed ], next))) (Ok ([], state))
    match inferBounds with
    | Error err -> Error err
    | Ok (typedBounds, afterBounds) ->
        let names = typedBounds |> List.mapi (fun index typed -> RangeSyntax.bound index, typed.Type)
        let boundEnv = names |> List.fold (fun current (name, ty) -> TypeEnv.extend name (TypeEnv.mono ty) current) env
        let named = List.map2 (fun (bound: Expression) (name, _) -> Expression.from bound (IdentifierExpr name)) bounds names
        // The call is written as it is read, with the bounds themselves (see `ExpressionRewrites.writeAsRead`).
        let call callee =
            let read = { expr with Kind = FunctionCall(callee, None, bounds) }
            inferAsCall (inferExpr boundEnv) afterBounds expr callee named
            |> Result.map (fun (typed, next) ->
                { typed with Expr = read; Arguments = Some typedBounds }, { next with ReadAs = next.ReadAs |> Map.add expr.Id read })
        match call callee with
        | Ok (typed, next) ->
            match Unifier.unalias (resolveSoFar next typed.Type) with
            | Seqs.SeqType (TyPrimitive "int") -> call intCallee
            | _ -> Ok (typed, next)
        | Error err -> Error err

let rec internal inferCall
    (inferExpr: TypeEnv -> InferState -> Expression -> Result<TypedExpr * InferState, Diagnostic list>)
    (inferLambdaWithExpected: TypeEnv -> InferState -> Expression -> (Identifier * TypeExpr option) list -> Expression -> Ty option -> Ty option -> Result<TypedExpr * InferState, Diagnostic list>)
    (env: TypeEnv) (state: InferState) (expr: Expression) (name: string) (spanOpt: SourceSpan option) (args: Expression list) : Result<TypedExpr * InferState, Diagnostic list> =
    let calleeSpan = SyntaxTree.nameSpan spanOpt expr
    if name = DbgSyntax.builtIn && (TypeEnv.tryFind name env).IsNone then
        match args with
        | [single] ->
            match inferExpr env state single with
            | Ok (typedArg, next) -> Ok (mkTypedExpr expr typedArg.Type None None None, next)
            | Error err -> Error err
        | _ -> Error [ Diagnostics.at DiagnosticCodes.InvalidBuiltInCall calleeSpan $"`dbg` takes one argument, not {args.Length}" ]
    elif name = AssertSyntax.builtIn && (TypeEnv.tryFind name env).IsNone then
        // `assert(condition)`: a `bool`, and nothing comes of it but the crash when it is false (see `Asserts`).
        match args with
        | [ condition ] ->
            match inferExpr env state condition with
            | Ok (typedCondition, next) ->
                let constrained = addConstraintAt condition.Span typedCondition.Type (TyPrimitive "bool") next
                Ok (mkTypedExpr expr (TyPrimitive "unit") None None None, { constrained with Asserts = constrained.Asserts.Add expr.Id })
            | Error err -> Error err
        | _ -> Error [ Diagnostics.at DiagnosticCodes.InvalidBuiltInCall calleeSpan $"`assert` takes one argument, a `bool`, not {args.Length}" ]
    elif name = NotSyntax.builtIn then
        // The `not` keyword: the parser represents `not x` as a call to "not" with one argument.
        match args with
        | [single] ->
            match inferExpr env state single with
            | Ok (typedArg, next) ->
                let constrained = addConstraint typedArg.Type (TyPrimitive "bool") next
                Ok (mkTypedExpr expr (TyPrimitive "bool") None None None, constrained)
            | Error err -> Error err
        | _ -> Error [ Diagnostics.at DiagnosticCodes.InvalidBuiltInCall expr.Span "`not` takes one operand" ]
    elif name = NegateSyntax.builtIn then
        match args with
        | [ single ] ->
            inferExpr env state single
            |> Result.bind (fun (typedArg, next) ->
                let ty = typedArg.Type
                OperatorCalls.inferNegation (memberOfInstanceInScope env next) (tryResolveAttachedFunction env (resolveSoFar next ty)) env next expr ty (ArithmeticOperators.numericOperandType next ty ty)
                |> Result.map (fun (resultTy, negated) -> mkTypedExpr expr resultTy None None None, negated))
        | _ -> Error [ Diagnostics.at DiagnosticCodes.InvalidBuiltInCall expr.Span "Negation, `-`, takes one operand" ]
    elif name = WithSyntax.builtIn then
        // `record with f = v, ...`: the parser represents a record update as a call to "__with" with the
        // record and a record of the updated fields. The result has the record's type; every updated
        // field must exist on it, at the field's type.
        match args with
        | [ baseExpr; Kind (RecordExpr updates) ] ->
            match inferExpr env state baseExpr with
            | Error err -> Error err
            | Ok (typedBase, afterBase) ->
                // An open record's fields are the ones it lists: its other fields are kept, and can't be updated.
                // The record's type may be known only from constraints not yet applied to it: a payload a match
                // binds of a call's result.
                let baseTy = Unifier.unalias (resolveSoFar afterBase typedBase.Type)
                let updatable =
                    match baseTy with
                    | TyOpenRecord(fields, _) -> Some fields
                    | ty -> recordFieldsOf ty
                match updatable, baseTy with
                | None, _ -> Error [ Diagnostics.at DiagnosticCodes.InvalidRecordUpdate baseExpr.Span "`with` needs a record whose fields are known here" ]
                // An updated value of a nominal type is a new one, and only the type's home module makes those.
                | Some _, TyNominal(nominal, _, _, isPrivate) when not (inNominalHome afterBase nominal baseExpr.Span) ->
                    let message = ProtectedShapes.updatedOutside afterBase (Map.toList env.Names) nominal isPrivate.IsPrivate
                    Error [ Diagnostics.at DiagnosticCodes.NominalConstructedOutsideItsModule baseExpr.Span message ]
                | Some fields, _ ->
                    let mutable current = afterBase
                    let mutable errors: Diagnostic list = []
                    // A field that isn't the record's is reported at its name, with the record's field closest to it.
                    for update in updates do
                        match update with
                        | NamedField(fieldName, valueExpr) ->
                            match fields |> Map.tryFind fieldName with
                            | None ->
                                let nameSpan = declarationSpan current NamedFieldValue valueExpr
                                errors <- errors @ [ NameSuggestions.unknown DiagnosticCodes.UnknownMember nameSpan $"The record has no member '{fieldName}' to update" fields.Keys fieldName ]
                            | Some fieldTy ->
                                match inferExpr env current valueExpr with
                                | Ok (typedValue, next) -> current <- addConstraintAt valueExpr.Span typedValue.Type fieldTy next
                                | Error err -> errors <- errors @ err
                        | PositionalField valueExpr -> errors <- errors @ [ Diagnostics.at DiagnosticCodes.InvalidRecordUpdate valueExpr.Span "This field has no name, and `with` updates fields by name: write `name = value`" ]
                    if errors.IsEmpty then Ok (mkTypedExpr expr typedBase.Type None None None, current)
                    else Error errors
        | _ -> Error [ Diagnostics.at DiagnosticCodes.InvalidBuiltInCall expr.Span "`with` takes a record and the fields to update" ]
    elif name = RangeSyntax.builtIn then
        inferRange inferExpr env state expr args
    elif name = ExternSyntax.builtIn || name = ComponentImportSyntax.builtIn then
        // A foreign function's value (see `ExternSyntax`), or a component import's (see `ComponentImportSyntax`): the
        // embedder provides it, so it is whatever the def's written type says.
        let ty, next = freshVar state
        Ok (mkTypedExpr expr ty None None None, next)
    elif name = RecSyntax.builtIn then
        // `rec name = value`: the value of a self-referencing local def; the block already binds the name.
        match args with
        | [ inner ] -> inferExpr env state inner
        | _ -> Error [ Diagnostics.at DiagnosticCodes.InvalidBuiltInCall expr.Span "`rec` takes one definition" ]
    elif name = MutSyntax.builtIn || name = AssignSyntax.builtIn || name = CellPassingSyntax.argument then
        match expr.Kind with
        | MutSyntax.Call value -> MemoryCells.inferMut inferExpr env state expr value
        | CellPassingSyntax.Argument cell -> MemoryCells.inferArgument inferExpr env state expr cell
        | AssignSyntax.Call(target, value) -> MemoryCells.inferAssign inferExpr env state expr target value
        | _ -> Error [ Diagnostics.at DiagnosticCodes.InvalidBuiltInCall expr.Span $"`{name}` takes a cell and a value" ]
    elif name = ForSyntax.builtIn then
        match expr.Kind with
        | ForSyntax.Call(iterables, lambdaExpr, lambdaArgs, lambdaBody) ->
            Loops.inferFor inferExpr inferLambdaWithExpected env state expr iterables lambdaExpr lambdaArgs lambdaBody
        | _ -> Error [ Diagnostics.at DiagnosticCodes.InvalidBuiltInCall expr.Span "`for` takes an iterable and a loop body" ]
    elif name = ForSyntax.toSeq && (match args with [ iterable ] -> Loops.walksAsSequence inferExpr env state iterable | _ -> false) then
        // A sequence a `for` loop in a sequence's block walks is passed on as it is (see `Loops.walksAsSequence`).
        inferExpr env state { expr with Kind = FunctionCall(ForSyntax.sequenceItself, spanOpt, args) }
        |> Result.map (fun (typed, next) -> typed, { next with PipeTargets = next.PipeTargets |> Map.add expr.Id ForSyntax.sequenceItself })
    elif name = WhileSyntax.builtIn then
        match expr.Kind with
        | WhileSyntax.Call(condition, body) -> Loops.inferWhile inferExpr env state expr condition body
        | _ -> Error [ Diagnostics.at DiagnosticCodes.InvalidBuiltInCall expr.Span "`while` takes a condition and a loop body" ]
    elif name = CollectionSyntax.spread then
        match args with
        | [ operand ] -> CollectionLiterals.inferSpread inferExpr env state expr operand
        | _ -> Error [ Diagnostics.at DiagnosticCodes.InvalidBuiltInCall expr.Span "A spread takes one list or array" ]
    else
    match expr.Kind with
    // `T[...]` of a type with no `fromArray`, reported at the type's name.
    | CollectionSyntax.Construct(typeName, _, _) when (TypeEnv.tryFind name env).IsNone ->
        Error [ CollectionLiterals.noFromArray calleeSpan typeName ]
    | _ ->
    // A call that gives its callee another number of arguments than it takes says so, and is checked no further: what
    // else it reports, such as a context the callee requires, would only be about the wrong call (see `ArgumentCount`).
    // One argument given for several is inferred to see whether it is a tuple of them, and what inferring it found is
    // dropped.
    let typeOfArgument arg = inferExpr env state arg |> Result.toOption |> Option.map (fun (typed, _) -> typed.Type)
    // So is a call of a value that is no function (see `NotAFunction`).
    match TypeEnv.tryFind name env |> Option.bind (fun scheme -> NotAFunction.check state expr name scheme.Type calleeSpan args) with
    | Some error -> Error [ error ]
    | None ->
    let argumentCount =
        TypeEnv.tryFind name env
        |> Option.bind (fun scheme -> ArgumentCount.check state expr name scheme.Type calleeSpan args typeOfArgument |> Option.map (fun error -> scheme, error))
    match argumentCount with
    | Some (scheme, error) ->
        let funcTy, next = instantiate (recordReference expr scheme state) scheme
        match Unifier.unalias funcTy with
        | TyFunc(input, output, _) -> Ok (FailedCalls.result expr input output [ error ] next)
        | _ -> Error [ error ]
    | None ->
    let contextCheck =
        match contextRequirementsOf env state name with
        // A parameterised context is found by its type once the arguments are typed (see `resolveCallInstances`).
        | Some contexts -> ensureContextAvailable (Some expr.Id) true env state name (contexts |> List.filter (isParameterizedContext state >> not)) None calleeSpan
        | None -> Ok state
        |> Result.bind (fun current -> checkContextUse (Some expr.Id) env current (canonicalName env state name) calleeSpan)
    match contextCheck with
    // A call missing a context gives back what its callee's type says (see `FailedCalls`). It is not checked on, as the
    // lambda around it would then use the context too.
    | Error errors ->
        match TypeEnv.tryFind name env |> Option.map (fun scheme -> instantiate (recordReference expr scheme state) scheme) with
        | Some (funcTy, next) ->
            match Unifier.unalias funcTy with
            | TyFunc(input, output, _) -> Ok (FailedCalls.result expr input output errors next)
            | _ -> Error errors
        | None -> Error errors
    | Ok state ->
        // What the callee requires is used where the call runs, by the lambda around it if any (see `LambdaContexts`).
        let state =
            let written = contextsRequiredByCall env state name
            let ofMember = LambdaContexts.contextOfMember env state name |> Option.map (fun ctx -> TyApply(ctx, [])) |> Option.toList
            let state = state |> LambdaContexts.recordWritten env written |> LambdaContexts.recordUnsupplied env ofMember
            // A def not inferred yet, in a cycle with this one, is given a variable for its contexts, bound once they
            // are known.
            match pendingCallContexts env state name calleeSpan with
            | Some (var, next) -> LambdaContexts.record [ var ] next
            | None -> state
        // A callee a module block keeps from here is checked on as if it were reached (see `FailedCalls`).
        let reachErrors = TypeEnv.tryFind name env |> Option.map (fun scheme -> referenceErrors name expr scheme state) |> Option.defaultValue []
        let state = if FailedCalls.calleeKnown reachErrors && not reachErrors.IsEmpty then FailedCalls.failed expr reachErrors state else state
        // The callee's scheme, with the function a call of it is read as if it is a map or an array.
        match TypeEnv.tryFind name env |> Option.map (fun scheme -> scheme, lookupOf state scheme.Type) with
        | Some _ when not (FailedCalls.calleeKnown reachErrors) -> Error reachErrors
        | Some (_, Some lookup) -> inferLookup inferExpr env state expr lookup (Expression.from expr (IdentifierExpr name)) spanOpt args
        | Some (scheme, None) ->
            let mutable current = state
            let funcTy, nextState = instantiate (recordReference expr scheme current |> HiddenKeywords.note env expr name scheme) scheme
            let written = contextRequirementsOf env state name |> Option.defaultValue []
            let env, funcTy, nextState = ScopedCalls.enter env nextState expr name scheme funcTy written
            current <- { nextState with CalleeTypes = nextState.CalleeTypes |> Map.add expr.Id funcTy }

            let recordFieldsOpt = recordFieldsOf funcTy

            let isWorkflowCall =
                match args with
                | [Kind (Lambda(lambdaArgs, _, _))] when lambdaArgs.IsEmpty -> recordFieldsOpt
                | _ -> None

            match isWorkflowCall with
            | Some _ ->
                // A builder's block with no workflow keyword, `proc { ... }` with no `try` or `return`: the
                // workflow rewrite leaves it as a call, since it cannot tell a builder from a function given a
                // trailing lambda, but a builder is a record. The block runs where it is written, as a block
                // with keywords does, and its value is the block's.
                match args with
                | [Kind (Lambda(lambdaArgs, lambdaBody, _)) as lambdaExpr] ->
                    match inferLambdaWithExpected env current lambdaExpr lambdaArgs lambdaBody (Some (TyPrimitive "unit")) None with
                    | Ok (typedLambda, next) ->
                        let returnTy =
                            match Unifier.unalias typedLambda.Type with
                            | TyFunc(_, ret, _) -> ret
                            | other -> other
                        Ok (mkTypedExpr expr returnTy None None None, { LambdaContexts.recordRunHere env typedLambda.Type next with WorkflowBlocks = next.WorkflowBlocks.Add expr.Id })
                    | Error err -> Error err
                | _ -> Error [ Diagnostics.at DiagnosticCodes.InvalidWorkflow calleeSpan $"It is called without a trailing lambda: write its block after it, as in `{name} {{ ... }}`" |> Diagnostics.about name ]
            | None ->
                // With a known signature, each argument is checked against its parameter as it is inferred,
                // so a lambda argument's parameters can take their types from the signature and from the
                // arguments before it: in `customer \map { c -> c.name }`, `c` is already a record when
                // `c.name` is checked.
                let paramTys =
                    match Unifier.unalias funcTy, args with
                    | TyFunc(declaredInput, _, _), [ _ ] -> Some [ declaredInput ]
                    | TyFunc(declaredInput, _, _), _ ->
                        match Unifier.unalias declaredInput with
                        | TyRecord(fields, _) when fields.Count = args.Length ->
                            let tys = args |> List.mapi (fun index _ -> fields |> Map.tryFind (tupleFieldName index))
                            if tys |> List.forall Option.isSome then Some (tys |> List.choose id) else None
                        | _ -> None
                    | _ -> None
                // `f(a, b)` passes its arguments as one tuple, whose items take the callee's parameters' types
                // the same way: a tuple argument with its items and their types.
                let (|TupleOfParameters|_|) (arg: Expression) =
                    match arg, paramTys with
                    | Kind (TupleExpr items), Some [ declaredInput ] ->
                        match Unifier.unalias declaredInput with
                        | TyRecord(fields, _) when fields.Count = items.Length ->
                            let tys = items |> List.mapi (fun index _ -> fields |> Map.tryFind (tupleFieldName index))
                            if tys |> List.forall Option.isSome then Some (items, tys |> List.choose id) else None
                        | _ -> None
                    | _ -> None
                // A lambda can use the contexts its parameter's type requires, which the callee supplies.
                // The instances of parameterised contexts it is given are in its type, so the callee passes them.
                // One whose contexts only the call's result carries is not run by the call (see
                // `Contexts.asCarriedArgument`).
                let inferLambda state lambda lambdaArgs lambdaBody expectedInput paramTy =
                    match EmitLambdas.yieldGivenToEmitter state lambdaBody paramTy with
                    | Some yieldsInstead -> Error [ yieldsInstead ]
                    | None ->
                    let lambdaEnv, state = extendEnvForLambdaArgument env state lambda (Some paramTy)
                    // A square-bracket literal that ends the lambda builds the result its parameter's type gives it.
                    let state =
                        match Unifier.unalias (resolveSoFar state paramTy) with
                        | TyFunc(_, result, _) -> ExpectedLiterals.expectLiteralResult lambdaBody result state
                        | _ -> state
                    let instances = instancesSuppliedToLambda state lambda (Some paramTy)
                    let ownContexts =
                        match contextRequirementsOf env state name |> Option.map ContextVariables.names with
                        | Some vars when not vars.IsEmpty -> ContextVariables.boundAt (resolveSoFar state) vars scheme.Type funcTy
                        | _ -> []
                    let resultTy = match Unifier.unalias funcTy with TyFunc(_, ret, _) -> ret | _ -> TyUnion []
                    state
                    |> Contexts.asCarriedArgument ownContexts resultTy paramTy (fun state ->
                        match expectedInput with
                        | Some expectedInput -> inferLambdaWithExpected lambdaEnv state lambda lambdaArgs lambdaBody (Some expectedInput) None
                        | None -> inferExpr lambdaEnv state lambda)
                    |> Result.map (Contexts.withSuppliedInstances instances lambda)
                // A square-bracket literal builds the type its parameter takes, if it is one with a `fromArray`, and any
                // literal is checked against that type (see `ExpectedLiterals`).
                let inferItem state item paramTy =
                    let state = ExpectedLiterals.expect paramTy state item
                    CollectionLiterals.targeted inferExpr env state item paramTy |> Option.defaultWith (fun () -> inferExpr env state item)
                let argResults =
                    args
                    |> List.mapi (fun index arg ->
                        let paramTy = paramTys |> Option.bind (List.tryItem index)
                        let alone = Result.map (fun inferred -> inferred, None)
                        let inferred =
                            match arg, paramTy with
                            | Kind (Lambda(lambdaArgs, lambdaBody, _)), Some paramTy ->
                                inferLambda current arg lambdaArgs lambdaBody (LambdaArguments.expectedInput current (Some paramTy) arg lambdaArgs) paramTy |> alone
                            | TupleOfParameters(items, itemTys), _ ->
                                LambdaArguments.inferTupleItems inferItem inferLambda current items itemTys
                                |> Result.bind (fun (typedItems, next) ->
                                    Ok (mkTypedExpr arg (tupleRecordType (typedItems |> List.map (fun typedItem -> typedItem.Type))) None None None, next)
                                    |> recordExprType
                                    |> Result.map (fun inferred -> inferred, Some (typedItems, itemTys)))
                            | _, Some paramTy -> inferItem current arg paramTy |> alone
                            | _ -> inferExpr env current arg |> alone
                        match inferred with
                        | Ok ((ty, next), tupleItems) ->
                            // A tuple of the callee's parameters is reported at the argument in it that does not fit
                            // (see `LambdaArguments.reportedSpan`), which is worked out once a mismatch at the tuple is
                            // reported: each call working it out looked at every literal of the def before it (#2552).
                            let next =
                                match paramTy, tupleItems with
                                | Some paramTy, Some (typedItems, itemTys) ->
                                    let reported = lazy (LambdaArguments.reportedSpan next arg.Span typedItems itemTys ty.Type paramTy)
                                    let items = match arg.Kind with TupleExpr items -> items | _ -> []
                                    { next with ArgumentSpans = next.ArgumentSpans |> Map.add arg.Span reported }
                                    |> ParameterTypes.noteItems scheme.Symbol name items
                                | _ -> next
                            current <-
                                match paramTy with
                                | Some paramTy ->
                                    // An argument of the wrong type points back at the type the callee writes for the
                                    // parameter, and a lambda's at the types it writes for its own (#3109).
                                    addAssignableConstraintAt arg.Span ty.Type paramTy next
                                    |> ParameterTypes.noteArgument scheme.Symbol name index args.Length arg paramTy
                                | None -> next
                            Ok (ty, arg.Span)
                        | Error err -> Error err)
                let errors = argResults |> List.choose (function Error err -> Some err | _ -> None) |> List.concat
                if errors.IsEmpty then
                    let typedArgs, argSpans = argResults |> List.choose (function Ok typedArg -> Some typedArg | _ -> None) |> List.unzip
                    let positionalForNamed =
                        match Unifier.unalias (resolveSoFar current funcTy) with
                        | TyFunc(input, _, _) -> NamedArguments.check current expr name input args typedArgs
                        | _ -> None
                    match positionalForNamed, Unifier.unalias funcTy with
                    // What checking the arguments against the callee found is dropped, as it would only report this
                    // error again.
                    | Some diagnostic, TyFunc(input, output, _) -> Ok (FailedCalls.result expr input output [ diagnostic ] state)
                    | Some diagnostic, _ -> Error [ diagnostic ]
                    | None, TypeArguments.LeavesOut current expr name args typedArgs (output, call) -> Ok (TypeArguments.leaveOut expr output call { current with Constraints = state.Constraints })
                    | None, _ ->
                    let argTys = typedArgs |> List.map (fun typedExpr -> typedExpr.Type)
                    let current = WorkflowRests.noteCall name argTys current
                    // A function value requires the contexts its type says where it is called. One whose type is
                    // not known yet, such as a parameter of a def that does not write its type, requires a context
                    // variable of its own (see `ContextVariables`).
                    let calleeContexts, current =
                        match Unifier.unalias funcTy with
                        | TyFunc(_, _, contexts) -> contexts, current
                        | _ -> let var, next = freshVar current in [ var ], next
                    // A local def calling itself from its own lambda requires what the def does, which the lambda
                    // uses already (see `LambdaContexts`).
                    let selfCall = LambdaContexts.isSelfCall funcTy current
                    let current = LambdaContexts.recordSelfCall funcTy calleeContexts current
                    // The callee's own context variables stand for the contexts of the arguments given for them,
                    // and the call requires those too. Both are used by the lambda around the call, if any.
                    let requireUsed (next: InferState) =
                        let ofVariables =
                            match contextRequirementsOf env next name |> Option.map ContextVariables.names with
                            | Some vars when not vars.IsEmpty -> ContextVariables.boundAt (resolveSoFar next) vars scheme.Type funcTy
                            | _ -> []
                        let ofValue = if calleeContexts.IsEmpty || selfCall then [] else ContextVariables.resolved (resolveSoFar next) calleeContexts
                        let used = ofVariables @ ofValue |> List.distinct
                        let requirePlain current ctx = ensureContextAvailable (Some expr.Id) true env current name [ ctx ] None calleeSpan
                        ContextVariables.require requirePlain name calleeSpan used (LambdaContexts.recordUnsupplied env used next)
                    // When the callee's type is already a function (an annotated or already-inferred
                    // def), its return type is the call's type directly, so a member access or match on
                    // the result can see it without waiting for the constraint below to be solved.
                    match Unifier.unalias funcTy, paramTys with
                    // Each argument was checked against its parameter above, which is all a known signature
                    // asks; constraining several arguments together as well, as one record, would make each
                    // equal to its parameter, and undo an argument union's widening into its parameter's.
                    | TyFunc(_, declaredRet, _), Some _ when args.Length > 1 ->
                        requireUsed current |> Result.map (fun next -> { mkTypedExpr expr declaredRet None None None with Arguments = Some typedArgs }, next)
                    | _ ->
                    let retTy, nextState =
                        match Unifier.unalias funcTy with
                        | TyFunc(_, declaredRet, _) -> declaredRet, current
                        | _ -> freshVar current
                    // A known input is what the argument must be assignable to. A fresh variable in its place
                    // would be bound to the argument's own type first (constraints are solved newest first),
                    // and a nominal argument would then have to equal its parameter's shape.
                    let expectedInput, nextState2 =
                        match Unifier.unalias funcTy with
                        | TyFunc(declaredInput, _, _) -> declaredInput, nextState
                        | _ -> freshVar nextState
                    let inputTy = inputTypeFromArgs argTys
                    // A single argument is the call's input: a mismatch is that argument's. So is one with the
                    // callee's type when that is known to be a function, whose result is the call's already.
                    let inputSpan = match argSpans with [ single ] -> single | _ -> SourceSpan.none
                    let funcSpan = if paramTys.IsSome then inputSpan else SourceSpan.none
                    let withFuncConstraint = addConstraintAt funcSpan funcTy (TyFunc(expectedInput, retTy, calleeContexts)) nextState2
                    let withAssignable = addAssignableConstraintAt inputSpan inputTy expectedInput withFuncConstraint
                    requireUsed withAssignable |> Result.map (fun next -> { mkTypedExpr expr retTy None None None with Arguments = Some typedArgs }, next)
                else
                    Error errors
        | None ->
            match tryFindTypeDef name state with
            // A `module name =` block can only make the types it sees.
            | Some info when not (blockIsolationAt state name info.Symbol calleeSpan).IsEmpty ->
                Error (blockIsolationAt state name info.Symbol calleeSpan)
            // Only a nominal type's home module can make one: anywhere else it comes from what that module exports.
            | Some info when isNominal info && not (inNominalHome state (TypeDefInfo.nominalId info) calleeSpan) ->
                let message = ProtectedShapes.constructedOutside state (Map.toList env.Names) name info
                Error [ Diagnostics.at DiagnosticCodes.NominalConstructedOutsideItsModule calleeSpan message |> Diagnostics.about name ]
            // A context's instance is never a value: it is written only as what a `use` loads.
            | Some info when info.IsContext && not (state.UseInstances.Contains expr.Id) ->
                let message =
                    $"A context's instance can only be written in a `use`: to reuse one, write a function that runs a lambda under it, as in `{{ f -> use {name}(...) in f() }}`"
                Error [ Diagnostics.at DiagnosticCodes.InstanceOutsideUse calleeSpan message ]
            | Some info ->
                let madeTy, underlying, instantiated = ScopedTypes.made env state name info
                let mutable current = recordConstructorReference expr info instantiated
                let argResults =
                    args
                    |> List.map (fun arg ->
                        let inferred =
                            match LambdaArguments.namedFields arg underlying with
                            | Some (names, values, memberTys) ->
                                // A constructor does not run the lambdas it is given, so in a value they are inferred
                                // outside it, as a record's are (see `Contexts.asConstructorField`). One given for a
                                // field whose type requires contexts can use them, as a lambda argument can. A
                                // context's constructor is written only in a `use`, whose members run with the
                                // instances there, so a lambda for a member whose contexts are all named is inferred
                                // where the `use` is: what it needs besides them is required there (see
                                // `UseDischarge.fit`, which leaves a member with a context variable as it is too).
                                let inferLambda state lambda lambdaArgs lambdaBody expectedInput fieldTy =
                                    let lambdaEnv, state = Contexts.extendEnvForLambdaArgument env state lambda (Some fieldTy)
                                    let instances = Contexts.instancesSuppliedToLambda state lambda (Some fieldTy)
                                    let infer state expectedInput = inferLambdaWithExpected lambdaEnv state lambda lambdaArgs lambdaBody (Some expectedInput) None
                                    let namesItsContexts =
                                        match Unifier.unalias (resolveSoFar state fieldTy) with
                                        | TyFunc(_, _, declared) -> declared |> List.forall (resolveSoFar state >> function TyVar _ -> false | _ -> true)
                                        | _ -> false
                                    match expectedInput with
                                    | Some expectedInput when info.IsContext && namesItsContexts -> infer state expectedInput
                                    | Some expectedInput -> state |> Contexts.asConstructorField fieldTy (fun state -> infer state expectedInput)
                                    | None -> inferExpr lambdaEnv state lambda
                                    |> Result.map (Contexts.withSuppliedInstances instances lambda)
                                LambdaArguments.inferTupleItems (fun state item _ -> inferExpr env state item) inferLambda current values memberTys
                                |> Result.map (fun (typedValues, next) ->
                                    let fieldTys = List.zip names (typedValues |> List.map _.Type) |> Map.ofList
                                    mkTypedExpr arg (TyRecord(fieldTys, FieldOrder names)) None None None, next)
                                |> recordExprType
                            | None -> inferExpr env current arg
                        match inferred with
                        | Ok (ty, next) ->
                            current <- next
                            Ok ty
                        | Error err -> Error err)
                let errors = argResults |> List.choose (function Error err -> Some err | _ -> None) |> List.concat
                if errors.IsEmpty then
                    let typedArgs = argResults |> List.choose (function Ok typedExpr -> Some typedExpr | _ -> None)
                    match NamedArguments.check current expr name underlying args typedArgs with
                    | Some diagnostic -> Error [ diagnostic ]
                    | None ->
                    let argTys = typedArgs |> List.map (fun typedExpr -> typedExpr.Type)
                    let inputTy, fitted = UseDischarge.fit env current expr underlying args (inputTypeFromArgs argTys)
                    ScopedTypes.requireContext env (addAssignableConstraint inputTy underlying fitted) expr name info calleeSpan
                    |> Result.map (fun next -> mkTypedExpr expr madeTy None None None, next)
                else
                    Error errors
            | None ->
                // `s.contains("x")` is read as a call to a function named `s.contains`. When `s` is a value, the
                // dot was meant to reach a definition attached to its type, which is a pipe's to do.
                let dot = name.LastIndexOf '.'
                let receiver = if dot > 0 then name.Substring(0, dot) else ""
                let field = name.Substring(dot + 1)
                let receiverValue = if dot > 0 && not (receiver.Contains '.') then TypeEnv.tryFind receiver env else None
                // The function a call of the field is read as, if it is a map or an array (see `lookupOf`).
                let fieldLookup (scheme: TypeScheme) =
                    recordFieldsOf (resolveSoFar state scheme.Type) |> Option.bind (Map.tryFind field) |> Option.bind (lookupOf state)
                match receiverValue |> Option.map (fun scheme -> scheme, fieldLookup scheme) with
                // `machine.inventory(name)`, where the field is a map or an array, is a lookup in it.
                | Some (_, Some lookup) ->
                    let collection = Expression.from expr (MemberAccess(Expression.from expr (IdentifierExpr receiver), field, spanOpt))
                    inferLookup inferExpr env state expr lookup collection spanOpt args
                // `statement.setInt(position, value)`, where the field holds a function, calls it (see `FieldCalls`).
                | Some (scheme, None) when FieldCalls.holdsFunction state scheme.Type field || TypeDescriptions.holdsFunction state scheme.Type field ->
                    FieldCalls.infer (inferExpr env) state expr receiver field spanOpt args
                | Some (scheme, None) ->
                    let memberName = name.Substring(dot + 1)
                    let receiverTy = resolveSoFar state scheme.Type
                    let message =
                        match tryResolveAttachedFunction env receiverTy memberName with
                        | Some attached ->
                            $"'{receiver}' is a value, so '{name}' is not a function's name, but '{attached}' is attached to its type: call it with a pipe, as in `{receiver} \\{memberName}(...)`"
                        | None -> $"'{receiver}' is a value, so '{name}' is not a function's name, and '{Unifier.typeText state.TypeDefs receiverTy}' has no '{memberName}' attached"
                    Error [ Diagnostics.at DiagnosticCodes.UnknownFunction calleeSpan message |> Diagnostics.about name ]
                | None ->
                    // A def attached to a context available here, by its bare name.
                    match AttachedContextDefs.qualifiedFor env state name calleeSpan with
                    | Some (Ok qualified) ->
                        inferCall inferExpr inferLambdaWithExpected env (AttachedContextDefs.record expr.Id qualified state) expr qualified spanOpt args
                    | Some (Error errors) -> Error errors
                    | None -> Error [ StandardLibraryImports.unknown state.StandardLibraryExports.Value DiagnosticCodes.UnknownFunction calleeSpan $"'{name}' is not in scope" env.Names.Keys name ]
