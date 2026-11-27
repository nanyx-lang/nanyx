/// Inferring a whole module: its type definitions, imports and definitions, in order, with the checks that need them
/// all, such as the contexts a definition infers and whether an annotation is as general as the definition.
module NyxCompiler.InferModule

open System
open Parser.Program
open NyxCompiler
open NyxCompiler.InferState
open NyxCompiler.Visibility
open NyxCompiler.BranchJoin
open NyxCompiler.TypeResolution
open NyxCompiler.Contexts
open NyxCompiler.ContextInstances
open NyxCompiler.Infer

/// The type variables of a def's annotation are rigid: the body has to work for every type they stand for.
/// Once the constraints gathered so far are solved, each must still be a variable of its own. One bound to
/// a concrete type, or to the same type as another of the annotation's variables, means the annotation is
/// more general than the body, which would otherwise quietly narrow it. So must each variable that stands for an
/// associated type of a context it requires (`associated`), which the instance fixes: `fraction` in `[Num(a)] a -> a`
/// is not `a`.
let private checkAnnotationIsRigid (state: InferState) (name: Identifier) (span: SourceSpan) (typeExpr: TypeExpr) (associated: TyVar list) : Result<unit, Diagnostic list> =
    let vars =
        annotationVarNames typeExpr
        |> List.choose (fun varName -> state.TypeVars |> Map.tryFind (WrittenVar varName) |> Option.map (fun var -> varName, var))
        |> fun written -> written @ (associated |> List.choose (fun var -> var.Name |> Option.map (fun varName -> varName, var)))
        |> List.distinctBy fst
    if vars.IsEmpty then
        Ok ()
    else
        // A conflict is reported when the module's constraints are solved, and set aside here, so that it does not hide
        // this check of a def after it.
        match Solving.unify (Solving.agreeing state.Constraints) with
        | Error _ -> Ok ()
        | Ok subst ->
            let resolved = vars |> List.map (fun (varName, var) -> varName, Unifier.apply subst (TyVar var))
            // Show the variables an annotation variable resolved to by that annotation variable's name.
            let displayNames =
                resolved
                |> List.fold (fun acc (varName, ty) ->
                    match ty with
                    | TyVar image when not (acc |> Map.containsKey image.Id) -> acc |> Map.add image.Id (TyPrimitive varName)
                    | _ -> acc) Map.empty
            let problems =
                resolved
                |> List.mapi (fun index (varName, ty) ->
                    match ty with
                    | TyVar _ ->
                        resolved
                        |> List.take index
                        |> List.tryFind (fun (_, otherTy) -> otherTy = ty)
                        |> Option.map (fun (otherName, _) -> $"'{varName}' would have to be '{otherName}'")
                    // What failed to check was reported where it failed: it is not another error here.
                    | concrete when Unifier.holdsError concrete -> None
                    | concrete -> Some $"'{varName}' would have to be '{Unifier.typeText state.TypeDefs (Unifier.apply displayNames concrete)}'")
                |> List.choose id
            if problems.IsEmpty then
                Ok ()
            else
                let details = Diagnostics.listed problems
                Error [ Diagnostics.at DiagnosticCodes.AnnotationTooGeneral span $"The annotation of '{name}' is more general than its inferred type, where {details}" |> Diagnostics.about name ]

/// `item` with the ambient contexts it was inferred to require (`required`, by the def's name) in its annotation, where
/// it is a def that does not write its type: after the parameterised contexts it was given, or on their own
/// (`[Console]`). The backend passes their members to it, as it does to a def that writes them, so that whatever is
/// in scope where it is called (the host's `use`, say) reaches it.
let private withInferredAmbientContexts (required: Map<Identifier, Set<Identifier>>) (item: TypedTopLevelItem) =
    match item with
    | TypedDef (TypedValueDef(isExport, name, annotation, typedExpr)) ->
        match required |> Map.tryFind name with
        | Some contexts when not contexts.IsEmpty ->
            let ambient = contexts |> Set.toList |> List.map (TypeName >> TypeExpr.synthetic)
            let annotation =
                match annotation with
                | Some (Kind (TypeWithContext(parameterised, signature))) -> TypeWithContext(parameterised @ ambient, signature)
                | Some (Kind (TypeContext parameterised)) -> TypeContext(parameterised @ ambient)
                | Some signature -> TypeWithContext(ambient, signature)
                | None -> TypeContext ambient
            TypedDef (TypedValueDef(isExport, name, Some (TypeExpr.synthetic annotation), typedExpr))
        | _ -> item
    | _ -> item

