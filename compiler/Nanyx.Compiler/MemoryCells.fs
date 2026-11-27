/// Local mutable cells (see the memory page of the docs): `mut x = e` declares one, `x := e` replaces what it holds, and its
/// name anywhere else reads it. A cell can only be made in a `memory { ... }` block, the prelude's `memory` with a
/// block, which is where `Memory` is. A lambda that uses a cell of the block around it needs `Memory` of the block's
/// scope too, so it cannot outlive the block (see `ScopedCalls`).
module internal NyxCompiler.MemoryCells

open Parser.Program
open NyxCompiler.InferState

/// The context mutation needs, as the prelude declares it.
let context = FunctionContexts.cellContext

/// The function that provides `Memory` for the block it is given, as the prelude declares it.
let memoryFunction = "memory"

/// Whether a call to `name` is a call to the prelude's `memory`, rather than to a definition of the same name: one
/// declared in the file that declares `Memory`.
let isMemoryCall (env: TypeEnv) (state: InferState) (name: Identifier) =
    let fileOf (symbol: SymbolId option) = symbol |> Option.bind state.Symbols.TryFind |> Option.map _.Declaration.File
    name = memoryFunction
    && match fileOf (TypeEnv.tryFind name env |> Option.bind _.Symbol), fileOf (state.TypeDefs.TryFind context |> Option.bind _.Symbol) with
       | Some file, Some contextFile -> file = contextFile
       | _ -> false

/// `env` where a `mut` makes a cell of `scope` (see `TypeEnv.BlockScope`): a `memory` block's, or after `use m`, `m`
/// being a block's `Memory` named with `context m`, which may be an outer block's. That `use` starts no scope of its
/// own. It is what tells a `mut` declaration that `Memory` is there, and which scope its cell belongs to.
let withBlockScope (scope: Ty) (env: TypeEnv) : TypeEnv =
    { env with BlockScope = Some scope; BlockDepth = None }

/// The environment the arguments of a call to `name` are checked in, where the call supplies `Memory` of `scope`: one
/// where cells can be made, of that scope, for the prelude's `memory`, whose block is the lambda inside the call.
let aroundCall (env: TypeEnv) (state: InferState) (name: Identifier) (scope: Ty) : TypeEnv =
    if isMemoryCall env state name then { withBlockScope scope env with BlockDepth = Some (state.LambdaContexts.Length + 1) }
    else env

/// `state` with each `Memory` the `memory` block `env` is in has used so far in its own lambda, but of a scope not yet
/// known, made the block's own. The block's type makes them so once its lambda is inferred (see
/// `FunctionContexts.agree`); a call inside it that starts a scope of its own needs it sooner, so that a value made
/// before the call, as `def outer = MRef.new([])` is, is not taken to be of the call's scope.
let settleBlock (env: TypeEnv) (state: InferState) : InferState =
    match env.BlockScope, env.BlockDepth, state.LambdaContexts with
    | Some scope, Some depth, used :: _ when depth = state.LambdaContexts.Length ->
        let pending =
            used
            |> List.choose (function
                | FunctionContexts.ScopedContext(name, own) when name = context -> Some own
                | _ -> None)
            |> List.filter (resolveSoFar state >> FunctionContexts.isCallScope >> not)
        pending |> List.fold (fun current own -> addConstraint own scope current) state
    | _ -> state

/// The scope of the `memory` block `env` is in, whose cell a `mut` there makes: none outside a block.
let blockScope (env: TypeEnv) : Ty option =
    env.BlockScope

/// The scope of the cell `name`, as `env` binds it, if it is one.
let private scopeOf (env: TypeEnv) (state: InferState) (name: Identifier) : Ty option =
    TypeEnv.tryFind name env |> Option.bind _.Symbol |> Option.bind state.Cells.TryFind

/// Whether `name`, as `env` binds it, is a cell.
let isCell (env: TypeEnv) (state: InferState) (name: Identifier) = (scopeOf env state name).IsSome

