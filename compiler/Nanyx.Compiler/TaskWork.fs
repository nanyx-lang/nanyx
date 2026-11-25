/// What a task's function requires, which it requires where the task runs: after the call that starts it, perhaps once
/// the `memory` block around that call has ended. So no context a task's function requires may be a block's `Memory`
/// (NYX0279, see `TaskCaptures`).
///
/// In the type of a def of `nanyx/async` that starts tasks, `Async.start: [Async, TaskScope, c] ([Async, c] () -> a) ->
/// Task(a)` or `Async.race`, a context variable of the functions it takes stands for what a task's function requires:
/// a _work variable_. Each time inference names such a def, the variables its type is instantiated with there are work
/// variables, and so is whatever one comes to stand for in a def that is generalised, so that a def that starts tasks
/// with a function it is given (`def spawn = { f -> start(f) }`), or that is `start` under another name, has work
/// variables too, instantiated afresh where it is named. Once the module is inferred, a work variable whose contexts
/// have a `Memory` is the error, at the place it was named.
module internal NyxCompiler.TaskWork

/// The file `nanyx/async` is declared in, as a symbol's declaration names it.
let asyncFile = "nanyx/" + ModuleOrigin.asyncName + ".nyx"

/// The defs of `nanyx/async` that start a task with each function they are given, by their declared and bare names.
let private starters = set [ "Async.start"; "start"; "Async.race"; "race" ]

/// Whether `symbol` is a def of `nanyx/async` that starts tasks.
let startsTasks (symbol: Symbol) : bool =
    starters.Contains symbol.Name && symbol.Declaration.File = asyncFile

/// The context variables of the functions `ty`, the type of a def that starts tasks, takes: its work variables.
let variables (ty: Ty) : int list =
    let rec ofFunctions (ty: Ty) =
        match ty with
        | TyFunc(input, result, contexts) ->
            (contexts |> List.choose (function TyVar v -> Some v.Id | _ -> None)) @ ofFunctions input @ ofFunctions result
        | _ -> Ty.fold (fun found child -> found @ ofFunctions child) [] ty
    match ty with
    | TyFunc(input, _, _) -> ofFunctions input |> List.distinct
    | _ -> []

/// The variables among the contexts of any function in `ty`, or that a type generic in contexts carries: those a work
/// variable can stand for, as it stands for contexts.
let contextVariables (ty: Ty) : Set<int> =
    let rec visit (found: Set<int>) (ty: Ty) : Set<int> =
        let found =
            match ty with
            | TyFunc(_, _, contexts)
            | TyContexts contexts -> contexts |> List.fold (fun (found: Set<int>) context -> match context with TyVar v -> found.Add v.Id | _ -> found) found
            | _ -> found
        Ty.fold visit found ty
    visit Set.empty ty

/// Whether the contexts `contexts`, what a work variable stands for, have a `memory` block's `Memory`.
let holdsMemory (contexts: Ty) : bool =
    FunctionContexts.flatten [ contexts ]
    |> List.exists (function
        | FunctionContexts.ScopedContext(name, _) -> name = FunctionContexts.cellContext
        | _ -> false)
