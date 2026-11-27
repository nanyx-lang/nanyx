module NyxCompiler.Infer

open System
open Parser.Program
open NyxCompiler

type TypedExpr = NyxCompiler.TypedExpr
type TypedStatement = NyxCompiler.TypedStatement

open NyxCompiler.InferState

type InferState = NyxCompiler.InferState.InferState

let emptyState = NyxCompiler.InferState.emptyState

open NyxCompiler.Visibility
open NyxCompiler.BranchJoin
open NyxCompiler.TypeResolution
open NyxCompiler.Contexts
open NyxCompiler.ContextInstances

let blockImports = NyxCompiler.Visibility.blockImports

let attachedTypeNames = NyxCompiler.TypeResolution.attachedTypeNames

let builtInPropertyNames = NyxCompiler.TypeResolution.builtInPropertyNames

let builtInProperty = NyxCompiler.TypeResolution.builtInProperty

let internal typeExprToTy = NyxCompiler.TypeResolution.typeExprToTy

/// The type of the field `fieldName` of a value of type `ty`, as far as the constraints gathered so far tell. A
/// value of no known type yet is taken to be a record with at least that field, and an open record without it
/// to have it among its others: reading `p.name` makes `p` a `(name: a, ...r)`. `None` when `ty` is known not to
/// have the field.
let private fieldTySoFar (state: InferState) (ty: Ty) (fieldName: string) : (Ty * InferState) option =
    let openWith (row: Ty) (next: InferState) =
        let fieldTy, next = freshVar next
        let record, next = openRecordWith (Map.ofList [ fieldName, fieldTy ]) next
        Some (fieldTy, addConstraint row record next)
    let resolved = resolveSoFar state ty
    match recordFieldsOf resolved with
    | Some fields -> fields |> Map.tryFind fieldName |> Option.map (fun fieldTy -> fieldTy, state)
    | None ->
        match Unifier.unalias resolved with
        | TyVar _ -> openWith resolved state
        | TyOpenRecord(fields, row) ->
            match fields |> Map.tryFind fieldName with
            | Some fieldTy -> Some (fieldTy, state)
            | None -> openWith (TyVar row) state
        | _ -> None

/// A def's type `ty` generalised against `env`, both resolved against the constraints gathered so far. Unresolved,
/// a local bound to a call's result is a type variable, and generalising that would make every later use a fresh,
/// unrelated variable (and let a match on it skip the exhaustiveness check, #61); a variable `env` only has once
/// resolved, as a parameter's `#some(a) | r` does, would be generalised as if nothing else had it.
///
/// Every union rest variable that nothing can add tags to is solved to the empty union first:
/// `#some(5) \mapSome f` is a `#some(string)`, not a `#some(string) | r`. Such a variable is in no function's
/// input (see `Unifier.outputOnlyRestVars`), was not named in an annotation, and is not shared with `env`, where a
/// later constraint could still give it tags. The constraint solving it is added to the state, so the types
/// recorded for the def's expressions lose it too. A rest the unifier made up, as it does when a closed union is
/// given where an open one is expected, may be numbered differently the next time the constraints are solved, so
/// rather than it, each of inference's own variables solved to a type with it is constrained to that type without it.
/// So is every context variable no caller can choose (see `Unifier.outputOnlyContextVars`) solved to no contexts.
/// A variable in a box the value holds is never generalised (see `Boxes.heldVariables`).
///
/// So is a function's result that is a type variable found nowhere else in its type, which no annotation named: no
/// value of it can be made, so a call never gives one back, and the function never returns (#1864). `{ x -> loop(x) }`
/// is an `a -> undefined`, while `{ xs -> xs.head }`, whose result is in its input's type, and `{ -> [] }`, whose result
/// is a `list(a)`, are as generic as before.
let internal generalizeDef (env: TypeEnv) (state: InferState) (ty: Ty) : TypeScheme * InferState =
    // A conflict among the constraints, in this def or an earlier one, is set aside, so a def after an error is
    // generalised as it would be without it (#1441).
    let subst = fst (Solving.solve state.Constraints)
    let resolved = Unifier.apply subst ty
    let envVars = envVarsUnder subst env
    envVars.UnionWith(LambdaContexts.usedVars (Unifier.apply subst) state)
    let closed =
        Unifier.outputOnlyRestVars resolved @ Unifier.outputOnlyContextVars resolved @ Unifier.neverReturnedVars resolved
        |> List.filter (fun v -> v.Name.IsNone && not (envVars.Contains v.Id))
    let bottom = TyUnion []
    let closing = closed |> List.map (fun v -> v.Id, bottom) |> Map.ofList
    let closedTy = Unifier.apply closing resolved
    let madeUp, own = closed |> List.partition Unifier.isMadeUpRest
    let solvedWithMadeUp =
        if madeUp.IsEmpty then []
        else
            let madeUpIds = madeUp |> List.map (fun v -> v.Id) |> Set.ofList
            subst
            |> Map.toList
            |> List.choose (fun (id, _) ->
                let var = TyVar.ordinary id None
                let solved = Unifier.apply subst (TyVar var)
                if Unifier.isMadeUpRest var || Set.isEmpty (Set.intersect madeUpIds (TypeEnv.freeTyVars solved)) then None
                else Some (TyVar var, Unifier.apply closing solved))
    let held = Boxes.heldVariables closedTy
    let quantified = TypeEnv.freeTyVars closedTy |> Set.filter (fun id -> not (envVars.Contains id || held.Contains id))
    // A variable of the def's type that a task's work stands for is a work variable of the def (see `TaskWork`). Only
    // one among a function's contexts can be: a def whose type has none, as most have, looks at no work variable, which
    // a def of many tasks has many of (#2551).
    let workCandidates = Set.intersect quantified (TaskWork.contextVariables closedTy)
    let taskWork =
        if workCandidates.IsEmpty then state.TaskWork
        else
        state.TaskWork
        |> Map.fold (fun marked id at ->
            TypeEnv.freeTyVars (Unifier.apply subst (TyVar(TyVar.ordinary id None)))
            |> Set.intersect workCandidates
            |> Set.fold (fun marked var -> if Map.containsKey var marked then marked else marked |> Map.add var at) marked) state.TaskWork
    { Quantified = quantified
      Type = closedTy
      Symbol = None
      ProvidedBy = None },
    (own |> List.map (fun v -> TyVar v, bottom)) @ solvedWithMadeUp
    |> List.fold (fun st (var, closedTy) -> addConstraint var closedTy st) { state with TaskWork = taskWork }

/// The types of a lambda's parameters when its input is expected to be `expectedInput`. Several parameters take a
/// record's fields, by name or by position. Against an input known not to be a record they are a tuple of fresh
/// variables that must be that input, so `{ x, y -> ... }` where a tag or `string` is expected is a type error
/// reported at `span`, not two parameters of no known type.
let private argTypesFromExpected (state: InferState) (span: SourceSpan) (args: (Identifier * TypeExpr option) list) (expectedInput: Ty) =
    let mutable current = state
    let argTys =
        match Unifier.unalias (resolveSoFar state expectedInput) with
        | TyRecord _ when args.Length = 1 -> [ expectedInput ]
        | TyRecord(fields, _) ->
            args
            |> List.mapi (fun index (name, _) ->
                match fields |> Map.tryFind name with
                | Some ty -> ty
                | None ->
                    let tupleName = tupleFieldName index
                    match fields |> Map.tryFind tupleName with
                    | Some ty -> ty
                    | None ->
                        let ty, next = freshVar current
                        current <- next
                        ty)
        | _ when args.Length = 1 -> [ expectedInput ]
        | resolved ->
            let tys =
                args
                |> List.map (fun _ ->
                    let ty, next = freshVar current
                    current <- next
                    ty)
            match resolved with
            | _ when args.IsEmpty -> ()
            | TyVar _
            | TyOpenRecord _ -> ()
            | _ -> current <- addConstraintAt span expectedInput (tupleRecordType tys) current
            tys
    argTys, current

