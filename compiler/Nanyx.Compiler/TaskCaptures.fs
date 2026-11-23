/// What the rules of tasks need checked once a module is inferred (#1890, see "Structured concurrency" in
/// `docs/src/content/work-in-progress/concurrency-and-crash-boundaries.md`).
///
/// - A task shares only immutable values: starting a task with a function that holds what belongs to a `memory`
///   block, a cell, an `MRef` or an `MList`, is an error (NYX0279). Such a function requires the block's scoped
///   `Memory`, as anything that uses what the block holds does, so the check reads the contexts its type requires.
/// - A def that is a scope (see `TaskScopes`) can't give back a lambda that starts a task (NYX0233): a lambda requires
///   its contexts where it is called, so nothing else ties it to the def, which would have returned when it starts one.
///   A `Task` it gives back is the escape of the scope's call already.
module internal NyxCompiler.TaskCaptures

open Parser.Program

/// The file `nanyx/async` is declared in, as a symbol's declaration names it (see `ModuleOrigins.sourceFileName`).
let private asyncFile = StandardLibrary.importPrefix + ModuleOrigin.asyncName + ".nyx"

/// The errors of the calls in `module'` that start a task, `nanyx/async`'s `start`, with a function that requires a
/// `memory` block's `Memory`: the type of each node is `typeOf`'s, and the symbol each name resolved to `references`'s.
let captureErrors (symbols: Map<SymbolId, Symbol>) (references: Map<NodeId, SymbolId>) (typeOf: NodeId -> Ty option) (module': Module) : Diagnostic list =
    let startsTask (call: Expression) =
        references
        |> Map.tryFind call.Id
        |> Option.bind (fun symbol -> symbols |> Map.tryFind symbol)
        |> Option.exists (fun symbol -> symbol.Name = TaskScopes.startName && symbol.Declaration.File = asyncFile)
    let holdsMemory (ty: Ty) =
        match Unifier.unalias ty with
        | TyFunc(_, _, contexts) ->
            contexts |> List.exists (function FunctionContexts.ScopedContext(name, _) -> name = MemoryCells.context | _ -> false)
        | _ -> false
    module'
    |> List.collect SyntaxTree.ofItem
    |> List.collect SyntaxTree.expressionsWithin
    |> List.choose (fun expr ->
        match expr.Kind with
        | FunctionCall(_, _, [ work ]) when startsTask expr && (typeOf work.Id |> Option.exists holdsMemory) ->
            Some (
                Diagnostics.at
                    DiagnosticCodes.TaskHoldsMemory
                    work.Span
                    "A task can't hold what belongs to a 'memory' block: this function uses one of the block's cells or references. Tasks share only immutable values: pass the task what a cell holds, not the cell")
        | _ -> None)

/// Whether a function in `ty` requires the scoped context `context`. A value of a type in its scope that is part of it
/// is the escape of the scope's call, which `ScopedCalls` reports.
let rec private mentionsScope (context: string) (ty: Ty) : bool =
    match ty with
    | FunctionContexts.ScopedContext(name, _) when name = context -> true
    // A value of a type in a scope carries the scope as an argument of its type, which isn't a function's requirement.
    | TyNominal(_, args, _, _) ->
        args |> List.exists (function FunctionContexts.ScopedContext _ -> false | arg -> mentionsScope context arg)
    | _ -> Ty.exists (mentionsScope context) ty

/// The errors of the module-level defs of `module'` that are scopes, whose result, by `resultOf`, holds what starts a
/// task: reported at the def's first start of one.
let escapeErrors (resultOf: string -> Ty option) (module': Module) : Diagnostic list =
    let rec defs (items: TopLevelItem list) =
        items
        |> List.collect (fun item ->
            match item.Kind with
            | Def(ValueDef(_, name, _, ({ Kind = Lambda(_, { Kind = FunctionCall(callee, _, _) }, _) } as value))) when callee = TaskScopeSyntax.scopeCall -> [ name, value ]
            | ModuleBlock(_, inner) -> defs inner
            | _ -> [])
    defs module'
    |> List.choose (fun (name, value) ->
        match resultOf name with
        | Some result when mentionsScope TaskScopes.scopeContext result ->
            let at =
                SyntaxTree.expressionsWithin value
                |> List.tryFind (fun within -> match within.Kind with FunctionCall(callee, _, _) -> callee = TaskScopes.startName | _ -> false)
                |> Option.map _.Span
                |> Option.defaultValue value.Span
            Some (
                Diagnostics.at
                    DiagnosticCodes.ScopedContextEscapes
                    at
                    $"A task can't leave the def that started it: '{name}' gives back what starts one, which would run once '{name}' has returned. The def is the scope of its tasks, and waits for them before it returns")
        | _ -> None)
