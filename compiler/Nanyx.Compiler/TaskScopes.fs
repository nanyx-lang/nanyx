/// Every def is a scope for the tasks it starts (#1890, see "Structured concurrency" in
/// `docs/src/content/work-in-progress/concurrency-and-crash-boundaries.md`): a module-level def whose body starts a
/// task, directly or in a lambda written in it, doesn't return until every task it started has finished, and nothing
/// that holds a task can leave it. At a script's top level, the scope is the program.
///
/// The scope is `nanyx/async`'s `scope`, which supplies the scoped context `TaskScope` that starting a task requires, as
/// `memory` supplies `Memory`. So the body of each such def, and of a script's entry point, is the block of a call of
/// `scope`, and the rules that keep what requires a scoped context in its scope (`ScopedCalls`) keep its tasks in it:
/// a `Task`, or a lambda that starts one, given back from the def is NYX0233.
module internal NyxCompiler.TaskScopes

open Parser.Program

/// The module that declares tasks.
let private asyncModule = StandardLibrary.importPrefix + ModuleOrigin.asyncName

/// The scoped context a task belongs to, which `scope` supplies.
let scopeContext = "TaskScope"

/// The name a task is started by: `start! e` is `start({ e })` (a `#delay` keyword, see `WorkflowDesugaring`), the
/// bare name of `Async.start`, which is in scope wherever `Async` is (see `AttachedContextDefs`).
let startName = "start"

/// The def that starts a task, as it is declared.
let startDef = "Async." + startName

/// Whether `expr` starts a task: whether it names `start`, called or as a value, anywhere in it, a lambda included,
/// where nothing in it binds that name (a local `def start` is not the one that starts a task).
let private startsTask (expr: Expression) : bool =
    SyntaxTree.freeNames expr |> List.exists (fun name -> name = startName || name = startDef)

/// Whether the module defines a `start` of its own, at its top level or in a module block, so that `start` in it is
/// not the one that starts a task.
let rec private definesStart (items: TopLevelItem list) : bool =
    items
    |> List.exists (fun item ->
        match item.Kind with
        | Def(ValueDef(_, name, _, _)) -> name = startName
        | ModuleBlock(_, inner) -> definesStart inner
        | _ -> false)

/// `body` as the block of a call of `scope`, at `body`'s span.
let private scoped (body: Expression) : Expression =
    let block = Expression.at body.Span (Lambda([], body, false))
    Expression.at body.Span (FunctionCall(TaskScopeSyntax.scopeCall, None, [ block ]))

/// `value`, the value of a module-level def, with its body a scope if it starts a task.
let private scopedValue (value: Expression) : Expression option =
    match value.Kind with
    | Lambda(parameters, body, takesUnit) when startsTask value -> Some { value with Kind = Lambda(parameters, scoped body, takesUnit) }
    | _ -> None

/// `items`, with each module-level def that starts a task a scope, and whether any is.
let rec private scopedItems (isEntry: Expression -> bool) (items: TopLevelItem list) : TopLevelItem list * bool =
    let mutable any = false
    let items =
        items
        |> List.map (fun item ->
            match item.Kind with
            | Def(ValueDef(isExport, name, typeOpt, value)) ->
                match scopedValue value with
                | Some value ->
                    any <- true
                    { item with Kind = Def(ValueDef(isExport, name, typeOpt, value)) }
                | None -> item
            | ModuleBlock(name, inner) ->
                let inner, anyInner = scopedItems isEntry inner
                any <- any || anyInner
                { item with Kind = ModuleBlock(name, inner) }
            // A script's entry point, the lambda its statements are made into (see `Scripts.asEntry`).
            | Expr({ Kind = FunctionCall(callee, range, [ entry ]) } as call) when isEntry call ->
                match scopedValue entry with
                | Some entry ->
                    any <- true
                    { item with Kind = Expr { call with Kind = FunctionCall(callee, range, [ entry ]) } }
                | None -> item
            | _ -> item)
    items, any

/// `module'` with each module-level def that starts a task, and a script's entry point if it does, a scope, unless the
/// module defines a `start` of its own. `isEntry` says whether a top-level call is the one that runs the script's entry
/// point. A module with a scope imports `nanyx/async` for it, under `TaskScopeSyntax.alias`.
let wrap (isEntry: Expression -> bool) (module': Module) : Module =
    if definesStart module' then
        module'
    else
        match scopedItems isEntry module' with
        | items, true -> TopLevelItem.synthetic (Import [ ImportItem.synthetic (ModuleImport asyncModule) (Some TaskScopeSyntax.alias) ]) :: items
        | items, false -> items