/// The constraints inferring `expr` adds are reported at `expr` (or, for a node the compiler made up, wherever the
/// enclosing ones are); those its parent adds afterwards are reported at the parent again.
///
/// An expression that fails to check is recorded in `FailedExprs` and typed as `Unifier.errorType`, which agrees
/// with whatever its surroundings make of it, so checking carries on around it and this never gives `Error`. What
/// inferring it did before it failed is dropped. The smallest expression that fails is the one recorded: one whose
/// parts check but that does not itself (a call with the wrong number of arguments) is recorded whole.
let rec internal inferExpr (env: TypeEnv) (state: InferState) (expr: Expression) : Result<TypedExpr * InferState, Diagnostic list> =
    match state.Inferred |> Map.tryFind expr.Id with
    | Some typed -> Ok (typed, { state with Inferred = state.Inferred |> Map.remove expr.Id })
    | None ->
    let outer = state.Origin
    let origin = if SourceSpan.isNone expr.Span then outer else expr.Span
    let inferred =
        inferExprKind env { state with Origin = origin } expr
        |> Result.bind (fun (typedExpr, next) ->
            match expr.Kind with
            // A call to a function that requires a parameterised context finds its instance once its arguments are typed.
            // Arguments that do not fit the callee leave its type variables open, so no instance is found: the mismatch
            // is the error, which its constraints report, not the context (#1045).
            | FunctionCall(name, spanOpt, _) ->
                match resolveCallInstances env next expr name (SyntaxTree.nameSpan spanOpt expr) with
                | Error _ when snd (Solving.solve next.Constraints) |> List.exists (snd >> SourceSpan.contains expr.Span) -> Ok next
                // A call read as a call to another function (see `PipeTargets`), such as `__toSeq(xs)` read as
                // `__seqItself(xs)` (see `Loops.walksAsSequence`), was checked as that call: the contexts of the
                // function written, which is not called, are not required of it.
                | Error _ when next.PipeTargets.ContainsKey expr.Id -> Ok next
                | resolved -> resolved
                |> Result.bind (fun next -> resolveReferenceInstances env next expr)
                |> Result.bind (fun next -> localFunctionArguments env next expr)
                |> Result.bind (fun next ->
                    match next.CalleeTypes |> Map.tryFind expr.Id with
                    | Some calleeTy -> checkComparisonsAt env next expr name (SyntaxTree.nameSpan spanOpt expr) calleeTy
                    | None -> Ok next)
                |> Result.map (ScopedCalls.leave expr)
                |> Result.map (fun next -> typedExpr, next)
            | _ -> Ok (typedExpr, next))
        |> Result.map (fun (typedExpr, next) -> typedExpr, Unreached.readAsNever env next expr)
    let recovered =
        match inferred with
        | Ok _ -> inferred
        | Error diagnostics ->
            Ok (mkTypedExpr expr Unifier.errorType None None None, { state with FailedExprs = (expr.Id, diagnostics) :: state.FailedExprs })
    recovered
    |> recordExprType
    |> Result.map (fun (typedExpr, next) -> typedExpr, { next with Origin = outer })

