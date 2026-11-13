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
      /// The input the def's lambda is expected to take: the one its type declares, or, for a def that writes no type,
      /// the one the type it is attached to gives.
      ExpectedInput: Ty option
      /// The type the def's lambda is expected to return.
      ExpectedReturn: Ty option
      /// The state once the type was checked and the values its `typeof`s name inferred, before the contexts it
      /// declares were bound: what a def that then fails to check carries on from.
      Checked: InferState }

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
    let typeOpt, inlineContextsRead = readInlineContexts state typeOpt
    let span = declarationSpan state DefValue expr
    let contextsExist =
        match fst (extractContextFromTypeExpr typeOpt) with
        | Some contexts -> ensureContextsExist state name span contexts
        | None -> Ok ()
    let contextsExist =
        contextsExist
        |> Result.bind (fun () -> ContextVariables.ensureBound name typeOpt)
        |> Result.bind (fun () -> ensureContextTypesDetermined name typeOpt)
        |> Result.bind (fun () -> MemoryCells.checkCellTypes state name typeOpt)
    let typeOfsInferred =
        match typeOpt with
        | Some typeExpr -> inferTypeOfValues typeOfEnv state typeExpr
        | None -> Ok state
    ensureAttachedTypeExists state moduleName name span
    |> Result.bind (fun () -> AttachedOperators.check state name span)
    |> Result.bind (fun () -> ensureTypesExist state (Option.toList typeOpt))
    |> Result.bind (fun () -> inlineContextsRead)
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
        let expectedInputOpt =
            match expectedInputOpt with
            | Some _ -> expectedInputOpt
            | None when declaredOpt.IsNone -> tryInferAttachedLambdaInput state name expr
            | None -> None
        { TypeOpt = typeOpt
          Contexts = contextOpt
          DeclaredTypeExpr = declaredTypeExprOpt
          Declared = declaredOpt
          Env = envWithCtx
          ExpectedInput = expectedInputOpt
          ExpectedReturn = expectedReturnOpt
          Checked = checkedState },
        state)

/// The errors in the type definition `name`, checked once it is registered in `state`, so that it can name itself: a
/// `typeof` or `_` in it, or else a scoped context that is not nominal, a type it names that does not exist, and a
/// reference to itself that nothing guards.
let internal typeDefErrors (state: InferState) (name: Identifier) (modifiers: TypeDefModifier list) (parameters: (Identifier * TypeExpr option) list) (body: TypeExpr) =
    match typeOfInTypeDef name body parameters @ TypeHoles.inTypeDef name body parameters with
    | [] -> unnominalScopedContext name modifiers @ unknownTypesInTypeDef state body parameters @ unguardedSelfReferences name modifiers body
    | errors -> errors