/// `state` once `name`, as `env` binds it, is read or changed: a cell's use needs `Memory` of the cell's block, which
/// the lambda around it requires (see `LambdaContexts`). A cell made outside a block, which is an error already, has
/// none, and its uses need nothing.
let recordUse (env: TypeEnv) (state: InferState) (name: Identifier) : InferState =
    match TypeEnv.tryFind name env |> Option.bind _.Symbol with
    | Some symbol when not (state.CellsOutsideMemory.Contains symbol) ->
        match state.Cells.TryFind symbol with
        | Some scope -> LambdaContexts.record [ FunctionContexts.scoped context scope ] state
        | None -> state
    | _ -> state

/// Records that the local def `symbol` is a cell, of the scope of the `memory` block `env` is in. One made outside a
/// block, which is an error, has a scope of its own, and its uses need no `Memory` (see `recordUse`).
let declareCell (env: TypeEnv) (symbol: SymbolId option) (state: InferState) =
    match symbol, env.BlockScope with
    | Some symbol, Some scope -> { state with Cells = state.Cells |> Map.add symbol scope }
    | Some symbol, None ->
        let scope, next = freshVar state
        { next with
            Cells = next.Cells |> Map.add symbol scope
            CellsOutsideMemory = next.CellsOutsideMemory.Add symbol }
    | None, _ -> state

/// `state` with the declaration `expr`, `mut x = value`, of the cell's type `cellTy`, which can be wider than its first
/// value's (`mut found: #some(int) | #none = #none`): the backend holds the cell as a value of the type its `mut` has.
let typeMut (expr: Expression) (cellTy: Ty) (state: InferState) =
    { state with ExprTypes = state.ExprTypes |> Map.add expr.Id cellTy }

/// The value of a cell's declaration, `mut x = value`, at `expr`: a cell holding `value`, of its type. It needs `Memory`.
let inferMut (infer: TypeEnv -> InferState -> Expression -> Result<TypedExpr * InferState, Diagnostic list>) (env: TypeEnv) (state: InferState) (expr: Expression) (value: Expression) =
    if env.BlockScope.IsSome then
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
    | IdentifierExpr name when env.Names.ContainsKey name ->
        Error [ Diagnostics.at DiagnosticCodes.NotACell target.Span $"`:=` changes only a cell, a name declared with `mut`" |> Diagnostics.about name ]
    | _ -> Error [ Diagnostics.at DiagnosticCodes.NotACell target.Span "Only a cell can be assigned with `:=`: a name declared with `mut name = value`" ]

/// The type the parameter `symbol` of a lambda binds its name to, `ty` being the parameter's type and `typeOpt` its
/// annotation, and `state` with it a cell if it is written `mut name` (see `CellPassingSyntax`): its type is then `mut T`,
/// of the scope of the cell given for it, and its name reads a `T`.
let bindParameter (symbol: SymbolId option) (typeOpt: TypeExpr option) (ty: Ty) (state: InferState) : Ty * InferState =
    match symbol, typeOpt with
    // A type that says what the cell holds already, as an expected type does, gives the cell its scope.
    | Some symbol, CellPassingSyntax.CellParameter _ ->
        match resolveSoFar state ty with
        | FunctionContexts.CellType(held, scope) -> held, { state with Cells = state.Cells |> Map.add symbol scope }
        | _ ->
            let held, next = freshVar state
            let scope, next = freshVar next
            let constrained = addConstraint ty (FunctionContexts.cellType held scope) next
            held, { constrained with Cells = constrained.Cells |> Map.add symbol scope }
    | _ -> ty, state