and private inferExprKind (env: TypeEnv) (state: InferState) (expr: Expression) : Result<TypedExpr * InferState, Diagnostic list> =
    match expr.Kind with
    // A string literal is a `char` where one is expected (see `CharLiterals`).
    | LiteralExpr(StringLit text) when ExpectedLiterals.expectsChar state expr ->
        CharLiterals.typeOf expr.Span text |> Result.map (fun ty -> mkTypedExpr expr ty None None None, state)
    | InterpolatedString _ when ExpectedLiterals.expectsChar state expr -> CharLiterals.withHoles expr.Span
    // A number with a unit is a measured value of the unit's dimension, stored as the number the code around it expects,
    // as a plain literal is, or the one its dimension is constrained to (see `Units`, `NumericLiterals.check`).
    | LiteralExpr(UnitLit(_, unitName) as literal) ->
        match Units.unitNamed state expr.Span unitName with
        | Error error -> Error [ error ]
        | Ok info ->
            let storage, next = NumericLiterals.typeOf (recordUnitReference expr info state) expr.Span (Some expr.Id) literal
            let next =
                match Units.constrainedStorage next info.Dimension with
                | Some constrained -> addConstraintAt expr.Span storage constrained next
                | None -> next
            Ok (mkTypedExpr expr (TyMeasure(info.Dimension, storage)) None None None, next)
    | LiteralExpr literal ->
        let ty, next = NumericLiterals.typeOf state expr.Span (Some expr.Id) literal
        Ok (mkTypedExpr expr ty None None None, next)
    | InterpolatedString parts ->
        parts
        |> List.choose (function StringExpr inner -> Some inner | StringText _ -> None)
        |> inferAll env state
        |> Result.map (fun (_, current) -> mkTypedExpr expr (TyPrimitive "string") None None None, current)
    | UnitExpr ->
        Ok (mkTypedExpr expr (TyPrimitive "unit") None None None, state)
    // A workflow keyword's call, or a builder block's `zero`, reads a member of the context the builder runs its block
    // under, which must be in scope by its name.
    | IdentifierExpr name
    | FunctionCall(name, _, _) when WorkflowMembers.isMissing env state expr.Id name -> Error [ WorkflowMembers.missing state expr.Id name ]
    // The placeholder for code not written yet is an error, so a release build refuses it and a debug build defers it,
    // stopping the program if it is reached. It has the type `undefined`, as `crash(...)` has: it never gives a value,
    // so it fits any type without fixing a type variable, and nothing that uses it is another error.
    | IdentifierExpr name when name = Parser.Program.ImplicitNames.notImplemented ->
        let error = Diagnostics.at DiagnosticCodes.NotImplemented expr.Span "`???` stands for code not written yet"
        Ok (mkTypedExpr expr Unifier.bottomType None None None, { state with FailedExprs = (expr.Id, [ error ]) :: state.FailedExprs })
    | IdentifierExpr name ->
        match TypeEnv.tryFind name env with
        | Some scheme when not (referenceErrors name expr scheme state).IsEmpty -> Error (referenceErrors name expr scheme state)
        | Some scheme ->
            // A function named as a value requires its contexts where it is called, which its type says (see
            // `ContextVariables.ofNamedValue`), so they need not be available where it is named. So do the contexts a
            // host provides that an unannotated function was inferred to require, once it is inferred (`f >> shout` is
            // a `[Console]` function); any other ambient context it requires, a module's own instance or `Memory`'s
            // scope, is found where it is named. A parameterised context is found where it is named, but by type, once
            // a call settles it (see `resolveCallInstances`).
            let inferredAmbient =
                inferredAmbientContexts env state (canonicalName env state name)
                |> Option.filter (fun contexts -> Set.isSubset contexts state.HostContexts)
            let contextCheck =
                match contextRequirementsOf env state name with
                | Some contexts ->
                    let parameterized = contexts |> List.filter (isParameterizedContext state)
                    ensureContextAvailable None false env state name parameterized None expr.Span
                | None -> Ok state
                |> Result.bind (fun current ->
                    if inferredAmbient.IsSome then Ok current
                    else checkContextUse None env current (canonicalName env state name) expr.Span)
            match contextCheck with
            | Error err -> Error err
            | Ok state ->
                // A member of a plain context read as a value, as a builder's block reads its `zero`, is used where it
                // is read, as a call of it is (see `LambdaContexts`). One that is a function requires the context where
                // it is called instead, so that what it holds of the instance can't outlive the instance's `use`.
                let ofMember = LambdaContexts.contextOfMember env state name |> Option.map (fun ctx -> TyApply(ctx, [])) |> Option.toList
                let isFunction = match Unifier.unalias scheme.Type with TyFunc _ -> true | _ -> false
                let state = if isFunction then state else state |> LambdaContexts.recordUnsupplied env ofMember
                let ty, next = instantiate (recordReference expr scheme state) scheme
                let ty =
                    match Unifier.unalias ty with
                    | TyFunc(input, output, own) when not ofMember.IsEmpty -> TyFunc(input, output, own @ ofMember |> List.distinct)
                    | _ -> ty
                let ty, next =
                    match contextRequirementsOf env state name with
                    | Some written -> ContextVariables.ofNamedValue written scheme.Type ty next
                    | None -> ty, next
                // Each by the name its module declares it under, as the signatures of the functions it is passed to
                // write it (`Console` for `c.Console`), and a scoped one of a scope of its own.
                let ty, next =
                    match inferredAmbient |> Option.map Set.toList, Unifier.unalias ty with
                    | Some (_ :: _ as contexts), TyFunc(input, output, own) ->
                        let declared =
                            contexts
                            |> List.map (fun ctxName ->
                                let name = next.TypeDefs |> Map.tryFind ctxName |> Option.map _.Name |> Option.defaultValue ctxName
                                TypeExpr.synthetic (TypeName name))
                        let carried, next = WrittenContexts.plain next ty declared
                        TyFunc(input, output, own @ carried |> List.distinct), next
                    | _ -> ty, next
                // A function named as a value is checked as a call to it would be, at the type it is passed as.
                checkComparisonsAt env next expr name expr.Span ty
                |> Result.map (fun next ->
                    let symbol = scheme.Symbol |> Option.bind next.Symbols.TryFind
                    mkTypedExpr expr ty None None None, MemoryCells.recordUse env next name |> LambdaContexts.recordNamed symbol ty)
        | None ->
            // A def attached to a context available here, by its bare name.
            match AttachedContextDefs.qualifiedFor env state name expr.Span with
            | Some (Ok qualified) -> inferExpr env (AttachedContextDefs.record expr.Id qualified state) { expr with Kind = IdentifierExpr qualified }
            | Some (Error errors) -> Error errors
            | None -> Error [ StandardLibraryImports.unknown state.StandardLibraryExports.Value DiagnosticCodes.UnknownIdentifier expr.Span $"'{name}' is not in scope" env.Names.Keys name ]
    | SpreadSyntax.Call parts -> RecordSpread.infer inferExpr env state expr parts
    // A loop over a map or a set run by a builder's `for`, which takes a list (see `WorkflowMembers.loopOverStore`).
    | FunctionCall(name, spanOpt, (iterable :: others as args)) when name = WorkflowSyntax.forMember && state.WorkflowMemberPlaces.ContainsKey expr.Id ->
        WorkflowMembers.loopOverStore inferExpr env state expr iterable others
        |> Option.defaultWith (fun () -> InferCalls.inferCall inferExpr inferLambdaWithExpected env (ExpectedLiterals.intoMapEntries state expr) expr name spanOpt args)
    // The number a measured value is stored as, which a derived `toString` writes (see `StructuralInstances`).
    | FunctionCall(name, _, [ value ]) when name = Units.storageIntrinsic ->
        inferExpr env state value
        |> Result.bind (fun (typed, next) ->
            match Unifier.unalias (resolveSoFar next typed.Type) with
            | TyMeasure(_, storage) -> Ok (mkTypedExpr expr storage None None None, next)
            | other -> Error [ Diagnostics.at DiagnosticCodes.TypeMismatch value.Span $"'{name}' takes a measured value, not a '{Unifier.tyToStringIn state.TypeDefs other}'" ])
    | FunctionCall(name, spanOpt, args) -> InferCalls.inferCall inferExpr inferLambdaWithExpected env (ExpectedLiterals.intoMapEntries state expr) expr name spanOpt args
    | Lambda(args, body, _) ->
        let expectedInput, expectedReturn =
            match ExpectedLiterals.lambdaTypeOf state expr with
            | Some (TyFunc(input, ret, _)) -> Some input, Some ret
            | _ -> None, None
        // Checked against a function type, as a record field's or a lambda's result, it can use the contexts that type
        // requires, as a lambda argument can (see `Contexts.extendEnvForLambdaArgument`).
        let expected = ExpectedLiterals.functionOf state expr
        let lambdaEnv, state = Contexts.extendEnvForLambdaArgument env state expr expected
        let instances = Contexts.instancesSuppliedToLambda state expr expected
        state
        |> Contexts.outsideValue (fun state -> inferLambdaWithExpected lambdaEnv state expr args body expectedInput expectedReturn)
        |> Result.map (Contexts.withSuppliedInstances instances expr)
    // A power of a measured value would be of a derived dimension, which a later step adds (see `MeasuredArithmetic`). The
    // base is inferred alone to see, and what that finds is set aside.
    | BinaryOp(op, opSpan, left, right) when op = ArithmeticOperators.power ->
        match inferExpr env state left |> Result.toOption |> Option.bind (fun (typed, next) -> MeasuredArithmetic.dimensionOf next typed.Type) with
        | Some dimension -> Error [ MeasuredArithmetic.powerError (opSpan |> Option.defaultValue expr.Span) dimension ]
        | None ->
        ArithmeticOperators.inferPower (inferExpr env) state expr left right
        |> Result.bind (fun (typed, next) ->
            // The fraction `/` gives on the base's type, where a definition attached to it says (see `negativePowerError`).
            let leftTy = resolveSoFar next typed.Type
            let fraction =
                tryResolveAttachedFunction env leftTy ArithmeticOperators.divide
                |> Option.bind (fun name -> TypeEnv.tryFind name env)
                |> Option.bind (fun scheme -> match Unifier.unalias scheme.Type with TyFunc(_, result, _) -> Some result | _ -> None)
            match ArithmeticOperators.negativePowerError expr right leftTy fraction with
            | Some error -> Error [ error ]
            | None -> Ok (typed, next))
    | BinaryOp(op, _, left, right) when (CompositionOperators.functionFor op).IsSome ->
        inferAsCall (inferExpr env) state expr (CompositionOperators.functionFor op).Value [ left; right ]
    | CoalesceOperator.Call(left, right) -> CoalesceOperator.infer (inferExpr env) state expr left right
    // A list literal that spreads a set, a sequence or anything else `Iterable` (see `CollectionLiterals.spreading`).
    | BinaryOp(_, _, left, right) ->
        CollectionLiterals.spreading inferExpr env state expr
        |> Option.defaultWith (fun () -> inferOperator env state expr left right)
    | Pipe(value, name, spanOpt, args) -> inferPipe env state expr value name spanOpt args
    // The names a lambda gives its contexts are bound with its parameters (see `inferLambdaAfterTypeOfs`).
    | ContextNames(_, body) ->
        inferExpr env state body
        |> Result.map (fun (typedBody, next) -> mkTypedExpr expr typedBody.Type (Some typedBody) None None, next)
    | UseIn(value, body) when (ContextNames.loaded env state value).IsSome ->
        let scheme, instance = (ContextNames.loaded env state value).Value
        inferExpr (ContextNames.load env instance) (ContextNames.recordLoad value scheme instance state) body
        |> Result.map (fun (typedBody, next) -> mkTypedExpr expr typedBody.Type (Some typedBody) None None, next)
    | UseIn(value, _) when not (UseDischarge.notLoadable env state value).IsEmpty -> Error (UseDischarge.notLoadable env state value)
    | UseIn(value, body) ->
        match inferExpr env (state |> UseDischarge.mark value |> LambdaContexts.enterUse value) value with
        | Error err -> Error err
        | Ok (typedValue, nextState) ->
            let withFields = extendEnvWithRecordFields (FromUse value.Id) nextState env typedValue.Type
            let envWithUse, nextState = useInstance withFields nextState value typedValue.Type
            let scopeErrors, envWithUse = ScopedCalls.instanceInScope envWithUse nextState value
            match ownInstanceErrors withFields envWithUse nextState value @ scopeErrors with
            | _ :: _ as errors -> Error errors
            | [] ->
            match inferExpr envWithUse nextState body with
            | Ok (typedBody, finalState) ->
                Ok (mkTypedExpr expr typedBody.Type (Some typedBody) None None, finalState)
            | Error err -> Error err
    | WorkflowBindExpr _ ->
        Error [ Diagnostics.at DiagnosticCodes.WorkflowKeywordOutsideWorkflow expr.Span "A workflow's bind is outside a builder block: write it inside one, such as `proc { ... }`" ]
    | WorkflowReturnExpr _ ->
        Error [ Diagnostics.at DiagnosticCodes.WorkflowKeywordOutsideWorkflow expr.Span "A workflow's `return` is outside a builder block: write it inside one, such as `proc { ... }`" ]
    | Block statements ->
        inferBlock env state expr statements
    | TupleExpr items ->
        let state = ExpectedLiterals.intoRecordFields state expr (items |> List.map PositionalField)
        inferAll env state items
        |> Result.map (fun (itemExprs, current) -> mkTypedExpr expr (tupleRecordType (itemExprs |> List.map _.Type)) None None None, current)
    | RecordExpr fields ->
        let state = ExpectedLiterals.intoRecordFields state expr fields
        // The positional fields are inferred first, then the named ones, each in the order they are written.
        let positional = fields |> List.choose (function PositionalField value -> Some value | NamedField _ -> None)
        let named = fields |> List.choose (function NamedField(name, value) -> Some (name, value) | PositionalField _ -> None)
        inferAll env state (positional @ List.map snd named)
        |> Result.map (fun (typedFields, current) ->
            let positionalExprs, namedExprs = List.splitAt positional.Length typedFields
            let positionalFieldsMap = positionalExprs |> List.mapi (fun index typedExpr -> tupleFieldName index, typedExpr.Type)
            let namedFieldsMap = List.zip (List.map fst named) (namedExprs |> List.map _.Type)
            let recordTy = TyRecord(Map.ofList (positionalFieldsMap @ namedFieldsMap), FieldOrder.ofList named)
            // A mismatch of the record with the one expected of it is reported at the first field value that doesn't
            // fit, as a call's arguments are (see `ArgumentSpans`).
            let current =
                match ExpectedLiterals.expectedRecord state expr with
                | Some (expectedTy, expectedFields) ->
                    let names = (positionalExprs |> List.mapi (fun index _ -> tupleFieldName index)) @ List.map fst named
                    let items = List.zip names typedFields |> List.choose (fun (name, typed) -> expectedFields |> Map.tryFind name |> Option.map (fun ty -> typed, ty))
                    let reported = lazy (LambdaArguments.reportedSpan current expr.Span (List.map fst items) (List.map snd items) recordTy expectedTy)
                    { current with ArgumentSpans = current.ArgumentSpans |> Map.add expr.Span reported }
                | None -> current
            mkTypedExpr expr recordTy None None None, current)
    | ListExpr items ->
        // Checked against a `list(a)`, the items are checked against `a`, and the list is one (see `ExpectedLiterals`).
        let expectedElement = ExpectedLiterals.elementOf state expr
        let state = expectedElement |> Option.fold (fun state element -> items |> List.fold (ExpectedLiterals.expect element) state) state
        // A lambda among them takes the input and gives the result of the function type expected of it, as a lambda
        // argument does: `{ x, y -> ... }` for `(x: int, y: int) -> int` takes the fields by name.
        let state = expectedElement |> Option.fold (fun state element -> ExpectedLiterals.intoLambdas element state items) state
        inferAll env state items
        |> Result.map (fun (typedItems, current) ->
            match typedItems with
            | [] ->
                let elementTy, nextState = freshVar current
                mkTypedExpr expr (listType elementTy) None None None, nextState
            // Checked against a `list(a)` whose `a` is known, each element is given where an `a` is expected, as an
            // argument is: two lambdas that give back different tags of the union `a`'s result is fit it, though they
            // don't join. Where `a` is still a variable, as `list.length`'s is, the elements say what it is, joined, and
            // so they do where it is a tuple or a record with a field still a variable, as `__map`'s entries, `(k, v)`,
            // are: `["k" => #s("a"), "v" => #n(1.0)]` holds `#s(string) | #n(float)`s (#2882).
            | typedItems when expectedElement |> Option.exists (fun element ->
                match resolveSoFar current element with
                | TyVar _ -> false
                | TyRecord(fields, _) -> fields |> Map.forall (fun _ field -> match resolveSoFar current field with TyVar _ -> false | _ -> true)
                | _ -> true) ->
                let element = expectedElement.Value
                mkTypedExpr expr (listType element) None None None,
                typedItems |> List.fold (fun constrained item -> addAssignableConstraintAt item.Expr.Span item.Type element constrained) current
            | typedItems ->
                // The elements are joined as an `if`'s branches are, so `[#red, #green]` is a `list(#red | #green)`.
                let current = OddBranches.note Set.empty OddBranches.Items typedItems current
                let elementTy, constrained = joinBranchTypes Set.empty current (typedItems |> List.map _.Type)
                match expectedElement with
                | Some element -> mkTypedExpr expr (listType element) None None None, addConstraint elementTy element constrained
                | None -> mkTypedExpr expr (listType elementTy) None None None, constrained)
    | TagExpr(name, payloadOpt) ->
        match payloadOpt with
        | None -> Ok (mkTypedExpr expr (TyTag(name, None)) None None None, state)
        | Some payload ->
            // Checked against a union, the payload is checked against what the union's tag holds.
            let expected = ExpectedLiterals.payloadOf state expr name
            let state = expected |> Option.fold (fun state ty -> ExpectedLiterals.expect ty state payload) state
            let inferred = expected |> Option.bind (CollectionLiterals.targeted inferExpr env state payload) |> Option.defaultWith (fun () -> inferExpr env state payload)
            match inferred with
            | Ok (payloadExpr, next) -> Ok (mkTypedExpr expr (TyTag(name, Some payloadExpr.Type)) None None None, next)
            | Error err -> Error err
    // An `if` whose condition's `is` tests bind names is checked as the match it stands for.
    | IsSyntax.BindingIf asMatch -> inferExprKind env state { expr with Kind = asMatch }
    | IfExpr(condition, thenExpr, elseExpr) ->
        match inferExpr env state condition with
        | Error err -> Error err
        | Ok (condExpr, next) ->
            let withConstraint = addConstraint condExpr.Type (primitiveType "bool") next
            match inferExpr env withConstraint thenExpr with
            | Error err -> Error err
            | Ok (thenExprTyped, nextState) ->
                match inferExpr env nextState elseExpr with
                | Error err -> Error err
                | Ok (elseExprTyped, finalState) ->
                    // The condition and branches are kept as statements so that walks over the typed tree (the
                    // backend's collection of local and pattern-bound types) reach what they bind, such as a
                    // match in the else branch of a multi-branch if.
                    let children = [ condExpr; thenExprTyped; elseExprTyped ] |> List.map TypedExprStatement
                    // A number literal can't be `()`, so one in the then branch of an `if` without an `else` is of its
                    // default type at once, for the error to say.
                    let finalState = if IfSyntax.isLeftOut thenExpr elseExpr then NumericLiterals.settleWithin [ thenExprTyped.Type ] finalState else finalState
                    match BranchJoin.leftOutElseError finalState expr thenExpr elseExpr thenExprTyped.Type with
                    | Some error ->
                        // It fails to check, as the `if` it would be with an `else`, so nothing is reported again.
                        Ok (mkTypedExpr expr thenExprTyped.Type None (Some children) None, { finalState with FailedExprs = (expr.Id, [ error ]) :: finalState.FailedExprs })
                    | None ->
                        let finalState = OddBranches.note Set.empty OddBranches.IfBranches [ thenExprTyped; elseExprTyped ] finalState
                        let resultTy, constrained = joinBranchTypes Set.empty finalState [ thenExprTyped.Type; elseExprTyped.Type ]
                        Ok (mkTypedExpr expr resultTy None (Some children) None, constrained)
    // Desugaring has made each `except` a `match`; one checked before then is checked as that match.
    | Except(value, arms) -> inferExprKind env state { expr with Kind = Except.asMatch expr value arms }
    | Match(scrutinees, arms) -> InferMatch.inferMatch inferExpr inferAll inferLambdaWithExpected env state expr scrutinees arms
    | MemberAccess(baseExpr, memberName, spanOpt) ->
        let memberSpan = SyntaxTree.nameSpan spanOpt expr
        match baseExpr.Kind with
        | IdentifierExpr baseName ->
            match qualifiedDefinition env state baseName memberName with
            | Some (qualifiedName, scheme) when not (referenceErrors qualifiedName expr scheme state).IsEmpty -> Error (referenceErrors qualifiedName expr scheme state)
            | Some (qualifiedName, scheme) ->
                let ty, next = instantiate (recordReference expr scheme state) scheme
                let ty, next =
                    match contextRequirementsOf env state qualifiedName with
                    | Some written -> ContextVariables.ofNamedValue written scheme.Type ty next
                    | None -> ty, next
                Ok (mkTypedExpr expr ty None None None, next)
            | None -> readMember env state expr baseExpr (Some baseName) memberName memberSpan
        // `t.Encoder.string` names the definition attached to `Encoder` through the import's alias `t`: it is read as
        // the member `string` of `t.Encoder`, as `Encoder.string` is of `Encoder` (#1833).
        | MemberAccess _ when (SyntaxTree.dottedName baseExpr |> Option.bind (fun qualifier -> qualifiedDefinition env state qualifier memberName)).IsSome ->
            let qualifier = Expression.from baseExpr (IdentifierExpr (SyntaxTree.dottedName baseExpr).Value)
            let read = { expr with Kind = MemberAccess(qualifier, memberName, spanOpt) }
            inferExprKind env state read
            |> Result.map (fun (typed, next) -> { typed with Expr = expr }, { next with ReadAs = next.ReadAs |> Map.add expr.Id read })
        | _ -> readMember env state expr baseExpr None memberName memberSpan
    | TypeValue written -> TypeArguments.infer state expr written
    // `C(T).member` as a value has the member's type in the context's definition, as a call of it does, and a function
    // member requires `C(T)` where it is called, as a plain context's named as a value does. A member the context does
    // not have leaves the value's type to what uses it.
    | ContextMemberAccess(ctxType, memberName) ->
        let state = recordContextMemberReference expr ctxType memberName state
        let ty, next =
            match Contexts.memberType state ctxType memberName with
            | Some (memberTy, next) ->
                match Unifier.unalias memberTy, Contexts.contextTypeOf next ctxType with
                | TyFunc(input, output, own), Some (context, next) -> TyFunc(input, output, own @ [ context ] |> List.distinct), next
                | _ -> memberTy, next
            | None -> freshVar state
        Ok (mkTypedExpr expr ty None None None, next)
    // `C(T).member(args)`: the arguments are checked against the member's type in the context's definition, and the
    // call has its result type. A member the context does not have leaves the call's type to what uses it.
    | ContextMemberCall(ctxType, memberName, args) ->
        inferAll env (recordContextMemberReference expr ctxType memberName state) args
        |> Result.map (fun (typedArgs, current) ->
            match Contexts.memberType current ctxType memberName with
            | Some (memberTy, next) ->
                match Unifier.unalias memberTy with
                | TyFunc(input, ret, _) ->
                    let inputSpan = match args with [ single ] -> single.Span | _ -> SourceSpan.none
                    mkTypedExpr expr ret None None None, addAssignableConstraintAt inputSpan (inputTypeFromArgs (typedArgs |> List.map _.Type)) input next
                | _ ->
                    let ty, next = freshVar next
                    mkTypedExpr expr ty None None None, next
            | None ->
                let ty, next = freshVar current
                mkTypedExpr expr ty None None None, next)

