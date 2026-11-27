/// What a def's written type says, read and checked before its value is inferred, and the checks of a type definition.
/// A top-level def and a local one go through the same steps here, so that a fix to one is a fix to the other.
module NyxCompiler.DefAnnotations

open Parser.Program
open NyxCompiler
open NyxCompiler.InferState
open NyxCompiler.TypeResolution
open NyxCompiler.Contexts
open NyxCompiler.ContextInstances

/// A def's written type, read: what its value is inferred against.
type internal ReadAnnotation =
    { /// The type as written, with the contexts written inline in it read.
      TypeOpt: TypeExpr option
      /// The contexts the type declares.
      Contexts: TypeExpr list option
      /// The type without its contexts.
      DeclaredTypeExpr: TypeExpr option
      /// The type the def is declared at.
      Declared: Ty option
      /// The environment the value is inferred in: with the members of the contexts the type declares.
      Env: TypeEnv
      /// The input the def's lambda is expected to take: the one its type declares.
      ExpectedInput: Ty option
      /// For a def that writes no type, attached to a type, whose lambda takes one parameter: the type it is attached
      /// to, which the parameter is once the body is inferred, if nothing in the body says otherwise.
      AttachedInput: Ty option
      /// The type the def's lambda is expected to return.
      ExpectedReturn: Ty option
      /// The state once the type was checked and the values its `typeof`s name inferred, before the contexts it
      /// declares were bound: what a def that then fails to check carries on from.
      Checked: InferState }

/// Where the return type `annotation` gives a def's lambda is written: the result of the function type it writes, or
/// the whole type where that is not a function type as written, an alias of one say.
let internal writtenReturn (annotation: ReadAnnotation) : SourceSpan option =
    match annotation.DeclaredTypeExpr with
    | Some { Kind = TypeFunc(_, result) } -> Some result.Span
    | Some written -> Some written.Span
    | None -> None

/// Reads and checks `typeOpt`, the type the def `name` writes for its value `expr`, in `env`. The def is attached to a
/// type of `moduleName`'s, if its name says so. `extraCheck` is checked after the contexts, before the values each
/// `typeof` names are inferred in `typeOfEnv` by `inferTypeOfValues`. The first check that fails is the error.
let internal read
    (inferTypeOfValues: TypeEnv -> InferState -> TypeExpr -> Result<InferState, Diagnostic list>)
    (moduleName: string option)
    (extraCheck: Result<unit, Diagnostic list>)
    (typeOfEnv: TypeEnv)
    (env: TypeEnv)
    (state: InferState)
    (name: Identifier)
    (typeOpt: TypeExpr option)
    (expr: Expression)
    : Result<ReadAnnotation * InferState, Diagnostic list> =
    let memberRequirements = checkMemberRequirements state typeOpt
    let span = declarationSpan state DefValue expr
    let expr = LocalRecursion.written expr
    let contextsExist =
        match fst (extractContextFromTypeExpr typeOpt) with
        | Some contexts -> ensureContextsExist state name span contexts
        | None -> Ok ()
    // Only a lambda runs in the contexts an annotation writes on their own (`def main: [Console] = { ... }`): a value
    // that is a function written point-free requires them, so they go on its function type.
    let onlyContextsOnValue =
        match typeOpt, expr.Kind with
        | Some ({ Kind = TypeContext _ } as annotation), kind when (match kind with Lambda _ -> false | _ -> true) ->
            Error [ Diagnostics.at DiagnosticCodes.ContextsWithoutType annotation.Span
                        $"'{name}' is not a lambda, so it does not run in the contexts its annotation writes: write them on its function type, as in '[Console] string -> ()'"
                    |> Diagnostics.about name ]
        | _ -> Ok ()
    // An operator takes its two operands as two arguments, so the lambda that defines one takes two parameters.
    let operatorArity =
        let last = name.Substring(name.LastIndexOf '.' + 1)
        match expr.Kind with
        | Lambda(parameters, _, _) when Names.isOperator last && parameters.Length <> 2 ->
            let takes = match parameters.Length with 0 -> "none" | 1 -> "one" | n -> string n
            Error [ Diagnostics.at DiagnosticCodes.OperatorArity span
                        $"An operator takes exactly two arguments, its left and right operands, but the lambda that defines it takes {takes}"
                    |> Diagnostics.about (Names.written last) ]
        | _ -> Ok ()
    let contextsExist =
        onlyContextsOnValue
        |> Result.bind (fun () -> operatorArity)
        |> Result.bind (fun () -> contextsExist)
        |> Result.bind (fun () -> ContextVariables.ensureBound name typeOpt)
        |> Result.bind (fun () -> ensureContextTypesDetermined state name typeOpt)
        |> Result.bind (fun () -> MemoryCells.checkCellTypes state name typeOpt)
    let typeOfsInferred =
        match typeOpt with
        | Some typeExpr -> inferTypeOfValues typeOfEnv state typeExpr
        | None -> Ok state
    ensureAttachedTypeExists state moduleName name span
    |> Result.bind (fun () -> AttachedOperators.check state name span)
    |> Result.bind (fun () ->
        match typeOpt with
        | Some context when name = UseInstanceSyntax.name -> ensureInstanceContextExists state context
        | _ -> ensureTypesExist state (Option.toList typeOpt))
    |> Result.bind (fun () -> memberRequirements)
    |> Result.bind (fun () -> contextsExist)
    |> Result.bind (fun () -> extraCheck)
    |> Result.bind (fun () -> typeOfsInferred)
    |> Result.map (fun checkedState ->
        let contextOpt, declaredTypeExprOpt = extractDefContexts typeOpt expr
        let envWithCtx, state =
            match contextOpt with
            | Some contexts -> extendEnvWithContexts env checkedState contexts ||> fun env state -> bindInstances env state contexts
            | None -> env, checkedState
        let expectedInputOpt, expectedReturnOpt, declaredOpt, state =
            match declaredTypeExprOpt with
            | Some declaredExpr ->
                let ty, state = typeExprToTy state declaredExpr
                match Unifier.unalias ty with
                | TyFunc(inputTy, returnTy, _) -> TypeHoles.expectedLambdaInput declaredExpr (Some inputTy), Some returnTy, Some ty, state
                | _ -> None, None, Some ty, state
            | None -> None, None, None, state
        { TypeOpt = typeOpt
          Contexts = contextOpt
          DeclaredTypeExpr = declaredTypeExprOpt
          Declared = declaredOpt
          Env = envWithCtx
          ExpectedInput = expectedInputOpt
          AttachedInput = if declaredOpt.IsNone then tryInferAttachedLambdaInput state name expr else None
          ExpectedReturn = expectedReturnOpt
          Checked = checkedState },
        state)