/// `state` with the type of a script's entry point (`InferState.ScriptEntry`) requiring the ambient contexts that the
/// unannotated functions and values its statements use need (`uses`, `required`, by name), and those that the values
/// that run in it (`ScriptEntryValues`) need: known only once the module is inferred, so not yet in the type the lambda
/// was given. The host then passes their members to the entry point, which passes them on.
let private withEntryContexts (required: Map<Identifier, Set<Identifier>>) (uses: DeferredContextUse list) (state: InferState) =
    match state.ScriptEntry |> Option.bind (fun lambda -> state.ExprTypes |> Map.tryFind lambda |> Option.map (fun ty -> lambda, ty)) with
    | Some (lambda, TyFunc(input, output, own)) ->
        let needed =
            (uses |> List.filter (fun used -> used.Caller = AmbientScope) |> List.map (fun used -> required.[used.Callee]))
            @ (state.ScriptEntryValues |> Seq.map (fun name -> required.[name]) |> List.ofSeq)
            |> Set.unionMany
            |> Set.toList
            |> List.filter (WrittenContexts.isScoped state >> not)
            |> List.map (fun ctxName -> TyApply(ctxName, []))
            |> List.filter (fun context -> not (List.contains context own))
        if needed.IsEmpty then state
        else { state with ExprTypes = state.ExprTypes |> Map.add lambda (TyFunc(input, output, own @ needed)) }
    | _ -> state

/// The type variables the signature whose contexts are `contexts`, read into `state`, names the associated types of
/// those contexts by (see `InferState.AssociatedVariables`).
let private associatedVariables (state: InferState) (contexts: TypeExpr list option) : TyVar list =
    contexts
    |> Option.defaultValue []
    |> List.collect (fun ctx ->
        match ctx.Kind with
        | TypeApply(ctxName, args) ->
            let associated = tryFindTypeDef ctxName state |> Option.map _.AssociatedTypes.Length |> Option.defaultValue 0
            args
            |> List.skip (args.Length - min associated args.Length)
            |> List.choose (fun arg ->
                match arg.Kind with
                | TypeVar name -> state.TypeVars |> Map.tryFind (WrittenVar name)
                | _ -> None)
        | _ -> [])

/// `scheme`, the type of a def that writes it with the contexts `contexts`, generic in the associated types of those
/// contexts too, though its type may not name them: `elem` in `[Iterable(i)] i -> ()`. A context that the def's value
/// needs of one, as a hole `"{item}"` needs `ToString(Iterable(i).elem)`, must be written then, as for any of its type
/// variables.
let private withAssociatedQuantified (state: InferState) (contexts: TypeExpr list option) (scheme: TypeScheme) : TypeScheme =
    let associated =
        associatedVariables state contexts
        |> List.map (TyVar >> resolveSoFar state >> TypeEnv.freeTyVars)
        |> Set.unionMany
    { scheme with Quantified = Set.union scheme.Quantified associated }