/// An operator applied to `left` and `right` that is neither `**`, a composition nor `??`: read through a definition
/// of it if one is found, or else as the operator built in.
and private inferOperator (env: TypeEnv) (state: InferState) (expr: Expression) (left: Expression) (right: Expression) : Result<TypedExpr * InferState, Diagnostic list> =
    // A string literal compared with a `char` is one, on either side (see `CharLiterals`): on the right, it is checked
    // knowing so; on the left, it is read again once the right is known, a literal's type taking no constraint.
    let compares =
        match expr.Kind with
        | BinaryOp(op, _, _, _) -> EqualityOperators.isOperator op || OrderingOperators.isOperator op
        | _ -> false
    let leftAsChar (leftExpr: TypedExpr) (rightExpr: TypedExpr) (state: InferState) =
        match left.Kind with
        | LiteralExpr(StringLit text) when compares && CharLiterals.isChar state rightExpr.Type ->
            CharLiterals.typeOf left.Span text
            |> Result.map (fun ty -> { leftExpr with Type = ty }, rightExpr, { state with ExprTypes = state.ExprTypes |> Map.add left.Id ty })
        | _ -> Ok (leftExpr, rightExpr, state)
    match inferExpr env state left with
    | Error err -> Error err
    | Ok (leftExpr, next) ->
        let next = if compares && CharLiterals.isChar next leftExpr.Type then ExpectedLiterals.expect Chars.ty next right else next
        match
            inferExpr env next right
            |> Result.bind (fun (rightExpr, nextState) -> leftAsChar leftExpr rightExpr nextState)
            |> Result.bind (fun (leftExpr, rightExpr, nextState) -> ArithmeticOperators.readLiterals (inferExpr env) env nextState expr leftExpr rightExpr) with
        | Error err -> Error err
        | Ok (leftExpr, rightExpr, nextState) ->
            // The operator on operands of these types, which `MeasuredArithmetic` may have read as their storages.
            let operatorOn (leftExpr: TypedExpr) (rightExpr: TypedExpr) (nextState: InferState) =
                let arithmetic = match expr.Kind with BinaryOp(op, _, _, _) -> ArithmeticOperators.isOperator op | _ -> false
                let nextState, constrained = NumericLiterals.atOperator arithmetic leftExpr.Type rightExpr.Type nextState
                let input = inputTypeFromArgs [ leftExpr.Type; rightExpr.Type ]
                // Inside an attached `==`, `==` on its own type compares by structure rather than calling itself.
                let attached (name: Identifier) =
                    tryResolveAttachedFunction env (resolveSoFar nextState leftExpr.Type) name
                    |> Option.filter (fun found ->
                        name <> EqualityOperators.equals
                        || (nextState.ContextScope <> DefScope(found, false) && nextState.ContextScope <> DefScope(found, true)))
                match OperatorCalls.definition (memberOfInstanceInScope env nextState) attached env nextState expr leftExpr.Type input with
                | Some (callee, scheme, instance) ->
                    let resultTy, called = OperatorCalls.infer nextState expr callee scheme instance input
                    // A definition that requires a parameterised context finds its instance as a call to it does (#1594).
                    let opSpan = match expr.Kind with BinaryOp(_, opSpan, _, _) -> opSpan | _ -> None
                    match requireOperatorContexts env called expr callee (SyntaxTree.nameSpan opSpan expr) |> Result.bind (fun called -> resolveCallInstances env called expr callee (SyntaxTree.nameSpan opSpan expr)) with
                    | Error _ when snd (Solving.solve called.Constraints) |> List.exists (snd >> SourceSpan.contains expr.Span) -> Ok called
                    | resolved -> resolved
                    |> Result.map (fun called -> mkTypedExpr expr resultTy None None None, called)
                | None ->
                match expr.Kind with
                | BinaryOp(op, _, _, _) when op = AppendOperator.operator ->
                    AppendOperator.check constrained expr leftExpr.Type rightExpr.Type
                    |> Result.map (fun (resultTy, joined) -> mkTypedExpr expr resultTy None None None, joined)
                | BinaryOp(op, _, _, _) when ArithmeticOperators.isOperator op ->
                    match OperatorCalls.lacking env constrained expr op leftExpr.Type input with
                    | Some error -> Error [ error ]
                    | None ->
                    let resultTy, constrained = ArithmeticOperators.resultOf constrained expr op leftExpr.Type
                    ArithmeticOperators.check constrained expr op leftExpr.Type rightExpr.Type resultTy (ArithmeticOperators.numericOperandType constrained leftExpr.Type rightExpr.Type)
                    |> Result.map (fun numericConstrained -> mkTypedExpr expr resultTy None None None, numericConstrained)
                | BinaryOp(op, _, _, _) when OrderingOperators.isOperator op ->
                    OrderingOperators.require (orderingInstance op expr.Span) env constrained expr op leftExpr.Type input
                    |> Result.map (fun required -> mkTypedExpr expr (TyPrimitive "bool") None None None, required)
                | BinaryOp(op, _, _, _) when EqualityOperators.isOperator op ->
                    // Tags are joined, not made equal, as an `if`'s branches are: `o == #none` leaves `o` open to other
                    // tags. Anything else is made equal.
                    let operandTy, joined =
                        if EqualityOperators.joinsOperands (resolveSoFar nextState leftExpr.Type) (resolveSoFar nextState rightExpr.Type) then
                            joinBranchTypes Set.empty nextState [ leftExpr.Type; rightExpr.Type ]
                        else leftExpr.Type, constrained
                    let comparesParts = comparesOwnParts env Set.empty EqualityOperators.equals (partInstanceIn env joined EqualityOperators.context) (fun _ -> true)
                    let derive = derivedComparison env Set.empty (fun _ -> None) expr.Id expr.Span
                    EqualityOperators.require (wellKnownInstance WellKnownContexts.equality op expr.Span) comparesParts derive env joined expr op operandTy input
                    |> Result.map (fun required -> mkTypedExpr expr (TyPrimitive "bool") None None None, required)
                | BinaryOp(op, _, _, _) when ["and"; "or"] |> List.contains op ->
                    // Both operands are already unified with each other; pin them to bool.
                    let boolConstrained = constrained |> addConstraint leftExpr.Type (TyPrimitive "bool")
                    Ok (mkTypedExpr expr (TyPrimitive "bool") None None None, boolConstrained)
                // A spare operator (`^`, `<>`, ...) means only what a definition of it gives it.
                | BinaryOp(op, opSpan, left, _) ->
                    let at = opSpan |> Option.defaultValue expr.Span
                    Error [ OperatorCalls.undefinedSpare (MemoryCells.isCell env constrained) op at left (resolveSoFar nextState leftExpr.Type) ]
                | _ -> Ok (mkTypedExpr expr leftExpr.Type None None None, constrained)
            // An operator attached to the left operand's type, a type of its own, is called whatever the operands
            // are, as `` Stamp.`+`: Stamp, Time(int) -> Stamp `` is for `s + 3s` (#2873). A number type's is not: on one,
            // the operator is the built-in one, so `2 * 3s` is read as `MeasuredArithmetic` says.
            let attachesOperator (op: string) =
                match Unifier.unalias (resolveSoFar nextState leftExpr.Type) with
                | SizedNumberType _ -> false
                | TyNominal _ as own -> (tryResolveAttachedFunction env own op).IsSome
                | _ -> false
            // An arithmetic operator on a measured value is the operator on its storage, whose result has the dimension
            // the table says (see `MeasuredArithmetic`).
            match expr.Kind with
            | BinaryOp(op, _, _, _) when ArithmeticOperators.isOperator op && attachesOperator op -> operatorOn leftExpr rightExpr nextState
            | BinaryOp(op, _, _, _) when ArithmeticOperators.isOperator op ->
                match MeasuredArithmetic.read nextState expr op leftExpr.Type rightExpr.Type with
                | Some (Ok reading) ->
                    operatorOn { leftExpr with Type = reading.Left } { rightExpr with Type = reading.Right } nextState
                    |> Result.map (fun (typed, next) -> { typed with Type = reading.Result typed.Type }, next)
                // On a type of its own beside a measured value, as `now() + 5s` is, that the type has no operator is
                // the error, naming what it attaches for the operands instead (see `OperatorCalls.lacking`).
                | Some (Error error) ->
                    let leftTy = resolveSoFar nextState leftExpr.Type
                    match OperatorCalls.lacking env nextState expr op leftTy (inputTypeFromArgs [ leftExpr.Type; rightExpr.Type ]) with
                    | Some lacking when (tryResolveAttachedFunction env leftTy op).IsNone -> Error [ lacking ]
                    | _ -> Error [ error ]
                | None -> operatorOn leftExpr rightExpr nextState
            | _ -> operatorOn leftExpr rightExpr nextState