/// `typeOpt`, the annotation of the def `name`, with each `_` its contexts are applied to written as the type the def's
/// value made it, as `a` in `[Foo(_)] a, a -> a` whose body calls `Foo`'s members with `a`s: a call reads a context's
/// arguments from the annotation, so each `_` must stand for a type the annotation can write, with the variables it
/// names. One the value left as a variable of its own, or made a type an annotation cannot write, is an error at the `_`.
///
/// A `_` in the rest of the signature that the value made a type of the variables the signature names is written as that
/// type too, as the result `_` of `[Num(a)] a, a -> _` over `{ x, y -> x / y }` is `fraction`, `Num(a).fraction`: a call
/// reads the annotation again, and so ties those variables, which an instance of a context may fix, to what the call
/// gives (#2879). Any other `_` there stays as it is.
let internal settleHoles (state: InferState) (name: Identifier) (typeOpt: TypeExpr option) : Result<TypeExpr option, Diagnostic list> =
    match typeOpt with
    | Some ({ Kind = TypeWithContext(contexts, inner) } as annotation) when contexts |> List.exists TypeHoles.hasHoles || TypeHoles.hasHoles inner ->
        let names =
            state.TypeVars
            |> Map.toList
            |> List.choose (fun (key, var) ->
                let varName = match key with WrittenVar name -> name | ScopeOf context -> context
                match resolveSoFar state (TyVar var) with
                | TyVar resolved -> Some (resolved.Id, varName)
                | _ -> None)
            |> Map.ofList
        let resolvedHole (hole: TypeExpr) = state.ExprTypes |> Map.tryFind hole.Id |> Option.map (resolveSoFar state)
        let holes =
            contexts
            |> List.collect TypeExpr.holes
            |> List.map (fun hole ->
                let written = resolvedHole hole |> Option.bind (ContextInference.typeExprOf names)
                hole, written |> Option.map (fun written -> { written with Span = hole.Span }))
        let errors =
            holes
            |> List.choose (function
                | hole, None ->
                    Some (Diagnostics.at DiagnosticCodes.UndeterminedContextType hole.Span
                              $"The body of '{name}' does not make this '_' a type its parameters or result name, so no call can supply the context: write the type in place of '_'")
                | _, Some _ -> None)
        let named =
            TypeExpr.holes inner
            |> List.choose (fun hole ->
                match resolvedHole hole with
                | Some ty when not (TypeEnv.freeTyVars ty).IsEmpty ->
                    ContextInference.typeExprOf names ty |> Option.map (fun written -> hole.Id, { written with Span = hole.Span })
                | _ -> None)
        let settled = (holes |> List.choose (fun (hole, written) -> written |> Option.map (fun e -> hole.Id, e))) @ named |> Map.ofList
        let rec settle (typeExpr: TypeExpr) =
            match settled |> Map.tryFind typeExpr.Id with
            | Some written -> written
            | None -> TypeExpr.mapChildren settle typeExpr
        if errors.IsEmpty then Ok (Some { annotation with Kind = TypeWithContext(List.map settle contexts, settle inner) })
        else Error errors
    | _ -> Ok typeOpt

/// The errors in the type definition `name`, checked once it is registered in `state`, so that it can name itself: a
/// `typeof` or `_` in it, or else a scoped context that is not nominal, a scope it cannot belong to, a scope it holds a
/// value of without declaring it, a type it names
/// that does not exist, and a reference to itself that nothing guards.
let internal typeDefErrors (state: InferState) (name: Identifier) (modifiers: TypeDefModifier list) (parameters: (Identifier * TypeExpr option) list) (body: TypeExpr) =
    match typeOfInTypeDef name body parameters @ TypeHoles.inTypeDef name body parameters with
    | [] ->
        unnominalScopedContext name modifiers @ typeScopeErrors state name modifiers @ undeclaredHeldScopes state name modifiers body
        @ unknownTypesInTypeDef state body parameters
        @ unguardedSelfReferences name modifiers body
    | errors -> errors
