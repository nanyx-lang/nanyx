/// Every def is a scope for the tasks it starts (#1890, see "Structured concurrency" in
/// `docs/src/content/work-in-progress/concurrency-and-crash-boundaries.md`): a module-level def whose body starts a
/// task, directly or in a lambda written in it, doesn't return until every task it started has finished, and nothing
/// that holds a task can leave it. At a script's top level, the scope is the program.
///
/// The scope is `nanyx/async`'s `Async.concurrent`, which supplies the scoped context `TaskScope` that starting a task requires, as
/// `memory` supplies `Memory`. So the body of each such def, and of a script's entry point, is the block of a call of
/// `Async.concurrent`, and the rules that keep what requires a scoped context in its scope (`ScopedCalls`) keep its tasks in it:
/// a `Task`, or a lambda that starts one, given back from the def is NYX0233.
module internal NyxCompiler.TaskScopes

open Parser.Program

/// The module that declares tasks.
let private asyncModule = StandardLibrary.importPrefix + ModuleOrigin.asyncName

/// The scoped context a task belongs to, which `Async.concurrent` supplies.
let scopeContext = TaskScopeSyntax.context

/// The name a task is started by: `start! e` is `start({ e })` (a `#delay` keyword, see `WorkflowDesugaring`), the
/// bare name of `Async.start`, which is in scope wherever `Async` is (see `AttachedContextDefs`).
let startName = "start"

/// The def that starts a task, as it is declared.
let startDef = "Async." + startName

/// Whether `expr` starts a task: whether it names `Async.start`, or `start` unless the module defines its own
/// (`ownStart`), called or as a value, anywhere in it, a lambda included, where nothing in it binds that name (a local
/// `def start` is not the one that starts a task).
let private startsTask (ownStart: bool) (expr: Expression) : bool =
    SyntaxTree.freeNames expr |> List.exists (fun name -> (name = startName && not ownStart) || name = startDef)

/// Whether the module defines a `start` of its own, at its top level or in a module block, so that `start` in it is
/// not the one that starts a task.
let rec private definesStart (items: TopLevelItem list) : bool =
    items
    |> List.exists (fun item ->
        match item.Kind with
        | Def(ValueDef(_, name, _, _)) -> name = startName
        | ModuleBlock(_, inner) -> definesStart inner
        | _ -> false)

/// `body` as the block of a call of `Async.concurrent`, at `body`'s span.
let private scoped (body: Expression) : Expression =
    let block = Expression.at body.Span (Lambda([], body, false))
    Expression.at body.Span (FunctionCall(TaskScopeSyntax.scopeCall, None, [ block ]))

/// `value`, the value of a module-level def, with its body a scope if it starts a task.
let private scopedValue (ownStart: bool) (value: Expression) : Expression option =
    match value.Kind with
    | Lambda(parameters, body, takesUnit) when startsTask ownStart value -> Some { value with Kind = Lambda(parameters, scoped body, takesUnit) }
    | _ -> None

// A scope def that is called last by one joins its caller's scope (#2562): the call stays a tail call, so a def that
// starts tasks and calls itself last is a loop, and the tasks of every turn belong to the outermost call's scope, which
// waits for them. So each scope def `f` that writes a function's type is two: `f`, whose body opens the scope and calls
// the other in it, and `TaskScopeSyntax.inCallersScope f`, which has `f`'s body and writes `f`'s type requiring its
// caller's scope too. A call that is last in the body of such a def, of another of the same module or module block,
// calls the second. A scope def that writes no type stays one def, its body the scope (#2665).

/// `written`, the type a scope def writes, as the def that runs its body in its caller's scope writes it: requiring the
/// scope of tasks too. `None` when it is not a function's type.
let private requiringScope (written: TypeExpr) : TypeExpr option =
    let scope = TypeExpr.at written.Span (TypeName $"{TaskScopeSyntax.alias}.{scopeContext}")
    match written.Kind with
    | TypeWithContext(contexts, signature) -> Some { written with Kind = TypeWithContext(contexts @ [ scope ], signature) }
    | TypeFunc _ -> Some (TypeExpr.at written.Span (TypeWithContext([ scope ], written)))
    | _ -> None