/// `value` piped to the function `name`, its name at `spanOpt`, with `args`: a call of the function attached to
/// `value`'s type under that name, if there is one, or else of `name` itself.
and private inferPipe (env: TypeEnv) (state: InferState) (expr: Expression) (value: Expression) (name: string) (spanOpt: SourceSpan option) (args: Expression list) : Result<TypedExpr * InferState, Diagnostic list> =
    match inferExpr env state value with
    | Error err -> Error err
    | Ok (typedValue, nextState) ->
        // A pipe resolves left to right: the value's type as the code before the pipe settles it picks the
        // attached function, even where that type is still a variable waiting on the module's constraints. A
        // number literal the code before it has not settled is of its default type, `3 \toFloat` an int's.
        let nextState = NumericLiterals.settleWithin [ typedValue.Type ] nextState
        let valueTy = resolveSoFar nextState typedValue.Type
        let resolvedName =
            tryResolveAttachedFunction env valueTy name
            |> Option.defaultValue name
        let nextState =
            if resolvedName = name then nextState
            else { nextState with PipeTargets = nextState.PipeTargets |> Map.add expr.Id resolvedName }
        let callArgs = pipeCallArguments value args
        let lookup : PipeLookups.Lookup =
            { PipedType = valueTy; AttachedUnder = attachedTypeNames valueTy; Written = name; Called = resolvedName; At = SyntaxTree.nameSpan spanOpt expr }
        let checkArgumentsOnly () =
            let mutable current = nextState
            for arg in args do
                match inferExpr env current arg with
                | Ok (_, next) -> current <- next
                | Error _ -> ()
            current
        // Piping what failed to check to a name attached to nothing is not another error: the name may be
        // attached to the type it was meant to have. Its arguments are still checked.
        if resolvedName = name && Unifier.isError valueTy && (TypeEnv.tryFind name env).IsNone then
            let current = checkArgumentsOnly ()
            Ok (mkTypedExpr expr Unifier.errorType None None None, { current with UnknownTypeUses = expr.Id :: current.UnknownTypeUses })
        else
        match PipeLookups.misfit env nextState lookup (not args.IsEmpty) with
        | Some misfit ->
            // A plain function the value's type attaches nothing in place of, which does not take the value, is
            // the pipe's error, on its name, rather than the call's mismatch.
            let current = checkArgumentsOnly ()
            Ok (mkTypedExpr expr Unifier.errorType None None None, { current with FailedExprs = (expr.Id, [ misfit ]) :: current.FailedExprs })
        | None ->
        let call = { expr with Kind = FunctionCall(resolvedName, spanOpt, callArgs) }
        // A value that is itself a pipe, the chain to the left of this one, is given to the call as it is typed already:
        // inferring it again at each stage made a chain's checking double with every stage (#1851). Nothing the call
        // expects of its argument changes how a pipe is typed. Any other value is inferred again as the call's first
        // argument, where what the call expects of it, as a lambda's parameter types, is known.
        let inferCall () =
            match value.Kind with
            | Pipe _ ->
                inferExpr env { nextState with Inferred = nextState.Inferred |> Map.add value.Id typedValue } call
                |> Result.map (fun (typedCall, finalState) -> typedCall, { finalState with Inferred = finalState.Inferred |> Map.remove value.Id })
            | _ ->
                inferExpr env (LambdaContexts.forgetSince state nextState) call
                // The call infers the value again, as its first argument, so what the value used the first time goes. A
                // call in the value left to find its instance later is left again at the types the call settles, so the
                // one left the first time, at types nothing settles, goes.
                |> Result.map (fun (typedCall, finalState) ->
                    typedCall, withoutRepeatedPending state.PendingContextInstances nextState.PendingContextInstances finalState)
        match inferCall () with
        | Ok (typedCall, finalState) ->
            // The call failed if it recorded itself as failed. Its error for a name it found nothing for, or more
            // than one of, is the pipe's, which says what the pipe looked for.
            let finalState =
                match finalState.FailedExprs with
                | (id, diagnostics) :: rest when id = expr.Id && rest.Length = nextState.FailedExprs.Length ->
                    let isTypeName typeName =
                        (nextState.TypeDefs.ContainsKey typeName && not (nextState.HiddenTypeNames.Contains typeName))
                        || isBuiltInTypeName typeName
                    { finalState with FailedExprs = (id, PipeLookups.explain env nextState isTypeName lookup diagnostics) :: rest }
                | _ -> finalState
            Ok ({ typedCall with Expr = { expr with Kind = Pipe(value, resolvedName, spanOpt, args) } }, finalState)
        | Error err -> Error err

