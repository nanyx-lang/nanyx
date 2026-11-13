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
/// more general than the body, which would otherwise quietly narrow it.
let private checkAnnotationIsRigid (state: InferState) (name: Identifier) (span: SourceSpan) (typeExpr: TypeExpr) : Result<unit, Diagnostic list> =
    let vars =
        annotationVarNames typeExpr
        |> List.choose (fun varName -> state.TypeVars |> Map.tryFind varName |> Option.map (fun var -> varName, var))
    if vars.IsEmpty then
        Ok ()
    else
        match Unifier.unify state.Constraints with
        // A conflict is reported when the module's constraints are solved.
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
                        |> Option.map (fun (otherName, _) -> $"`{varName}` would have to be `{otherName}`")
                    | concrete -> Some $"`{varName}` would have to be `{Unifier.tyToString (Unifier.apply displayNames concrete)}`")
                |> List.choose id
            if problems.IsEmpty then
                Ok ()
            else
                let details = String.concat "; " problems
                Error [ Diagnostics.at DiagnosticCodes.AnnotationTooGeneral span $"Annotation of '{name}' is more general than the inferred type: {details}" ]

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
    // Each typed item with the index of the item it was checked from, which may be out of order (see `DefinitionOrder`).
    let mutable items: (int * TypedTopLevelItem) list = []
    let mutable itemIndex = 0
    let emit item = items <- items @ [ itemIndex, item ]
    // A name defined again is reported, and only its first definition is inferred.
    let module', duplicateErrors = DuplicateDefinitions.check initialState.Declared module'
    let mutable errors: Diagnostic list = duplicateErrors @ InitialisationCycles.check initialState.Declared module'

    // An imported context whose every member is also an imported definition of that name is an ambient instance:
    // the one the runtime supplies, as `nanyx/console`'s definitions are its `Console`. The top level of the module
    // runs in it; a def runs in it only if it declares the context, or does not write its type and so infers it.
    state <-
        { state with
            AmbientMembers =
                state.TypeDefs
                |> Map.toList
                |> List.collect (fun (ctxName, info) ->
                    // An aliased import's context (`c.Console`) has its members under the alias too (`c.println`).
                    let qualify memberName =
                        if ctxName.Contains '.' then $"{ctxName.Substring(0, ctxName.LastIndexOf '.')}.{memberName}" else memberName
                    let bindings =
                        info.Members
                        |> Map.toList
                        |> List.map (fun (memberName, memberSymbol) ->
                            let memberName = qualify memberName
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
    // with once its body has been checked. Type definitions are registered in the same pass, so an
    // annotation can name a type declared further down.
    // A block the file imports (`import money`) brings its exports in under other names: each is bound wherever the
    // definition it stands for is, so it has the same type and refers to the same symbol.
    let blockImported = blockImports state.ModuleBlocks module'
    let aliases = blockImported |> List.collect (blockAliases state.ModuleBlocks module') |> Map.ofList
    state <- { state with BlockImports = blockImported; BlockAliases = aliases }
    let aliasesOf =
        aliases |> Map.toList |> List.groupBy snd |> List.map (fun (qualified, pairs) -> qualified, List.map fst pairs) |> Map.ofList
    let declareTopLevel (name: string) symbol scheme (env: TypeEnv) =
        aliasesOf
        |> Map.tryFind name
        |> Option.defaultValue []
        |> List.fold (fun acc alias -> TypeEnv.declare alias symbol scheme acc) (TypeEnv.declare name symbol scheme env)
    let withoutTopLevel (name: string) (env: TypeEnv) =
        aliasesOf |> Map.tryFind name |> Option.defaultValue [] |> List.fold (fun acc alias -> acc |> Map.remove alias) (env |> Map.remove name)

    let mutable preboundFreshVars: Map<string, Ty> = Map.empty
    let prebind (item: TopLevelItem) =
        match item with
        | Kind (Def (TypeDef(name, modifiers, parameters, body))) ->
            state <- registerTypeDef state name modifiers parameters body
        | Kind (Def (ValueDef(isExport, name, typeOpt, value))) ->
            beginDef ()
            let symbol, declared = declareSymbol DefValue value.Id name TopLevelValue isExport None state
            // The module's own def wins over an imported one of the same name, and requires only its own contexts.
            state <- { declared with ContextRequirements = declared.ContextRequirements |> Map.remove name }
            match typeOpt, value.Kind, symbol with
            | None, Lambda _, Some symbol -> state <- { state with UnannotatedDefs = state.UnannotatedDefs |> Map.add name symbol }
            | _ -> ()
            let _, declaredTypeExprOpt = extractContextFromTypeExpr typeOpt
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
    for item in module' do
        prebind item

    // The exported types of an imported block can be written under the import's names too (`$Money`, `m.$Money`).
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
        state <- { state with FailedExprs = (failedNode.Id, diagnostics) :: state.FailedExprs }
        types <- types |> Map.add name Unifier.errorType
        emit (TypedDef (TypedValueDef(isExport, name, None, mkTypedExpr expr Unifier.errorType None None None)))
        match TypeEnv.tryFind name env with
        | Some scheme -> env <- declareTopLevel name scheme.Symbol (TypeEnv.mono Unifier.errorType) env
        | None -> ()

    let check (item: TopLevelItem) =
        match item.Kind with
        | Def (ValueDef(isExport, name, typeOpt, expr)) ->
            beginDef ()
            // A def's own name is not in scope in its annotation: `def x: typeof x` says nothing.
            match DefAnnotations.read inferTypeOfValues currentModule (Ok ()) (withoutTopLevel name env) env state name typeOpt expr with
            | Error err -> abandon isExport name expr err
            | Ok (annotation, nextState2) ->
                state <- annotation.Checked
                let typeOpt, contextOpt, declaredTypeExprOpt, expectedDeclaredOpt, envWithCtx =
                    annotation.TypeOpt, annotation.Contexts, annotation.DeclaredTypeExpr, annotation.Declared, annotation.Env
                // The name is already in `env` from the pre-binding pass: at its declared type, or at the
                // fresh variable recorded in `preboundFreshVars`. Self-recursion sees the declared type as a
                // monomorphic binding while the body is checked, as before.
                // The symbol the pre-binding pass made.
                let symbol, _ = declareSymbol DefValue expr.Id name TopLevelValue isExport None state
                let envWithRec =
                    match expectedDeclaredOpt with
                    | Some declaredTy -> TypeEnv.declare name symbol (TypeEnv.mono declaredTy) envWithCtx
                    | None -> envWithCtx
                let nextStateWithReq =
                    let written = recordWrittenType name typeOpt nextState2
                    match contextOpt with
                    | Some contexts -> registerContextRequirement written name contexts declaredTypeExprOpt
                    | None -> written
                // A function runs in the contexts its type declares, or infers them. A script's value is computed at
                // the top level, in the ambient ones; a named module's is built where there are none.
                let bodyState =
                    match expr.Kind with
                    | Lambda _ -> { nextStateWithReq with ContextScope = DefScope(name, typeOpt.IsNone) } |> LambdaContexts.markBody expr
                    | _ when currentModule.IsSome || (blockAround state.ModuleBlocks expr.Span).IsSome ->
                        { nextStateWithReq with ContextScope = ValueScope name }
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
                    let finalState = NumericLiterals.settle (fun _ -> true) finalState
                    state <-
                        finalState
                        |> recordSymbolType symbol declaredTy
                        |> recordAnnotated symbol expectedDeclaredOpt.IsSome
                    let rigidResult =
                        match declaredTypeExprOpt with
                        | Some declaredExpr -> checkAnnotationIsRigid finalState name (declarationSpan finalState DefValue expr) declaredExpr
                        | None -> Ok ()
                    match rigidResult |> Result.bind (fun () -> synthesizeSettledInstances env state name) with
                    | Error err -> abandon isExport name expr err
                    | Ok synthesizedState ->
                        state <- synthesizedState
                        // Generalize against an env without this def's own pre-binding, so the fresh
                        // variable it was pre-bound at does not stop its type being generalized. It is resolved
                        // first, as a local def's is: `{ x -> x + 1 }` is `α -> α` until its constraints are
                        // applied, and generalising that would make every use `α -> α` too.
                        let scheme, generalizedState =
                            ArithmeticInference.generalize (fun state -> generalizeDef (withoutTopLevel name env) state declaredTy) (ArithmeticInference.TopLevelDef name) state
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
                            env <- declareTopLevel name symbol scheme env
                            types <- types |> Map.add name declaredTy
                            emit (TypedDef (TypedValueDef(isExport, name, annotation, typedExpr)))
                | Error err -> abandon isExport name expr err
        // A block is a module, not a program, so it has no top-level code.
        | Expr expr when (blockAround state.ModuleBlocks expr.Span).IsSome ->
            let blockName = fst (blockAround state.ModuleBlocks expr.Span).Value
            errors <- errors @ [ Diagnostics.at DiagnosticCodes.CodeInModuleBlock expr.Span $"Module '{blockName}' is a block, which can only hold definitions, types and imports, not code to run" ]
        | Expr expr ->
            match inferExpr env state expr with
            | Ok (typedExpr, next) ->
                state <- next
                emit (TypedExprItem typedExpr)
            | Error err -> errors <- errors @ err
        | Import itemsList -> emit (TypedImport itemsList)
        | ReExport item -> emit (TypedImport [ item ])
        | ModuleDecl name ->
            currentModule <- Some name
            emit (TypedModuleDecl name)
        | ModuleBlock _ -> ()
        // Attributes are resolved by the compiler front end before inference (see Attributes.applyAttributes).
        | Attribute _ -> ()
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
            | ModuleDecl name -> Some name
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
        // functions it uses need that nothing nearer supplies, until no more are found.
        let uses = List.rev state.DeferredContextUses
        let mutable required =
            state.UnannotatedDefs |> Map.map (fun name _ -> state.InferredContexts |> Map.tryFind name |> Option.defaultValue Set.empty)
        let mutable changed = true
        while changed do
            changed <- false
            for used in uses do
                match used.Caller with
                | DefScope(caller, true) when required.ContainsKey caller ->
                    let needed = Set.difference required.[used.Callee] used.Available
                    if not (Set.isSubset needed required.[caller]) then
                        required <- required |> Map.add caller (Set.union required.[caller] needed)
                        changed <- true
                | _ -> ()
        for used in uses do
            match used.Caller with
            | DefScope(caller, false) ->
                for ctxName in Set.difference required.[used.Callee] used.Available do
                    errors <- errors @ [ undeclaredContext state caller ctxName $"'{used.Callee}' requires" used.Span |> ContextFixes.withAddToType state caller ctxName ]
            | ValueScope caller ->
                for ctxName in Set.difference required.[used.Callee] used.Available do
                    errors <- errors @ [ contextInValue caller ctxName $"'{used.Callee}' requires" used.Span ]
            | _ -> ()
        state <- recordDeferredProviders required uses state |> recordInstanceProviders
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

    // Each number literal is checked against its type, now that every type is known (see `NumericLiterals`).
    let checkedLiterals, literalErrors = NumericLiterals.check state
    state <- checkedLiterals
    errors <- errors @ literalErrors

    // The defs that failed and the expressions that failed inside the others, in the order they are in the source. An
    // expression inferred more than once (a lambda checked again against its expected type) is reported once.
    let failedExprErrors = state.FailedExprs @ state.UnhandledMatches |> List.rev |> List.collect snd
    let allErrors =
        errors @ failedExprErrors
        |> List.distinct
        |> List.sortBy (fun diag -> diag.Span |> Option.map (fun span -> span.Start) |> Option.defaultValue System.Int32.MaxValue)
    types, items |> List.sortBy fst |> List.map snd, state, allErrors

/// Infers a module, carrying on past an item that fails to check: the types and items of those that do, and the
/// diagnostics of those that do not. The constraints are not solved here, so a type mismatch is not among them.
let inferModule (module': Module) : Map<string, Ty> * TypedTopLevelItem list * InferState * Diagnostic list =
    inferModuleWithEnv TypeEnv.empty emptyState module'
