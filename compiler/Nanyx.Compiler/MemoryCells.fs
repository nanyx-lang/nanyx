/// Local mutable cells (see the memory page of the docs): `mut x = e` declares one, `x := e` replaces what it holds, and its
/// name anywhere else reads it. A cell can only be made in a `memory { ... }` block, the prelude's `memory` with a
/// block, which is where `$Memory` is. A lambda that uses a cell of the block around it needs `$Memory` of the block's
/// scope too, so it cannot outlive the block (see `ScopedCalls`).
module internal NyxCompiler.MemoryCells

open Parser.Program
open NyxCompiler.InferState

/// The context mutation needs, as the prelude declares it.
let context = FunctionContexts.cellContext

/// The function that provides `$Memory` for the block it is given, as the prelude declares it.
let memoryFunction = "memory"

/// The name the environment binds while a `memory` block is checked, to the block's scope (see
/// `FunctionContexts.scoped`): what tells a `mut` declaration that `$Memory` is there, and which scope its cell belongs
/// to. No code can write it.
let private scopeKey = "@memory"

/// Whether a call to `name` is a call to the prelude's `memory`, rather than to a definition of the same name: one
/// declared in the file that declares `$Memory`.
let isMemoryCall (env: TypeEnv) (state: InferState) (name: Identifier) =
    let fileOf (symbol: SymbolId option) = symbol |> Option.bind state.Symbols.TryFind |> Option.map _.Declaration.File
    name = memoryFunction
    && match fileOf (TypeEnv.tryFind name env |> Option.bind _.Symbol), fileOf (state.TypeDefs.TryFind context |> Option.bind _.Symbol) with
       | Some file, Some contextFile -> file = contextFile
       | _ -> false

/// The environment the arguments of a call to `name` are checked in, where the call supplies `$Memory` of `scope`: one
/// where cells can be made, of that scope, for the prelude's `memory`.
let aroundCall (env: TypeEnv) (state: InferState) (name: Identifier) (scope: Ty) : TypeEnv =
    if isMemoryCall env state name then TypeEnv.extend scopeKey (TypeEnv.mono scope) env else env

/// The environment after a `use` of a value of the type `ty`, `env` being the one the `use` makes: an instance of
/// `$Memory` it loads is the innermost one, so a `mut` after it makes a cell of that instance's scope, which may be an
/// outer block's. The `use` starts no scope of its own.
let afterUse (state: InferState) (env: TypeEnv) (ty: Ty) : TypeEnv =
    match resolveSoFar state ty with
    | TyNominal(nominal, FunctionContexts.ScopeType scope, _) when
        state.TypeDefs.TryFind context |> Option.exists (fun info -> TypeDefInfo.nominalId info = nominal)
        ->
        TypeEnv.extend scopeKey (TypeEnv.mono scope) env
    | _ -> env

/// The scope of the cell `name`, as `env` binds it, if it is one.
let private scopeOf (env: TypeEnv) (state: InferState) (name: Identifier) : Ty option =
    TypeEnv.tryFind name env |> Option.bind _.Symbol |> Option.bind state.Cells.TryFind

/// Whether `name`, as `env` binds it, is a cell.
let isCell (env: TypeEnv) (state: InferState) (name: Identifier) = (scopeOf env state name).IsSome

/// `state` once `name`, as `env` binds it, is read or changed: a cell's use needs `$Memory` of the cell's block, which
/// the lambda around it requires (see `LambdaContexts`).
let recordUse (env: TypeEnv) (state: InferState) (name: Identifier) : InferState =
    match scopeOf env state name with
    | Some scope -> LambdaContexts.record [ FunctionContexts.scoped context scope ] state
    | None -> state

/// Records that the local def `symbol` is a cell, of the scope of the `memory` block `env` is in. One made outside a
/// block, which is an error, has a scope of its own.
let declareCell (env: TypeEnv) (symbol: SymbolId option) (state: InferState) =
    match symbol, TypeEnv.tryFind scopeKey env with
    | Some symbol, Some scope -> { state with Cells = state.Cells |> Map.add symbol scope.Type }
    | Some symbol, None ->
        let scope, next = freshVar state
        { next with Cells = next.Cells |> Map.add symbol scope }
    | None, _ -> state