/// `state` with each argument of `call` that names a local function that requires a parameterised context read as the
/// lambda that calls it (see `localFunctionAsLambda`), once the call has settled the argument's type, so that the
/// lambda's call finds the instance at that type. A type may be settled as one of the variables of the def around the
/// call, which then requires the context, as `apply(f, v)` does at `v`'s type. One left open otherwise is left as it
/// is: `dbg(g)` never calls it.
and private localFunctionArguments (env: TypeEnv) (state: InferState) (call: Expression) : Result<InferState, Diagnostic list> =
    let settled (current: InferState) (ty: Ty) =
        let subst = fst (Solving.solve current.Constraints)
        let envVars = envVarsUnder subst env
        TypeEnv.freeTyVars (Unifier.apply subst ty) |> Set.forall envVars.Contains
    match call.Kind with
    | FunctionCall(_, _, args) ->
        match args with
        | [ Kind (TupleExpr items) ] -> items
        | _ -> args
        |> List.fold (fun result arg ->
            result
            |> Result.bind (fun (current: InferState) ->
                match localFunctionAsLambda env current arg |> Option.bind (fun lambda -> current.ExprTypes |> Map.tryFind arg.Id |> Option.map (fun ty -> ty, lambda)) with
                | Some (argTy, lambda) when settled current argTy ->
                    inferExpr env { current with ReadAs = current.ReadAs |> Map.add arg.Id lambda } lambda
                    |> Result.map (fun (typed, next) -> addConstraintAt arg.Span argTy typed.Type next)
                | _ -> Ok current)) (Ok state)
    | _ -> Ok state

/// `baseExpr.memberName`, where `expr` is that member access and `receiver` the base as written if it is a name: a
/// record's field, or else a built-in property of the base's type as far as the code before it settles that type,
/// as for a pipe. A value of no known type yet is taken to be a record with that field (see `fieldTySoFar`), so
/// `{ s -> s.length }` on its own reads a field, not a string's length. Passed to a function that takes it as a
/// `string -> b`, its parameter is a string already (see `LambdaArguments`).
and private readMember (env: TypeEnv) (state: InferState) (expr: Expression) (baseExpr: Expression) (receiver: string option) (memberName: string) (memberSpan: SourceSpan) =
    match inferExpr env state baseExpr with
    | Error err -> Error err
    | Ok (typedBase, next) ->
        let symbol = match baseExpr.Kind with IdentifierExpr name -> TypeEnv.tryFind name env |> Option.bind _.Symbol | _ -> None
        let resolvedBase = resolveSoFar next typedBase.Type
        // A private type's fields are its representation, which only its home module reads.
        match ProtectedShapes.hiddenRepresentation next expr.Span resolvedBase with
        | Some nominal when recordFieldsOf resolvedBase |> Option.exists (Map.containsKey memberName) ->
            Error [ ProtectedShapes.fieldReadOutside nominal memberName memberSpan ]
        | _ ->
        match TypeDescriptions.memberType next symbol typedBase.Type memberName |> Option.orElseWith (fun () -> fieldTySoFar next typedBase.Type memberName) with
        | Some (fieldTy, next) -> Ok (mkTypedExpr expr fieldTy None None None, next)
        | None ->
            let baseTy = resolveSoFar next typedBase.Type
            match builtInProperty baseTy memberName with
            | Some (property, propertyTy) ->
                Ok (mkTypedExpr expr propertyTy None None None, { next with BuiltInProperties = next.BuiltInProperties |> Map.add expr.Id property })
            // What failed to check was reported where it failed; reading from it is not another error.
            | None when Unifier.isError baseTy ->
                Ok (mkTypedExpr expr Unifier.errorType None None None, { next with UnknownTypeUses = expr.Id :: next.UnknownTypeUses })
            // Nothing is read from what never gives a value, as `crash(...)` and `???` don't.
            | None when Unifier.isBottom baseTy -> Ok (mkTypedExpr expr Unifier.bottomType None None None, next)

            | None -> Error [ unknownMember env memberSpan receiver baseTy memberName ]

/// Infers each of `exprs` in turn, each in the state the one before it left: their typed expressions and the state
/// after the last, or every error any of them reported.
and private inferAll (env: TypeEnv) (state: InferState) (exprs: Expression list) : Result<TypedExpr list * InferState, Diagnostic list> =
    let typed, current, errors =
        exprs
        |> List.fold (fun (typed, current, errors) expr ->
            match inferExpr env current expr with
            | Ok (typedExpr, next) -> typedExpr :: typed, next, errors
            | Error err -> typed, current, errors @ err) ([], state, [])
    if errors.IsEmpty then Ok (List.rev typed, current) else Error errors

/// Infers the value named by each `typeof` in `typeExpr`, so that reading the annotation finds their types.
and internal inferTypeOfValues (env: TypeEnv) (state: InferState) (typeExpr: TypeExpr) : Result<InferState, Diagnostic list> =
    typeOfValues typeExpr
    |> List.fold (fun result value -> result |> Result.bind (fun current -> inferExpr env current value |> Result.map snd)) (Ok state)