/// `body` with each call in a place it gives back, of one of `scopeDefs` by a name in `free`, calling the def that runs
/// that def's body in its caller's scope instead.
let rec private callingInScope (scopeDefs: Set<Identifier>) (free: Set<NodeId>) (body: Expression) : Expression =
    let last = callingInScope scopeDefs free
    match body.Kind with
    | FunctionCall(name, range, args) when scopeDefs.Contains name && free.Contains body.Id ->
        { body with Kind = FunctionCall(TaskScopeSyntax.inCallersScope name, range, args) }
    | Block statements ->
        match List.rev statements with
        | ({ Kind = ExprStatement value } as statement) :: before ->
            { body with Kind = Block(List.rev ({ statement with Kind = ExprStatement(last value) } :: before)) }
        | _ -> body
    | IfExpr(condition, whenTrue, whenFalse) -> { body with Kind = IfExpr(condition, last whenTrue, last whenFalse) }
    | Match(values, arms) -> { body with Kind = Match(values, arms |> List.map (fun (patterns, arm) -> patterns, last arm)) }
    | UseIn(value, inner) -> { body with Kind = UseIn(value, last inner) }
    | ContextNames(names, inner) -> { body with Kind = ContextNames(names, last inner) }
    | _ -> body

/// The scope def `name`, whose type is `written` and whose value is the lambda `value`, as the two defs it is: the one
/// that runs its body in its caller's scope, and itself, opening the scope and calling that one in it. `None` when it
/// writes no function's type: the def that runs its body would require the caller's scope without writing it.
let private splitScopeDef (scopeDefs: Set<Identifier>) (isExport: bool) (name: Identifier) (written: TypeExpr option) (value: Expression) =
    match value.Kind, written |> Option.bind requiringScope with
    | Lambda(parameters, body, takesUnit), Some inScopeType ->
        let free =
            SyntaxTree.freeReferences value
            |> List.choose (fun (referenced, at) -> if scopeDefs.Contains referenced then Some at.Id else None)
            |> Set.ofList
        let inScopeName = TaskScopeSyntax.inCallersScope name
        let inScope = ValueDef(false, inScopeName, Some inScopeType, { value with Kind = Lambda(parameters, callingInScope scopeDefs free body, takesUnit) })
        // The opening def names its parameters itself, as one written `_` can't be passed on.
        let passed = parameters |> List.mapi (fun index (_, written) -> $"__scopeArgument{index}", written)
        let arguments = passed |> List.map (fun (parameter, _) -> Expression.at body.Span (IdentifierExpr parameter))
        let call = Expression.at body.Span (FunctionCall(inScopeName, None, arguments))
        let opening = ValueDef(isExport, name, written, Expression.from value (Lambda(passed, scoped call, takesUnit)))
        Some(inScope, opening)
    | _ -> None

/// `items`, with each module-level def that starts a task a scope, and whether any is.
let rec private scopedItems (ownStart: bool) (isEntry: Expression -> bool) (items: TopLevelItem list) : TopLevelItem list * bool =
    let mutable any = false
    let scopeDefs =
        items
        |> List.choose (fun item ->
            match item.Kind with
            // An attached def, `Type.name`, is called by its type and stays one def.
            | Def(ValueDef(_, name, Some written, ({ Kind = Lambda _ } as value))) when startsTask ownStart value && (requiringScope written).IsSome && not (name.Contains '.') -> Some name
            | _ -> None)
        |> Set.ofList
    let items =
        items
        |> List.collect (fun item ->
            match item.Kind with
            | Def(ValueDef(isExport, name, typeOpt, value)) ->
                let split = if scopeDefs.Contains name then splitScopeDef scopeDefs isExport name typeOpt value else None
                match split, scopedValue ownStart value with
                | Some(inScope, opening), _ ->
                    any <- true
                    [ { item with Kind = Def inScope; Id = NodeId.next () }; { item with Kind = Def opening } ]
                | None, Some value ->
                    any <- true
                    [ { item with Kind = Def(ValueDef(isExport, name, typeOpt, value)) } ]
                | None, None -> [ item ]
            | ModuleBlock(name, inner) ->
                let inner, anyInner = scopedItems ownStart isEntry inner
                any <- any || anyInner
                [ { item with Kind = ModuleBlock(name, inner) } ]
            // A script's entry point, the lambda its statements are made into (see `Scripts.asEntry`).
            | Expr({ Kind = FunctionCall(callee, range, [ entry ]) } as call) when isEntry call ->
                match scopedValue ownStart entry with
                | Some entry ->
                    any <- true
                    [ { item with Kind = Expr { call with Kind = FunctionCall(callee, range, [ entry ]) } } ]
                | None -> [ item ]
            | _ -> [ item ])
    items, any

/// `module'` with each module-level def that starts a task, and a script's entry point if it does, a scope. In a module
/// that defines a `start` of its own, only `Async.start` starts one. `isEntry` says whether a top-level call is the one
/// that runs the script's entry point. A module with a scope imports `nanyx/async` for it, under `TaskScopeSyntax.alias`.
let wrap (isEntry: Expression -> bool) (module': Module) : Module =
    match scopedItems (definesStart module') isEntry module' with
    | items, true -> TopLevelItem.synthetic (Import [ ImportItem.synthetic (ModuleImport asyncModule) (Some TaskScopeSyntax.alias) ]) :: items
    | items, false -> items
