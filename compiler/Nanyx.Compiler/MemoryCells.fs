/// Local mutable cells (see the memory page of the docs): `mut e` makes one, `x := e` replaces what it holds, and its
/// name anywhere else reads it. A cell can only be made in a `memory { ... }` block, the prelude's `memory` with a
/// block, which is where `$Memory` is. A lambda that uses a cell of the block around it needs `$Memory` too, so it
/// must not outlive the block.
module internal NyxCompiler.MemoryCells

open Parser.Program
open NyxCompiler.InferState

/// The context mutation needs, as the prelude declares it.
let context = "$Memory"

/// The function that provides `$Memory` for the block it is given, as the prelude declares it.
let memoryFunction = "memory"

/// The name the environment binds while a `memory` block is checked: what tells `mut` that `$Memory` is there. No code
/// can write it.
let private scopeKey = "@memory"

/// Whether a call to `name` is a call to the prelude's `memory`, rather than to a definition of the same name: one
/// declared in the file that declares `$Memory`.
let isMemoryCall (env: TypeEnv) (state: InferState) (name: Identifier) =
    let fileOf (symbol: SymbolId option) = symbol |> Option.bind state.Symbols.TryFind |> Option.map _.Declaration.File
    name = memoryFunction
    && match fileOf (TypeEnv.tryFind name env |> Option.bind _.Symbol), fileOf (state.TypeDefs.TryFind context |> Option.bind _.Symbol) with
       | Some file, Some contextFile -> file = contextFile
       | _ -> false

/// The environment the arguments of a call to `name` are checked in: one where `$Memory` is, for the prelude's `memory`.
let aroundCall (env: TypeEnv) (state: InferState) (name: Identifier) : TypeEnv =
    if isMemoryCall env state name then TypeEnv.extend scopeKey (TypeEnv.mono (TyPrimitive "unit")) env else env

/// Whether `name`, as `env` binds it, is a cell.
let isCell (env: TypeEnv) (state: InferState) (name: Identifier) =
    match TypeEnv.tryFind name env |> Option.bind _.Symbol with
    | Some symbol -> state.Cells.Contains symbol
    | None -> false

/// `state` once `name`, as `env` binds it, is read or changed: a cell's use needs `$Memory`, which the lambda around it
/// requires (see `LambdaContexts`).
let recordUse (env: TypeEnv) (state: InferState) (name: Identifier) : InferState =
    if isCell env state name then LambdaContexts.record [ TyApply(context, []) ] state else state

/// Records that the local def `symbol` is a cell.
let declareCell (symbol: SymbolId option) (state: InferState) =
    match symbol with
    | Some symbol -> { state with Cells = state.Cells.Add symbol }
    | None -> state

/// `mut value`, at `expr`: a cell holding `value`, of its type. It needs `$Memory`.
let inferMut (infer: TypeEnv -> InferState -> Expression -> Result<TypedExpr * InferState, Diagnostic list>) (env: TypeEnv) (state: InferState) (expr: Expression) (value: Expression) =
    if env.ContainsKey scopeKey then
        infer env state value |> Result.map (fun (typedValue, next) -> mkTypedExpr expr typedValue.Type None None None, next)
    else
        let message = $"`mut` needs the context '{context}', which only a `memory {{ ... }}` block provides: make the cell inside one"
        Error [ Diagnostics.at DiagnosticCodes.MutOutsideMemory expr.Span message ]

/// `target := value`, at `expr`: `value` replaces what the cell `target` holds, and must have its type. It is unit.
let inferAssign (infer: TypeEnv -> InferState -> Expression -> Result<TypedExpr * InferState, Diagnostic list>) (env: TypeEnv) (state: InferState) (expr: Expression) (target: Expression) (value: Expression) =
    match target.Kind with
    | IdentifierExpr name when isCell env state name ->
        infer env state target
        |> Result.bind (fun (typedTarget, afterTarget) ->
            infer env afterTarget value
            |> Result.map (fun (typedValue, next) ->
                let assigned = addAssignableConstraintAt expr.Span typedValue.Type typedTarget.Type next
                mkTypedExpr expr (TyPrimitive "unit") None None None,
                { assigned with CellAssignments = assigned.CellAssignments.Add expr.Span }))
    | IdentifierExpr name when env.ContainsKey name ->
        Error [ Diagnostics.at DiagnosticCodes.NotACell target.Span $"'{name}' is not a cell, so `:=` cannot change it: only a def whose value is `mut ...` makes one" ]
    | _ -> Error [ Diagnostics.at DiagnosticCodes.NotACell target.Span "Only a cell can be assigned with `:=`: a name a def gave the value `mut ...`" ]

/// The code of a type mismatch found at `span`: an assignment's own when it is one of the module's `:=`s.
let mismatchCode (state: InferState) (span: SourceSpan) =
    if state.CellAssignments.Contains span then DiagnosticCodes.CellAssignmentMismatch else DiagnosticCodes.TypeMismatch

/// The error, if any, for the `memory` block at `call`, whose block is `block`: its result must not be, or hold, a
/// lambda that uses one of its cells, which would let the cell outlive the block. A lambda uses a cell when it names
/// one from outside itself, or a local function that uses one (see `EscapingLambdas.tryFind`).
let checkEscape (state: InferState) (call: Expression) (block: Expression) : Diagnostic list =
    match block.Kind with
    | Lambda([], body, _) ->
        let result = EscapingLambdas.resultOf body
        let declaredWithin (span: SourceSpan) (symbol: SymbolId) =
            state.Symbols |> Map.tryFind symbol |> Option.exists (fun declared -> SourceSpan.contains span declared.Declaration)
        let usesCell (lambda: Expression) (node: Expression) =
            match state.References |> Map.tryFind node.Id with
            | Some symbol -> state.Cells.Contains symbol && not (declaredWithin lambda.Span symbol)
            | None -> false
        match EscapingLambdas.tryFind state usesCell (SyntaxTree.blocksWithin body) result with
        | Some _ ->
            let message =
                $"The result of this `{memoryFunction}` block holds a lambda that uses its cells, which needs '{context}' and so cannot leave the block: return what the cells hold instead"
            [ Diagnostics.at DiagnosticCodes.CellEscapesMemory result.Span message |> Diagnostics.withRelated call.Span "the block" ]
        | None -> []
    | _ -> []

/// `state` once the call `call` to `name` is checked, or the error if it is a `memory` block whose cells escape it
/// (see `checkEscape`).
let checkCall (env: TypeEnv) (call: Expression) (name: Identifier) (state: InferState) : Result<InferState, Diagnostic list> =
    match call.Kind with
    | FunctionCall(_, _, [ block ]) when isMemoryCall env state name ->
        match checkEscape state call block with
        | [] -> Ok state
        | errors -> Error errors
    | _ -> Ok state