/// Infers `expr`, the value of a def whose written type was read as `annotation`: a lambda against the input and
/// result the type expects of it. An attached def's lambda is inferred from its body like any other. Where the body
/// leaves its parameter open (any type, or any record with some fields), the parameter is then the type it is attached
/// to, if that fits: `def Point.show = { p -> "{p.x}" }` takes a `Point`, while `def Point.origin = { n -> Point(x = n,
/// y = n) }` takes an int, and `def UserId.new = { s -> ... s \string.isEmpty ... }` a string.
and internal inferDefValue (env: TypeEnv) (state: InferState) (expr: Expression) (annotation: DefAnnotations.ReadAnnotation) : Result<TypedExpr * InferState, Diagnostic list> =
    match expr.Kind with
    | Lambda(args, body, _) when annotation.ExpectedInput.IsSome || annotation.ExpectedReturn.IsSome ->
        inferLambdaWithExpected env state expr args body annotation.ExpectedInput annotation.ExpectedReturn
    | _ ->
    // A square-bracket literal builds the type the def is declared at, if it is one with a `fromArray`.
    match annotation.Declared |> Option.bind (CollectionLiterals.targeted inferExpr env state expr) with
    | Some targeted -> targeted
    | None ->
        // A literal that ends the def's block builds the declared type too: a long one is written on the lines under `=`.
        let state =
            match expr.Kind, annotation.Declared with
            | Block statements, Some declared ->
                match SyntaxTree.blockValue statements with
                | Some last -> ExpectedLiterals.expectBlockResult declared state last
                | None -> state
            | _, Some declared -> ExpectedLiterals.expect declared state expr
            | _ -> state
        inferExpr env state expr
        |> Result.map (fun (typed, next) ->
            // An error earlier in the module is set aside (see `Solving.agreeing`).
            let agreeing = Solving.agreeing next.Constraints
            let isOpen (input: Ty) =
                match Solving.unify agreeing with
                | Ok subst ->
                    match Unifier.unalias (Unifier.apply subst input) with
                    | TyVar _
                    | TyOpenRecord _ -> true
                    | _ -> false
                | Error _ -> false
            match annotation.AttachedInput, Unifier.unalias typed.Type with
            | Some attached, TyFunc(input, _, _) when
                isOpen input && Solving.unify ((input, attached, Equal, expr.Span) :: agreeing) |> Result.isOk
                ->
                typed, addConstraintAt expr.Span input attached next
            | _ -> typed, next)

and inferLambdaWithExpected (env: TypeEnv) (state: InferState) (lambda: Expression) (args: (Identifier * TypeExpr option) list) (body: Expression) (expectedInputOpt: Ty option) (expectedReturnOpt: Ty option) : Result<TypedExpr * InferState, Diagnostic list> =
    // The parameters' annotations are read only when nothing else gives their types, but each must name types.
    let annotated =
        match ensureTypesExist state (args |> List.choose snd), expectedInputOpt with
        | Error errors, _ -> Error errors
        | Ok (), Some _ -> Ok state
        | Ok (), None ->
            args
            |> List.choose snd
            |> List.fold (fun result typeExpr -> result |> Result.bind (fun current -> inferTypeOfValues env current typeExpr)) (Ok state)
    // A parameter that takes no field of a named record it is given is an error at the parameter (see
    // `LambdaParameterFields`).
    // So is a lambda of several parameters given one that is no record (see `LambdaArity`).
    let fields =
        match expectedInputOpt |> Option.map (LambdaParameterFields.check state lambda args) with
        | Some (_ :: _ as errors) -> Error errors
        | _ ->
            match expectedInputOpt |> Option.map (fun input -> LambdaArity.check state lambda args input expectedReturnOpt) with
            | Some (_ :: _ as errors) -> Error errors
            | _ -> Ok ()
    annotated
    |> Result.bind (fun state -> fields |> Result.map (fun () -> state))
    |> Result.bind (fun state -> inferLambdaAfterTypeOfs env state lambda args body expectedInputOpt expectedReturnOpt)