/// The contexts the lambda `lambda`, with the parameters `args`, requires for its `mut` parameters: `Memory` of each
/// one's scope, since a function that takes a cell can only be called where its block is, whether or not it uses the
/// cell, and `state` once they are recorded. `Memory` is nominal, so it is required by name, as an ambient context is:
/// a def's own body adds it to what the def requires if the def does not write its type (`InferredContexts`), and
/// otherwise the def must write it if it writes contexts at all (see `ensureMemoryWritten`). Any other lambda's type
/// carries it. A function type that takes a cell requires it whether it says so or not (see
/// `FunctionContexts.withCellMemory`).
let parameterContexts (state: InferState) (lambda: Expression) (args: (Identifier * TypeExpr option) list) : Ty list * InferState =
    let required =
        args
        |> List.choose (fun (name, typeOpt) ->
            match typeOpt with
            | CellPassingSyntax.CellParameter _ ->
                state.DeclarationSymbols
                |> Map.tryFind (lambda.Id, LambdaParameters, name)
                |> Option.bind state.Cells.TryFind
                |> Option.map (FunctionContexts.scoped context)
            | _ -> None)
    match required, state.ContextScope with
    | [], _ -> [], state
    | _ when not (state.BodyLambdas.Contains lambda.Id) -> required, state
    | _, DefScope(defName, true) ->
        let inferred = state.InferredContexts |> Map.tryFind defName |> Option.defaultValue Set.empty
        [], { state with InferredContexts = state.InferredContexts |> Map.add defName (inferred.Add context) }
    | _ -> [], state

/// The errors for each function type in `typeOpt`, the annotation of the def `defName`, that takes a cell and writes
/// contexts without `Memory`: taking a cell requires it. A function type that takes one and writes no contexts requires
/// it without saying so (see `TypeResolution`).
let ensureMemoryWritten (state: InferState) (defName: Identifier) (typeOpt: TypeExpr option) : Result<unit, Diagnostic list> =
    let takesCell (input: TypeExpr) =
        let parameters =
            match input.Kind with
            | TypeTuple items -> items
            | _ -> [ input ]
        parameters |> List.exists (fun parameter -> match parameter.Kind with TypeApply(name, [ _ ]) -> name = CellPassingSyntax.typeName | _ -> false)
    let writesMemory (contexts: TypeExpr list) = contexts |> List.exists (fun ctx -> ctx.Kind = TypeName context)
    let missing =
        typeOpt
        |> Option.map TypeExpr.within
        |> Option.defaultValue []
        |> List.choose (fun typeExpr ->
            match typeExpr.Kind with
            | TypeWithContext((first :: _) as contexts, { Kind = TypeFunc(input, _) }) when takesCell input && not (writesMemory contexts) ->
                let names = contexts |> List.map (fun ctx -> match ctx.Kind with TypeName name | TypeApply(name, _) | TypeVar name -> name | _ -> "...")
                Some (SourceSpan.between first.Span (List.last contexts).Span, String.concat ", " names)
            | _ -> None)
    match missing with
    | [] -> Ok ()
    | spans ->
        spans
        |> List.map (fun (span, written) ->
            Diagnostics.at DiagnosticCodes.UndeclaredContext span
                $"'{defName}' takes a cell, a `mut` parameter, so it requires '{context}' too: write '[{written}, {context}]'"
            |> Diagnostics.about context
            |> ContextFixes.withAddToType state defName context)
        |> Error

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
                    $"`mut` passes only a cell, a name declared with `mut` or a `mut` parameter" |> Diagnostics.about name ]
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

/// The errors for the cells' types in `typeOpt`, the annotation of the def `defName` (see `ensureCellTypesPlaced` and
/// `ensureMemoryWritten`).
let checkCellTypes (state: InferState) (defName: Identifier) (typeOpt: TypeExpr option) : Result<unit, Diagnostic list> =
    ensureCellTypesPlaced typeOpt |> Result.bind (fun () -> ensureMemoryWritten state defName typeOpt)

/// The code of a type mismatch found at `span`: an assignment's own when it is one of the module's `:=`s.
let mismatchCode (state: InferState) (span: SourceSpan) =
    if state.CellAssignments.Contains span then DiagnosticCodes.CellAssignmentMismatch else DiagnosticCodes.TypeMismatch
