/// What the rules of tasks need checked once a module is inferred (#1890, see "Structured concurrency" in
/// `docs/src/content/work-in-progress/concurrency-and-crash-boundaries.md`).
///
/// - A task shares only immutable values: starting a task with a function that holds what belongs to a `memory`
///   block, a cell, an `MRef` or an `MList`, is an error (NYX0279). Such a function requires the block's scoped
///   `Memory`, as anything that uses what the block holds does, so the check reads the contexts a task's work requires
///   wherever a def that starts tasks is named, under any name and through any def that passes its work on (see
///   `TaskWork`).
/// - A def that is a scope (see `TaskScopes`) can't give back a lambda that starts a task (NYX0233): a lambda requires
///   its contexts where it is called, so nothing else ties it to the def, which would have returned when it starts one.
///   A `Task` it gives back is the escape of the scope's call already.
module internal NyxCompiler.TaskCaptures

open Parser.Program

/// The errors of the places in `module'` where a def that starts tasks is named, `nanyx/async`'s `start` or `race`, or
/// one that starts them with a function it is given, whose task's work (see `TaskWork`), as `resolve` gives it once the
/// module is inferred, requires a `memory` block's `Memory`. A call of it with one argument is reported at the argument.
let captureErrors (taskWork: Map<int, SourceSpan>) (resolve: Ty -> Ty) (module': Module) : Diagnostic list =
    let places =
        taskWork
        |> Map.toList
        |> List.filter (fun (id, _) -> TaskWork.holdsMemory (resolve (TyVar(TyVar.ordinary id None))))
        |> List.map snd
        |> Set.ofList
    if places.IsEmpty then
        []
    else
        let calls =
            module'
            |> List.collect SyntaxTree.ofItem
            |> List.collect SyntaxTree.expressionsWithin
            |> List.choose (fun expr ->
                match expr.Kind with
                | FunctionCall(_, _, [ work ]) when places.Contains expr.Span -> Some (expr.Span, work.Span)
                | _ -> None)
            |> Map.ofList
        places
        |> Set.toList
        |> List.map (fun place ->
            Diagnostics.at
                DiagnosticCodes.TaskHoldsMemory
                (calls |> Map.tryFind place |> Option.defaultValue place)
                "A task can't hold what belongs to a 'memory' block: the function it runs uses one of the block's cells or references. Tasks share only immutable values: pass the task what a cell holds, not the cell")

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
                |> List.tryFind (fun within ->
                    match within.Kind with
                    | FunctionCall(callee, _, _) -> callee = TaskScopes.startName || callee = TaskScopes.startDef
                    | _ -> false)
                |> Option.map _.Span
                |> Option.defaultValue value.Span
            Some (
                Diagnostics.at
                    DiagnosticCodes.ScopedContextEscapes
                    at
                    $"A task can't leave the def that started it: '{name}' gives back what starts one, which would run once '{name}' has returned. The def is the scope of its tasks, and waits for them before it returns")
        | _ -> None)

/// Whether the function type `ty` itself requires the scope of tasks where it is called.
let private requiresTaskScope (ty: Ty) : bool =
    match Unifier.unalias ty with
    | TyFunc(_, _, contexts) ->
        contexts |> List.exists (function FunctionContexts.ScopedContext(name, _) -> name = TaskScopes.scopeContext | _ -> false)
    | _ -> false

/// The errors of the module-level defs of `module'` that start a task but are not scopes, because their bodies start it
/// without naming `start` (through `def go = Async.start`, say; see `TaskScopes`): their types, by `typeOf`, require
/// the scope of tasks, which nothing outside a scope supplies. Each is reported at the def's first call whose callee,
/// by its type at the call (`calleeType`), requires the scope. A def that writes its type is checked against it.
let unscopedErrors (typeOf: string -> Ty option) (calleeType: NodeId -> Ty option) (module': Module) : Diagnostic list =
    let rec defs (items: TopLevelItem list) =
        items
        |> List.collect (fun item ->
            match item.Kind with
            | Def(ValueDef(_, _, _, { Kind = Lambda(_, { Kind = FunctionCall(callee, _, _) }, _) })) when callee = TaskScopeSyntax.scopeCall -> []
            | Def(ValueDef(_, name, None, ({ Kind = Lambda _ } as value))) -> [ name, value ]
            | ModuleBlock(_, inner) -> defs inner
            | _ -> [])
    defs module'
    |> List.choose (fun (name, value) ->
        if typeOf name |> Option.exists requiresTaskScope then
            SyntaxTree.expressionsWithin value
            |> List.tryPick (fun within ->
                match within.Kind with
                | FunctionCall(callee, _, _) when calleeType within.Id |> Option.exists requiresTaskScope -> Some (callee, within.Span)
                | _ -> None)
            |> Option.map (fun (callee, at) ->
                Diagnostics.at
                    DiagnosticCodes.UndeclaredContext
                    at
                    $"'{callee}' starts a task, but '{name}' is not the scope of its tasks: only a def whose body names '{TaskScopes.startName}' is, and waits for the tasks it starts before it returns. Start the task with '{TaskScopes.startName}' in '{name}' itself")
        else
            None)