/// Infers a module in `initialEnv`, carrying on past an item that fails to check: the types and items of those
/// that do, and the diagnostics of those that do not. The constraints are not solved here, so a type mismatch is
/// not among them.
let inferModuleWithEnv (initialEnv: TypeEnv) (initialState: InferState) (module': Module) : Map<string, Ty> * TypedTopLevelItem list * InferState * Diagnostic list =
    let mutable env = initialEnv
    let mutable state = initialState
    // Each def's annotation has type variables of its own: `b` in one def's type is not `b` in another's, so a def
    // whose body fails to fit its annotation cannot solve another def's variables (#453).
    let beginDef () = state <- { state with TypeVars = initialState.TypeVars }
    let mutable currentModule: string option = None
    let mutable types = Map.empty
    // Each typed item with the index of the item it was checked from, which may be out of order (see `DefinitionOrder`),
    // the last emitted first.
    let mutable items: (int * TypedTopLevelItem) list = []
    let mutable itemIndex = 0
    let emit item = items <- (itemIndex, item) :: items
    // A name defined again is reported, and only its first definition is inferred.
    let module', duplicateErrors = DuplicateDefinitions.check initialState.Declared module'
    let mutable errors: Diagnostic list =
        duplicateErrors @ InitialisationCycles.check initialState.Declared module' @ AttachedOperators.redefinitions initialEnv initialState module'

    // An imported context whose every member is also an imported definition of that name is an ambient instance:
    // the one the runtime supplies, as the host's prelude's definitions are its `Console`. The top level of the module
    // runs in it; a def runs in it only if it declares the context, or does not write its type and so infers it.
    state <-
        { state with
            AmbientMembers =
                state.TypeDefs
                |> Map.toList
                |> List.collect (fun (ctxName, info) ->
                    let bindings =
                        info.Members
                        |> Map.toList
                        |> List.map (fun (memberName, memberSymbol) ->
                            let memberName = AmbientMember.definitionName ctxName memberName
                            memberName,
                            TypeEnv.tryFind memberName env
                            |> Option.bind (fun scheme -> scheme.Symbol)
                            |> Option.filter (fun symbol -> symbol <> memberSymbol))
                    if bindings.IsEmpty || not info.Parameters.IsEmpty || bindings |> List.exists (snd >> Option.isNone) then []
                    else bindings |> List.map (fun (memberName, symbol) -> memberName, (symbol.Value, ctxName)))
                |> Map.ofList }

    // Pre-bind every top-level value before inferring any body, so a definition can refer to one
    // declared later in the module (forward references, mutual recursion). An annotated def is bound
    // at its declared type; an unannotated one at a fresh variable that its inferred type is unified
    // with once its body has been checked. Type definitions are registered first, each after the types it names (see
    // `inDependencyOrder`), so an annotation or another type can name a type declared further down.
    // A block the file imports (`import money`) brings its exports in under other names: each is bound wherever the
    // definition it stands for is, so it has the same type and refers to the same symbol.
    // A block can re-export another file's module, whose names are bound here already, by its imports.
    let importedBy (item: ImportItem) =
        let brought = state.ImportSites |> Map.filter (fun _ sites -> sites |> List.contains item.Span)
        let values = env.Names |> Map.toList |> List.choose (fun (name, scheme) -> scheme.Symbol |> Option.filter brought.ContainsKey |> Option.map (fun _ -> name))
        let types = state.TypeDefs |> Map.toList |> List.choose (fun (name, info) -> info.Symbol |> Option.filter brought.ContainsKey |> Option.map (fun _ -> name))
        values @ types
    let blockImported =
        blockImports state.ModuleBlocks module'
        |> List.map (fun blockImport -> { blockImport with Names = blockAliases state.ModuleBlocks module' importedBy blockImport |> Map.ofList })
    errors <- errors @ (blockImported |> List.collect (unexportedBlockSelections state.ModuleBlocks module' importedBy))
    // In a file that declares a module, a block's definitions and types can be named without the file's module in front
    // too (`coins.penny` for `money.coins.penny`), as a script's are.
    let fileModule =
        module'
        |> List.tryPick (function
            | Kind (ModuleDecl(_, name)) as item when not (state.ModuleBlocks |> List.exists (fun (_, block) -> block.Start = item.Span.Start)) -> Some name
            | _ -> None)
    let blockShortNames =
        match fileModule with
        | Some fileName when not state.ModuleBlocks.IsEmpty ->
            module'
            |> List.choose (fun item ->
                match item.Kind with
                | Def (ValueDef(_, name, _, _))
                | Def (TypeDef(name, _, _, _)) when name.StartsWith(fileName + ".") && (blockAround state.ModuleBlocks item.Span).IsSome ->
                    Some (name.Substring(fileName.Length + 1), name)
                | _ -> None)
            |> Map.ofList
        | _ -> Map.empty
    let aliases = blockImported |> List.fold (fun acc blockImport -> Map.fold (fun acc alias qualified -> Map.add alias qualified acc) acc blockImport.Names) blockShortNames
    state <- { state with BlockImports = blockImported; BlockAliases = aliases }
    // A name that stands for another file's definition, which a block re-exports, is bound to it as the import is.
    env <-
        aliases
        |> Map.fold (fun acc alias qualified ->
            match TypeEnv.tryFind qualified initialEnv with
            | Some scheme when alias <> qualified -> TypeEnv.declare alias scheme.Symbol scheme acc
            | _ -> acc) env
    let aliasesOf =
        aliases |> Map.toList |> List.groupBy snd |> List.map (fun (qualified, pairs) -> qualified, List.map fst pairs) |> Map.ofList
    let declareTopLevel (name: string) symbol scheme (env: TypeEnv) =
        aliasesOf
        |> Map.tryFind name
        |> Option.defaultValue []
        |> List.fold (fun acc alias -> TypeEnv.declare alias symbol scheme acc) (TypeEnv.declare name symbol scheme env)
    let withoutTopLevel (name: string) (env: TypeEnv) =
        aliasesOf |> Map.tryFind name |> Option.defaultValue [] |> List.fold (fun acc alias -> TypeEnv.remove alias acc) (TypeEnv.remove name env)

    // A value of a module without a `module` line is a script's, computed at the top level, unless it is in a module
    // block, which is a named module.
    let isScriptValue (value: Expression) =
        not (module' |> List.exists (function Kind (ModuleDecl _) -> true | _ -> false))
        && (blockAround state.ModuleBlocks value.Span).IsNone

    let mutable preboundFreshVars: Map<string, Ty> = Map.empty
    let prebind (item: TopLevelItem) =
        match item with
        | Kind (Def (ValueDef(isExport, name, typeOpt, value))) ->
            beginDef ()
            let symbol, declared = declareSymbol DefValue value.Id name TopLevelValue isExport None state
            // The module's own def wins over an imported one of the same name, and requires only its own contexts.
            state <- { declared with ContextRequirements = declared.ContextRequirements |> Map.remove name }
            match typeOpt, value.Kind, symbol with
            | None, Lambda _, Some symbol -> state <- { state with UnannotatedDefs = state.UnannotatedDefs |> Map.add name symbol }
            | _, Lambda _, _ -> ()
            | _, _, Some symbol when isScriptValue value ->
                state <- { state with ScriptValues = state.ScriptValues |> Map.add name symbol }
            | _ -> ()
            let contextOpt, declaredTypeExprOpt = extractDefContexts typeOpt value
            // The plain contexts its type writes are known before it is inferred, so a def inferred first, as one in a
            // cycle with it is, finds them where it calls it: a lambda there requires them (#2610). One with type
            // arguments is read with the def's type, which may be wrong, so it is registered as the def is inferred.
            match contextOpt |> Option.map (List.filter (fun ctx -> match ctx.Kind with TypeName _ -> true | _ -> false)) with
            | Some (_ :: _ as plain) -> state <- registerContextRequirement state name plain declaredTypeExprOpt
            | _ -> ()
            match declaredTypeExprOpt with
            // An annotation with a `typeof` can only be read once the values it names are in scope, so the def is
            // pre-bound as an unannotated one is, and its annotation is unified with that once it has been read. So is
            // one with a `_`, which generalised here would stand for any type, as a named variable does.
            | Some declaredExpr when (typeOfValues declaredExpr).IsEmpty && not (TypeHoles.hasHoles declaredExpr) ->
                let ty, st = typeExprToTy state declaredExpr
                state <- st
                env <- declareTopLevel name symbol (TypeEnv.generalize env ty) env
            | _ ->
                let ty, st = freshVar state
                state <- st
                preboundFreshVars <- preboundFreshVars |> Map.add name ty
                env <- declareTopLevel name symbol (TypeEnv.mono ty) env
        | _ -> ()
    // A dimension is registered as the private nominal type it is (see `Units`), and its units after every type.
    for name, modifiers, parameters, body in inDependencyOrder (Units.withDimensionsAsTypes module') do
        state <- registerTypeDef state name modifiers parameters body
    state <- Units.markDimensions module' state
    // A derived dimension, `dimension Speed = Length / Time`, is registered again once the dimensions are marked as such,
    // so that what it names is read as dimensions (see `Units.withDimensionsAsTypes`).
    let derived = module' |> List.filter (fun item -> match item.Kind with DimensionDecl(_, _, _, Some _) -> true | _ -> false)
    if not derived.IsEmpty then
        for name, modifiers, parameters, body in inDependencyOrder (Units.withDimensionsAsTypes derived) do
            state <- registerTypeDef state name modifiers parameters body
        state <- Units.markDimensions module' state
    let withUnits, unitErrors = Units.declare (hasInstance env state "Num") state module'
    state <- withUnits
    errors <- errors @ unitErrors
    for item in module' do
        prebind item

    // The exported types of an imported block can be written under the import's names too (`Money`, `m.Money`).
    state <-
        { state with
            TypeDefs =
                aliases
                |> Map.fold (fun acc alias qualified ->
                    match state.TypeDefs |> Map.tryFind qualified with
                    | Some info -> acc |> Map.add alias info
                    | None -> acc) state.TypeDefs }

    // A def that fails to check is reported, and bound at the error type from then on, so its uses are not errors too:
    // its annotation may be what failed.
    //
    // It stays in the module, failed as a whole, so a debug build can defer it: a function's body traps, so calls to
    // it still compile, and a value traps where it is computed. It keeps no annotation.
    let abandon isExport (name: string) (expr: Expression) (diagnostics: Diagnostic list) =
        let failedNode =
            match expr.Kind with
            | Lambda(_, body, _) -> body
            | _ -> expr
        // Its type is the error type, which requires no contexts: a call to it gets that type and no more errors.
        state <- forgetContextRequirement { state with FailedExprs = (failedNode.Id, diagnostics) :: state.FailedExprs } name
        types <- types |> Map.add name Unifier.errorType
        emit (TypedDef (TypedValueDef(isExport, name, None, mkTypedExpr expr Unifier.errorType None None None)))
        match TypeEnv.tryFind name env with
        | Some scheme -> env <- declareTopLevel name scheme.Symbol (TypeEnv.mono Unifier.errorType) env
        | None -> ()

    let check (item: TopLevelItem) =
        match item.Kind with
        // A cell, `mut x = 0`, which no `memory` block can be around at the top level.
        | Def (ValueDef(isExport, name, _, ({ Kind = MutSyntax.Call _ } as written))) ->
            beginDef ()
            let message =
                $"A cell can't be declared at the top level, where no `memory` block can be around it: declare '{name}' inside a `memory {{ ... }}` block, in the body of a def"
            abandon isExport name written [ Diagnostics.at DiagnosticCodes.TopLevelCell { written.Span with End = written.Span.Start + 3 } message |> Diagnostics.about name ]
        | Def (ValueDef(isExport, name, typeOpt, written)) ->
            beginDef ()
            // `def p = println` is read as the lambda that calls `println`, so that it requires `ToString` at its own
            // type (see `pointFreeAsLambda`). It is an unannotated function, then, not a script's value, as the
            // pre-binding pass took it to be from its syntax: what it is matters only where it is used, which is
            // checked after it (see `DefinitionOrder`).
            let expr =
                match (if typeOpt.IsNone then pointFreeAsLambda env state written else None) with
                | Some lambda ->
                    let unannotated =
                        match TypeEnv.tryFind name env |> Option.bind _.Symbol with
                        | Some symbol ->
                            { state with
                                ScriptValues = state.ScriptValues |> Map.remove name
                                UnannotatedDefs = state.UnannotatedDefs |> Map.add name symbol }
                        | None -> state
                    state <- { unannotated with ReadAs = unannotated.ReadAs |> Map.add written.Id lambda }
                    lambda
                | None -> written
            let pendingBefore = state.PendingContextInstances |> List.map (fun p -> p.Call, p.Index) |> Set.ofList
            // A def's own name is not in scope in its annotation: `def x: typeof x` says nothing.
            match DefAnnotations.read inferTypeOfValues currentModule (Ok ()) (withoutTopLevel name env) env state name typeOpt expr with
            | Error err -> abandon isExport name expr err
            | Ok (annotation, nextState2) ->
                state <- annotation.Checked
                let typeOpt, contextOpt, declaredTypeExprOpt, expectedDeclaredOpt, envWithCtx =
                    annotation.TypeOpt, annotation.Contexts, annotation.DeclaredTypeExpr, annotation.Declared, annotation.Env
                // The name is already in `env` from the pre-binding pass: at its declared type, or at the
                // fresh variable recorded in `preboundFreshVars`. While the body is checked, the def sees itself at
                // its declared type, generic in the variables its annotation writes that no context it requires
                // names, so it can call itself at other types (`encodeAt(field, f.type)` walks a value's parts). An
                // annotation with a `typeof` or a `_` is not all known yet, so the def sees itself at it as it is.
                // The symbol the pre-binding pass made.
                let symbol, _ = declareSymbol DefValue expr.Id name TopLevelValue isExport None state
                let envWithRec =
                    match expectedDeclaredOpt with
                    | Some declaredTy when preboundFreshVars.ContainsKey name -> TypeEnv.declare name symbol (TypeEnv.mono declaredTy) envWithCtx
                    | Some declaredTy ->
                        // A union's rest and a context variable stay the def's own: a call's are what it is given.
                        let general = TypeEnv.generalize envWithCtx declaredTy
                        let kept = Set.union (Unifier.restVarIds true declaredTy) (FunctionContexts.variablesIn declaredTy |> Map.keys |> Set.ofSeq)
                        TypeEnv.declare name symbol { general with Quantified = Set.difference general.Quantified kept } envWithCtx
                    | None -> envWithCtx
                let nextStateWithReq =
                    let written = recordWrittenType name typeOpt nextState2 |> ParameterTypes.record symbol declaredTypeExprOpt expr
                    match contextOpt with
                    | Some contexts -> registerContextRequirement written name contexts declaredTypeExprOpt
                    | None -> written
                // A function runs in the contexts its type declares, or infers them. A script's value is computed at
                // the top level, in the ambient ones, and runs in its entry point if it needs any; any other module's is
                // built where there are none.
                let bodyState =
                    match expr.Kind with
                    | Lambda _ -> { nextStateWithReq with ContextScope = DefScope(name, typeOpt.IsNone) } |> LambdaContexts.markBody expr
                    | _ when currentModule.IsSome || (blockAround state.ModuleBlocks expr.Span).IsSome ->
                        { nextStateWithReq with ContextScope = ValueScope name }
                    | _ when state.ScriptValues.ContainsKey name -> { nextStateWithReq with ContextScope = ScriptValueScope name }
                    | _ -> nextStateWithReq
                let inferResult =
                    inferDefValue envWithRec bodyState expr annotation
                    |> Result.map (fun (typedExpr, next) -> typedExpr, { next with ContextScope = AmbientScope })
                match inferResult with
                | Ok (typedExpr, next) ->
                    // An unannotated def names the context variables it was found to use (see `ContextInference`).
                    let declaredTy, finalState, contextVariables =
                        match expectedDeclaredOpt with
                        | Some declaredTy ->
                            declaredTy, TypeHoles.constrainToAnnotation expr declaredTypeExprOpt declaredTy typedExpr.Type next, []
                        | None ->
                            let updatedState =
                                match preboundFreshVars |> Map.tryFind name with
                                | Some preboundTy -> addConstraintAt expr.Span preboundTy typedExpr.Type next
                                | None -> next
                            let names, named = ContextInference.nameContextVariables name typedExpr.Type updatedState
                            typedExpr.Type, named, names
                    // An annotated def pre-bound at a fresh variable (one with a `typeof`) is that annotation.
                    let finalState =
                        match expectedDeclaredOpt, preboundFreshVars |> Map.tryFind name with
                        | Some _, Some preboundTy -> addConstraintAt expr.Span preboundTy declaredTy finalState
                        | _ -> finalState
                    // The def's number literals that nothing has settled are of their default types before its
                    // annotation is checked against its value (see `NumericLiterals`).
                    let finalState = Fractions.settleLiterals (fun _ -> true) finalState
                    state <-
                        finalState
                        |> recordSymbolType symbol declaredTy
                        |> recordAnnotated symbol expectedDeclaredOpt.IsSome
                    // Each `_` the def's contexts are applied to is the type its value made it, and so is one elsewhere
                    // in its type that the value made a variable the type names.
                    let settledType = DefAnnotations.settleHoles finalState name typeOpt
                    let typeOpt, contextOpt =
                        match settledType with
                        | Ok settled when settled <> typeOpt ->
                            let contextOpt, settledTypeExprOpt = extractDefContexts settled expr
                            state <- registerContextRequirement (recordWrittenType name settled state) name contextOpt.Value settledTypeExprOpt
                            settled, contextOpt
                        | _ -> typeOpt, contextOpt
                    let rigidResult =
                        match declaredTypeExprOpt with
                        | Some declaredExpr -> checkAnnotationIsRigid finalState name (declarationSpan finalState DefValue expr) declaredExpr (associatedVariables finalState contextOpt)
                        | None -> Ok ()
                    // A function named point-free finds its instances at the type the def writes.
                    let valueResolved =
                        if expectedDeclaredOpt.IsSome then resolveValueInstances envWithRec state expr else Ok state
                    // A value that is not a function can't require a context of a type a later use would fix. Its
                    // calls are not left to the end of the module, which would report them again.
                    let unfixed =
                        if expectedDeclaredOpt.IsNone && contextOpt.IsNone then
                            let isOwn (p: PendingContextInstance) = not (pendingBefore.Contains (p.Call, p.Index))
                            let calls = state.PendingContextInstances |> List.filter isOwn
                            match ContextInference.unfixedValueType state name (declarationSpan state DefValue expr) declaredTy calls with
                            | [] -> Ok ()
                            | err ->
                                state <- { state with PendingContextInstances = state.PendingContextInstances |> List.filter (isOwn >> not) }
                                Error err
                        else Ok ()
                    match rigidResult |> Result.bind (fun () -> unfixed) |> Result.bind (fun () -> Result.map ignore settledType) |> Result.bind (fun () -> valueResolved) |> Result.bind (fun state -> synthesizeSettledInstances env state name) with
                    | Error err -> abandon isExport name expr err
                    | Ok synthesizedState ->
                        state <- synthesizedState
                        // Generalize against an env without this def's own pre-binding, so the fresh
                        // variable it was pre-bound at does not stop its type being generalized. It is resolved
                        // first, as a local def's is: `{ x -> x + 1 }` is `α -> α` until its constraints are
                        // applied, and generalising that would make every use `α -> α` too.
                        let generalize state =
                            let scheme, generalized = generalizeDef (withoutTopLevel name env) state declaredTy
                            if expectedDeclaredOpt.IsSome then withAssociatedQuantified generalized contextOpt scheme, generalized
                            else scheme, generalized
                        let scheme, generalizedState =
                            ArithmeticInference.generalize generalize (ArithmeticInference.TopLevelDef name) state
                        // The parameterised contexts an unannotated def requires are part of its type, as if written.
                        match resolvePendingInstances env generalizedState name symbol scheme with
                        | Error err ->
                            state <- generalizedState
                            abandon isExport name expr err
                        | Ok (inferredAnnotation, resolvedState) ->
                            // A def that writes parameterised contexts but not its type is given the type it would
                            // have written, which a call reads the contexts' arguments from.
                            let annotation, resolvedState =
                                match contextOpt, declaredTypeExprOpt with
                                | Some contexts, None when contexts |> List.exists (isParameterizedContext resolvedState) ->
                                    match ContextInference.writtenType resolvedState scheme with
                                    | Some signature ->
                                        Some (TypeExpr.synthetic (TypeWithContext(contexts, signature))),
                                        registerContextRequirement resolvedState name contexts (Some signature)
                                    | None -> typeOpt, resolvedState
                                | _ -> Option.orElse inferredAnnotation typeOpt, resolvedState
                            // The context variables it names are among the contexts it requires, after any others, so
                            // that a call binds them.
                            let written = resolvedState.ContextRequirements |> Map.tryFind name |> Option.defaultValue []
                            state <-
                                match contextVariables with
                                | [] -> resolvedState
                                | names -> { resolvedState with ContextRequirements = resolvedState.ContextRequirements |> Map.add name (written @ (names |> List.map (TypeVar >> TypeExpr.synthetic))) }
                            let scheme =
                                if expectedDeclaredOpt.IsSome then scheme
                                else
                                    let scheme, closed = NeverReturns.close state name expr.Span scheme
                                    state <- closed
                                    scheme
                            env <- declareTopLevel name symbol scheme env
                            types <- types |> Map.add name declaredTy
                            state <- { state with AssociatedVariables = state.AssociatedVariables @ associatedVariables state contextOpt }
                            emit (TypedDef (TypedValueDef(isExport, name, annotation, typedExpr)))
                            // The next def's constraints are solved on top of this one's where they share nothing.
                            Solving.checkpoint state.Constraints
                | Error err -> abandon isExport name expr err
        // A block is a module, not a program, so it has no top-level code.
        | Expr expr when (blockAround state.ModuleBlocks expr.Span).IsSome ->
            let blockName = fst (blockAround state.ModuleBlocks expr.Span).Value
            errors <- errors @ [ Diagnostics.at DiagnosticCodes.CodeInModuleBlock expr.Span $"'{blockName}' is a module block, which can only hold definitions, types and imports, not code to run" |> Diagnostics.about blockName ]
        // A statement has type variables of its own too: a scope the def before it wrote is not one its own
        // annotations name.
        | Expr expr ->
            beginDef ()
            match inferExpr env state expr with
            | Ok (typedExpr, next) ->
                state <- next
                emit (TypedExprItem typedExpr)
            | Error err -> errors <- errors @ err
        | Import itemsList -> emit (TypedImport itemsList)
        | ReExport item -> emit (TypedImport [ item ])
        | ModuleDecl(_, name) ->
            currentModule <- Some name
            emit (TypedModuleDecl name)
        | ModuleBlock _ -> ()
        // Attributes are resolved by the compiler front end before inference (see Attributes.applyAttributes).
        | Attribute _ -> ()
        // A dimension is the type it is registered as, and a unit is in `state.Units` (see `Units.declare`).
        | DimensionDecl _ ->
            match (Units.withDimensionsAsTypes [ item ]).Head.Kind with
            | Def (TypeDef(name, modifiers, parameters, body)) -> emit (TypedDef (TypedTypeDef(name, modifiers, parameters, body)))
            | _ -> ()
        | UnitDecl _ -> ()
        | Def (TypeDef(name, modifiers, parameters, body)) ->
            // Registered in the pre-binding pass above.
            errors <- errors @ DefAnnotations.typeDefErrors state name modifiers parameters body
            emit (TypedDef (TypedTypeDef(name, modifiers, parameters, body)))

    // A def is checked after the defs it uses that are not in a cycle with it, so that it sees their types. Each item
    // is checked in the module it is written in.
    let itemsByIndex = Array.ofList module'
    let inferredAfter = DefinitionOrder.inferredAfter module'
    let moduleBefore =
        module'
        |> List.scan (fun current (item: TopLevelItem) ->
            match item.Kind with
            | ModuleDecl(_, name) -> Some name
            | _ -> current) currentModule
        |> Array.ofList
    let mutable checkedItems = Set.empty
    let rec checkInOrder index =
        if not (checkedItems.Contains index) then
            checkedItems <- checkedItems.Add index
            for used in inferredAfter |> Map.tryFind index |> Option.defaultValue [] do
                checkInOrder used
            itemIndex <- index
            currentModule <- moduleBefore.[index]
            check itemsByIndex.[index]
    for index in 0 .. itemsByIndex.Length - 1 do
        checkInOrder index
    currentModule <- Array.last moduleBefore
    itemIndex <- itemsByIndex.Length

    // The holes left to the end of the module (see `InterpolationHoles.resolveLater`), now that every type is known.
    // An instance derived from a type's parts is a definition of its own (see `StructuralInstances`), inferred here
    // with the module's: its holes may be left to the end too, and may derive more, until none are.
    let mutable settling = true
    while settling do
        match resolvePendingInstances env state InterpolationHoles.moduleEnd None (TypeEnv.mono (TyPrimitive "unit")) with
        | Ok (_, resolved) -> state <- resolved
        | Error err -> errors <- errors @ err
        let derived, next = StructuralInstances.takeDefinitions state
        state <- next
        for item in derived do
            prebind item
        for item in derived do
            check item
        settling <- not derived.IsEmpty
    state <- { StructuralInstances.callInstances state with HoleScopes = Map.empty }

    do
        // The ambient contexts each unannotated function needs: those its body uses, and those the unannotated
        // functions it uses need that nothing nearer supplies, until no more are found. So for a script's values, which
        // need the contexts of the functions and values they use too; a function that names such a value is an error.
        let uses = List.rev state.DeferredContextUses
        // What a use needs that is not available where it is. A scoped context, Memory, is not among them: a call is
        // given the one of the cell it passes, by the block the cell is in or the parameter it came from, and that is
        // checked at the call (see `ScopedCalls`).
        let neededAt (required: Map<Identifier, Set<Identifier>>) (used: DeferredContextUse) =
            Set.difference required.[used.Callee] used.Available |> Set.filter (WrittenContexts.isScoped state >> not)
        let isScriptValue name = state.ScriptValues.ContainsKey name
        let mutable required =
            Seq.append state.UnannotatedDefs.Keys state.ScriptValues.Keys
            |> Seq.map (fun name -> name, state.InferredContexts |> Map.tryFind name |> Option.defaultValue Set.empty)
            |> Map.ofSeq
        let mutable changed = true
        while changed do
            changed <- false
            for used in uses do
                match used.Caller with
                | DefScope(caller, true)
                | ScriptValueScope caller when required.ContainsKey caller && (isScriptValue caller || not (isScriptValue used.Callee)) ->
                    let needed = neededAt required used
                    if not (Set.isSubset needed required.[caller]) then
                        required <- required |> Map.add caller (Set.union required.[caller] needed)
                        changed <- true
                | _ -> ()
        // The script's values that need a context run in its entry point, in the order written: one is not computed in
        // a function, or before its def.
        // A scoped context, Memory, is supplied by the block the value is built in, not by where it runs.
        let needed name = required.[name] |> Set.filter (WrittenContexts.isScoped state >> not)
        let entryValues = state.ScriptValues |> Map.filter (fun name _ -> not (needed name).IsEmpty)
        // A module without statements has no entry point for them to run in.
        if state.ScriptEntry.IsNone then
            for KeyValue(name, symbol) in entryValues do
                errors <- errors @ [ valueWithoutEntry name (Set.minElement (needed name)) state.Symbols.[symbol].Declaration ]
        for used in uses do
            match (if state.ScriptEntry.IsSome then entryValues |> Map.tryFind used.Callee else None), used.Caller with
            | Some _, (DefScope _ | ValueScope _) -> errors <- errors @ [ scriptRunValue used.Callee true used.Span ]
            | Some symbol, _ when used.Span.Start < state.Symbols.[symbol].Declaration.Start ->
                errors <- errors @ [ scriptRunValue used.Callee false used.Span ]
            | _ -> ()
        state <- { state with ScriptEntryValues = entryValues.Keys |> Set.ofSeq } |> withEntryContexts required uses
        let required = required |> Map.filter (fun name _ -> not (isScriptValue name))
        let uses = uses |> List.filter (fun used -> not (isScriptValue used.Callee))
        for used in uses do
            match used.Caller with
            | DefScope(caller, false) ->
                for ctxName in neededAt required used do
                    errors <- errors @ [ undeclaredContext state caller ctxName $"'{used.Callee}' requires" used.Span |> ContextFixes.withAddToType state caller ctxName ]
            | ValueScope caller ->
                for ctxName in neededAt required used do
                    errors <- errors @ [ contextInValue caller ctxName $"'{used.Callee}' requires" used.Span ]
            | _ -> ()
        state <- recordDeferredProviders required uses state |> recordInstanceProviders
        // A lambda that calls a def in a cycle with its own requires the plain ambient contexts that def does: its
        // variable for them takes them, or, where it was given since for a function type, fits them in there, as a
        // function that requires them does (see `FunctionContexts.fit`).
        for callee, var, span in List.rev state.PendingCallContexts do
            let needed =
                required |> Map.tryFind callee |> Option.defaultValue Set.empty
                |> Set.filter (WrittenContexts.isScoped state >> not)
                |> Set.toList
                |> List.map (fun ctxName -> TyApply(ctxName, []))
            match resolveSoFar state var with
            | TyVar _ -> state <- addConstraintAt span var (TyUnion needed) state
            | settled ->
                let expected = match settled with TyUnion items -> items | other -> [ other ]
                let fresh () =
                    let made, next = freshVar state
                    state <- next
                    made
                match FunctionContexts.fit fresh span needed expected with
                | Ok constraints -> state <- { state with Constraints = List.rev constraints @ state.Constraints }
                | Error extra ->
                    let named = extra |> List.choose (function TyApply(name, _) -> Some $"'{name}'" | _ -> None) |> String.concat ", "
                    let message =
                        $"'{callee}' requires the contexts {named}, which the function this call is in is given where a function that does not require them is expected: write them on the type expected, or a context variable for any, as in `[c] a -> b`"
                    errors <- errors @ [ Diagnostics.at DiagnosticCodes.ContextsNotAllowed span message ]
        // What an unannotated function needs is part of its type, as a context written in its type is. They come after
        // the parameterised contexts it requires, which its calls have already found instances of in that order.
        state <-
            { state with
                ContextRequirements =
                    required
                    |> Map.filter (fun _ contexts -> not contexts.IsEmpty)
                    |> Map.fold (fun acc name contexts ->
                        let parameterized = acc |> Map.tryFind name |> Option.defaultValue []
                        acc |> Map.add name (parameterized @ (contexts |> Set.toList |> List.map (TypeName >> TypeExpr.synthetic)))) state.ContextRequirements }
        items <- items |> List.map (fun (index, item) -> index, withInferredAmbientContexts required item)

    // Each number literal is checked against its type, now that every type is known (see `NumericLiterals`), and each
    // `/` on a type that settles gives its fraction (see `Fractions`).
    state <- Fractions.settle (Fractions.settleLiterals (fun _ -> true) state)
    let checkedLiterals, literalErrors = NumericLiterals.check state
    state <- checkedLiterals
    errors <- errors @ literalErrors

    // Each call that leaves out a type argument, now that the types say which one to pass (see `TypeArguments`).
    state <- TypeArguments.report state

    // The defs that failed and the expressions that failed inside the others, in the order they are in the source. An
    // expression inferred more than once (a lambda checked again against its expected type) is reported once.
    let failedExprErrors = state.FailedExprs @ state.UnhandledMatches |> List.rev |> List.collect snd
    let allErrors =
        errors @ failedExprErrors
        |> List.distinct
        |> List.sortBy (fun diag -> diag.Span |> Option.map (fun span -> span.Start) |> Option.defaultValue System.Int32.MaxValue)
    types, items |> List.rev |> List.sortBy fst |> List.map snd, state, allErrors