/// The value of a cell's declaration, `mut x = value`, at `expr`: a cell holding `value`, of its type. It needs `$Memory`.
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
        Error [ Diagnostics.at DiagnosticCodes.NotACell target.Span $"'{name}' is not a cell, so `:=` cannot change it: only a name declared with `mut` is one" ]
    | _ -> Error [ Diagnostics.at DiagnosticCodes.NotACell target.Span "Only a cell can be assigned with `:=`: a name declared with `mut name = value`" ]

/// The type the parameter `symbol` of a lambda binds its name to, `ty` being the parameter's type and `typeOpt` its
/// annotation, and `state` with it a cell if it is written `mut name` (see `CellPassingSyntax`): its type is then `mut T`,
/// of the scope of the cell given for it, and its name reads a `T`.
let bindParameter (symbol: SymbolId option) (typeOpt: TypeExpr option) (ty: Ty) (state: InferState) : Ty * InferState =
    match symbol, typeOpt with
    | Some symbol, CellPassingSyntax.CellParameter _ ->
        let held, next = freshVar state
        let scope, next = freshVar next
        let constrained = addConstraint ty (FunctionContexts.cellType held scope) next
        held, { constrained with Cells = constrained.Cells |> Map.add symbol scope }
    | _ -> ty, state

/// `mut cell` as an argument, at `expr` (see `CellPassingSyntax`): the cell `cell` names, of the type `mut T`. Passing it
/// uses it, as reading it does. Only a cell can be passed so.
let inferArgument (infer: TypeEnv -> InferState -> Expression -> Result<TypedExpr * InferState, Diagnostic list>) (env: TypeEnv) (state: InferState) (expr: Expression) (cell: Expression) =
    match cell.Kind with
    | IdentifierExpr name when isCell env state name ->
        infer env state cell
        |> Result.map (fun (typedCell, next) ->
            let scope = (scopeOf env state name).Value
            mkTypedExpr expr (FunctionContexts.cellType typedCell.Type scope) None None None, next)
    | IdentifierExpr name ->
        Error [ Diagnostics.at DiagnosticCodes.NotACellArgument cell.Span
                    $"'{name}' is not a cell, so `mut {name}` cannot pass it: only a name declared with `mut`, or a `mut` parameter, is one" ]
    | _ -> Error [ Diagnostics.at DiagnosticCodes.NotACellArgument cell.Span "Only a cell can be passed with `mut`: a name declared with `mut name = value`" ]

/// The errors for each `mut T` in `typeOpt`, a def's annotation, that is not a function's parameter: a cell can be passed
/// to a function, but nothing else can hold one yet.
let ensureCellTypesPlaced (typeOpt: TypeExpr option) : Result<unit, Diagnostic list> =
    let isCellType (typeExpr: TypeExpr) =
        match typeExpr.Kind with
        | TypeApply(name, [ _ ]) -> name = CellPassingSyntax.typeName
        | _ -> false
    let rec misplaced (typeExpr: TypeExpr) : TypeExpr list =
        let inside (items: TypeExpr list) = items |> List.collect (fun item -> if isCellType item then [ item ] else misplaced item)
        match typeExpr.Kind with
        // A parameter, or one of several: `mut int -> ()`, `mut int, mut int -> ()`.
        | TypeFunc(input, output) ->
            let parameters =
                match input.Kind with
                | TypeTuple items -> items
                | _ -> [ input ]
            (parameters |> List.collect (fun parameter -> if isCellType parameter then misplaced (List.head (TypeExpr.children parameter)) else misplaced parameter))
            @ inside [ output ]
        | TypeWithContext(contexts, inner) -> inside contexts @ misplaced inner
        | _ -> inside (TypeExpr.children typeExpr)
    let found = typeOpt |> Option.map (fun typeExpr -> if isCellType typeExpr then [ typeExpr ] else misplaced typeExpr) |> Option.defaultValue []
    match found with
    | [] -> Ok ()
    | _ ->
        found
        |> List.map (fun typeExpr ->
            Diagnostics.at DiagnosticCodes.MisplacedCellType typeExpr.Span
                "A cell's type, `mut T`, can only be a function's parameter for now: a cell can be passed to a function, but nothing can hold one")
        |> Error

/// The code of a type mismatch found at `span`: an assignment's own when it is one of the module's `:=`s.
let mismatchCode (state: InferState) (span: SourceSpan) =
    if state.CellAssignments.Contains span then DiagnosticCodes.CellAssignmentMismatch else DiagnosticCodes.TypeMismatch