and private inferLambdaAfterTypeOfs (env: TypeEnv) (state: InferState) (lambda: Expression) (args: (Identifier * TypeExpr option) list) (body: Expression) (expectedInputOpt: Ty option) (expectedReturnOpt: Ty option) : Result<TypedExpr * InferState, Diagnostic list> =
    let mutable current = state
    let mutable env' = env
    let argTys =
        match expectedInputOpt with
        | Some expectedInput ->
            let tys, next = argTypesFromExpected current lambda.Span args expectedInput
            current <- next
            // `{ ... }` ignores the parameter its expected type gives it, but `{ () -> ... }` says it takes unit.
            match lambda.Kind with
            | Lambda([], _, true) -> current <- addConstraintAt lambda.Span (TyPrimitive "unit") expectedInput current
            | _ -> ()
            tys
        | None ->
            args
            |> List.map (fun (_, typeOpt) ->
                let ty, next =
                    match typeOpt with
                    | Some typeExpr -> typeExprToTy current typeExpr
                    | None -> freshVar current
                current <- next
                ty)
    let mutable parameters = []
    for ((name, typeOpt), ty) in List.zip args argTys do
        let symbol, next = declareSymbol LambdaParameters lambda.Id name Parameter false (Some lambda.Span.End) current
        let bound, next = next |> recordSymbolType symbol ty |> recordAnnotated symbol typeOpt.IsSome |> MemoryCells.bindParameter symbol typeOpt ty
        current <- next
        parameters <- symbol :: parameters
        env' <- TypeEnv.declare name symbol (TypeEnv.mono bound) env'
    // `context l` names the instance of a context the lambda is given, for its body to load with `use l`.
    let named =
        match body.Kind with
        | ContextNames(names, _) -> ContextNames.bind env' current lambda body names
        | _ -> Ok (env', current)
    // A lambda that gives back a lambda, `{ a -> { b -> ... } }` checked against `a -> b -> c`, checks the one it gives
    // back against `b -> c`, so its parameters have their types while its body is inferred (#1749).
    let rec returnedLambda (expr: Expression) =
        match expr.Kind with
        | Lambda _ -> Some expr
        | Block statements -> SyntaxTree.blockValue statements |> Option.bind returnedLambda
        | _ -> None
    let expectReturned (current: InferState) =
        match returnedLambda body, expectedReturnOpt |> Option.map (resolveSoFar current >> Unifier.unalias) with
        | Some inner, Some (TyFunc _ as expected) -> ExpectedLiterals.expectLambda expected current inner
        | _, Some expected -> TypeMatches.expectResult body expected current
        | _ -> current
    named
    |> Result.bind (fun (env', current) -> inferExpr env' (LambdaContexts.enter lambda current |> expectReturned) body)
    |> function
    | Ok (bodyExpr, next) when (TypeDescriptions.escapingParameters next parameters bodyExpr.Type body.Span).IsSome ->
        Error [ (TypeDescriptions.escapingParameters next parameters bodyExpr.Type body.Span).Value ]
    | Ok (bodyExpr, next) ->
        let used, next = LambdaContexts.leave lambda next ||> fun used next -> MemoryCells.parameterContexts next lambda args |> fun (cells, next) -> used @ cells |> List.distinct, next
        let withReturnConstraint =
            match expectedReturnOpt with
            | Some expectedReturn -> addDeclaredConstraintAt body.Span bodyExpr.Type expectedReturn next
            | None -> next
        let inputTy =
            match expectedInputOpt with
            | Some expectedInput -> expectedInput
            | None -> inputTypeFromArgs argTys
        Ok (mkTypedExpr lambda (TyFunc(inputTy, bodyExpr.Type, used)) (Some bodyExpr) None None, withReturnConstraint)
        // Called directly for a lambda whose input is known, not only through `inferExpr`.
        |> recordExprType
    | Error err -> Error err

and inferBlock (env: TypeEnv) (state: InferState) (block: Expression) (statements: Statement list) : Result<TypedExpr * InferState, Diagnostic list> =
    match statements with
    | [] -> Ok (mkTypedExpr block (TyPrimitive "unit") None (Some []) None, state)
    | _ ->
        let mutable current = state
        let mutable env' = env
        let mutable lastExpr = mkTypedExpr (Expression.synthetic (LiteralExpr (IntLit 0I))) (TyPrimitive "unit") None None None
        let mutable typedStatements: TypedStatement list = []
        let mutable errors: Diagnostic list = []
        for statement in statements do
            match statement.Kind with
            | DefStatement(isExport, name, typeOpt, written) ->
                if errors.IsEmpty then
                    let expr =
                        match (if typeOpt.IsNone then pointFreeAsLambda env' current written else None) with
                        | Some lambda ->
                            current <- { current with ReadAs = current.ReadAs |> Map.add written.Id lambda }
                            lambda
                        | _ -> written
                    let pendingBefore = current.PendingContextInstances
                    let notSelfReferenced =
                        match LocalRecursion.selfReferences env' name expr with
                        | [] -> Ok ()
                        | selfReferences -> Error selfReferences
                    match DefAnnotations.read inferTypeOfValues None notSelfReferenced env' env' current name typeOpt expr with
                    | Error err -> errors <- err
                    | Ok (annotation, nextState) ->
                        let typeOpt, contextOpt, declaredTypeExprOpt, expectedDeclaredOpt, envWithCtx =
                            annotation.TypeOpt, annotation.Contexts, annotation.DeclaredTypeExpr, annotation.Declared, annotation.Env
                        let symbol, declaredState = declareSymbol DefValue expr.Id name LocalValue false (Some block.Span.End) nextState
                        let value = LocalRecursion.written expr
                        // Only a `rec` def's value can name the def (see `LocalRecursion`).
                        let selfVisible ty = if LocalRecursion.isRec expr then TypeEnv.declare name symbol (TypeEnv.mono ty) envWithCtx else envWithCtx
                        let preboundTy, preboundState, envWithRec, preboundFresh =
                            match expectedDeclaredOpt with
                            | Some declaredTy -> declaredTy, declaredState, selfVisible declaredTy, false
                            | None ->
                                let ty, st = freshVar declaredState
                                ty, st, selfVisible ty, true
                        let nextStateWithReq =
                            let written = recordWrittenType name typeOpt preboundState
                            match contextOpt with
                            | Some contexts -> registerLocalContextRequirement written symbol name contexts declaredTypeExprOpt
                            | None -> written
                        // A local function that writes its type runs in the contexts the type declares; one that does
                        // not runs in those of the def around it.
                        let outerScope = nextStateWithReq.ContextScope
                        let bodyState =
                            match typeOpt, value.Kind with
                            | Some _, Lambda _ -> { nextStateWithReq with ContextScope = DefScope(name, false) } |> LambdaContexts.markBody value
                            | _ -> nextStateWithReq
                        let bodyState = if preboundFresh then LambdaContexts.enterSelf preboundTy bodyState else bodyState
                        let inferResult =
                            inferDefValue envWithRec bodyState value annotation
                            |> Result.map (fun (typedExpr, next) -> typedExpr, { LambdaContexts.leaveSelf preboundTy typedExpr.Type next with ContextScope = outerScope })
                        match inferResult with
                        | Ok (typedExpr, next) ->
                            let declaredTy, finalState =
                                match expectedDeclaredOpt with
                                | Some declaredTy ->
                                    let valueTy, next = UseDischarge.fitInstance envWithCtx next name expr declaredTy typedExpr.Type
                                    declaredTy, TypeHoles.constrainToAnnotation expr declaredTypeExprOpt declaredTy valueTy next
                                | None ->
                                    let updatedState =
                                        if preboundFresh then addConstraintAt expr.Span preboundTy typedExpr.Type next else next
                                    typedExpr.Type, updatedState
                            current <-
                                finalState
                                |> recordSymbolType symbol declaredTy
                                |> recordAnnotated symbol expectedDeclaredOpt.IsSome
                            // Each `_` the def's contexts are applied to is the type its value made it, and so is one
                            // elsewhere in its type that the value made a variable the type names.
                            let typeOpt =
                                match DefAnnotations.settleHoles finalState name typeOpt with
                                | Ok settled when settled <> typeOpt ->
                                    let contexts, settledTypeExprOpt = extractDefContexts settled value
                                    current <- registerLocalContextRequirement (recordWrittenType name settled current) symbol name contexts.Value settledTypeExprOpt
                                    settled
                                | Ok _ -> typeOpt
                                | Error err ->
                                    errors <- err
                                    typeOpt
                            // A function named point-free finds its instances at the type the def writes.
                            if expectedDeclaredOpt.IsSome then
                                match resolveValueInstances envWithRec current expr with
                                | Ok resolved -> current <- resolved
                                | Error err -> errors <- err
                            // The rest of a workflow block after an `if` or `match` with keywords in its branches
                            // is bound to a local the branches all go on to. It is one continuation, not a function
                            // to use at different types: generalised, its failures would be closed to those of the
                            // rest alone, and a `try` in a branch could not add its own.
                            let scheme, generalizedState =
                                if WorkflowRests.isRest name then TypeEnv.mono declaredTy, current
                                // A cell holds values of one type: its own, not one for each use.
                                elif (match expr.Kind with MutSyntax.Call _ -> true | _ -> false) then TypeEnv.mono declaredTy, MemoryCells.declareCell env' symbol (NumericLiterals.settleWithin [ declaredTy ] (MemoryCells.typeMut expr declaredTy current))
                                else
                                    let scheme, generalized = ArithmeticInference.generalize (fun state -> generalizeDef env' state declaredTy) ArithmeticInference.LocalDef current
                                    // The parameterised contexts a local def that writes no type requires are part of
                                    // its type, as if written.
                                    if expectedDeclaredOpt.IsNone && contextOpt.IsNone then
                                        let earlier = pendingBefore |> List.map (fun p -> p.Call, p.Index) |> Set.ofList
                                        let calls = current.PendingContextInstances |> List.filter (fun p -> not (earlier.Contains (p.Call, p.Index)))
                                        match ContextInference.unfixedValueType current name (declarationSpan current DefValue expr) declaredTy calls with
                                        | [] -> ()
                                        | err -> errors <- err
                                        scheme, ContextInference.resolveLocal name symbol scheme pendingBefore generalized
                                    else scheme, generalized
                            current <-
                                let used =
                                    [ WellKnownContexts.equality; WellKnownContexts.ordering ]
                                    |> List.map (fun wellKnown -> wellKnown.Context, WellKnownContexts.usedByLocal wellKnown generalizedState scheme.Quantified)
                                    |> List.filter (snd >> Set.isEmpty >> not)
                                    |> Map.ofList
                                match symbol, used with
                                | Some symbol, compared when not compared.IsEmpty ->
                                    { generalizedState with LocalComparisons = generalizedState.LocalComparisons |> Map.add symbol compared }
                                | _ -> generalizedState
                            env' <- TypeEnv.declare name symbol scheme env'
                            typedStatements <- typedStatements @ [ TypedDefStatement(isExport, name, typeOpt, typedExpr) ]
                            lastExpr <- typedExpr
                        | Error err -> errors <- err
            | ImportStatement items ->
                typedStatements <- typedStatements @ [ TypedImportStatement items ]
            // An attribute is for the linters (see `Allows`), and `Attributes` has checked it.
            | AttributeStatement _ -> ()
            | TypeDefStatement(name, modifiers, parameters, body) ->
                current <- registerTypeDef current name modifiers parameters body
                if errors.IsEmpty then errors <- DefAnnotations.typeDefErrors current name modifiers parameters body
                typedStatements <- typedStatements @ [ TypedTypeDefStatement(name, modifiers, parameters, body) ]
            | UseStatement value when errors.IsEmpty && (ContextNames.loaded env' current value).IsSome ->
                let scheme, instance = (ContextNames.loaded env' current value).Value
                current <- ContextNames.recordLoad value scheme instance current
                env' <- ContextNames.load env' instance
                typedStatements <- typedStatements @ [ TypedUseStatement (mkTypedExpr value scheme.Type None None None) ]
            | UseStatement value when errors.IsEmpty && not (UseDischarge.notLoadable env' current value).IsEmpty ->
                errors <- UseDischarge.notLoadable env' current value
            | UseStatement value ->
                if errors.IsEmpty then
                    match inferExpr env' (current |> UseDischarge.mark value |> LambdaContexts.enterUse value) value with
                    | Ok (typedExpr, next) ->
                        let withFields = extendEnvWithRecordFields (FromUse value.Id) next env' typedExpr.Type
                        let withUse, next = useInstance withFields next value typedExpr.Type
                        let scopeErrors, withUse = ScopedCalls.instanceInScope withUse next value
                        match ownInstanceErrors withFields withUse next value @ scopeErrors with
                        | _ :: _ as replaced -> errors <- replaced
                        | [] ->
                            current <- next
                            env' <- withUse
                            typedStatements <- typedStatements @ [ TypedUseStatement typedExpr ]
                    | Error err -> errors <- err
            | ExprStatement expr ->
                if errors.IsEmpty then
                    let inferred =
                        ExpectedLiterals.blockResultOf current expr
                        |> Option.bind (CollectionLiterals.targeted inferExpr env' current expr)
                        |> Option.defaultWith (fun () -> inferExpr env' current expr)
                    match inferred with
                    | Ok (typedExpr, next) ->
                        current <- next
                        typedStatements <- typedStatements @ [ TypedExprStatement typedExpr ]
                        lastExpr <- typedExpr
                    | Error err -> errors <- err
        if errors.IsEmpty then errors <- BlockResult.check statements
        if errors.IsEmpty then
            current <- WorkflowRests.joinArguments env' statements current
            let localDefs = statements |> List.choose (fun statement -> match statement.Kind with DefStatement(_, name, _, _) -> Some name | _ -> None)
            current <- leaveLocalContextRequirements state localDefs current
            let blockExpr = mkTypedExpr block lastExpr.Type (Some lastExpr) (Some typedStatements) None
            Ok (blockExpr, current)
        else
            Error errors
