/// Where a function that can wait can stop and carry on later: its split points, which a state machine resumes at (see
/// "How tasks are compiled" in `docs/src/content/deep-dives/async.md`). A function is a state machine when it can stop:
/// when it calls a function value that requires `Async` (`IrFunction`'s `waits`), waits for a task or for a scope's tasks
/// (see `WasmScheduler`), or calls a function that is a state machine itself. Each such call is a split point. This
/// module finds them in the core IR once it is counted, and what each keeps across it; `TaskLowering` turns them into
/// code.
module internal Transpiler.Wasm.CoreIrSplitPoints

open System.Collections.Generic
open Transpiler.Wasm.CoreIr

/// What a split point waits on. Only a call, for now; a sequence that is pulled once may add a `yield` (#2426).
type SplitKind =
    /// A call of a function that can stop: the caller stops with it, and resumes it when it carries on.
    | WaitingCall

/// Where a state machine can stop, and what it keeps there.
type SplitPlan =
    { /// The first state of each split point, by the call that is one (the expression itself, not one equal to it).
      /// States are numbered from 1 in the order the code is written; 0 is "not resuming".
      States: IReadOnlyDictionary<Expr, int>
      /// The lowest and the highest state inside each expression that holds a split point, by the expression: since
      /// states are numbered in the order the code is written, the states inside an expression are a range.
      Ranges: IReadOnlyDictionary<Expr, int * int>
      /// The variables each state keeps in the frame while the function is stopped: those in scope there that the code
      /// after it reads.
      Saved: Map<int, Var list>
      /// The temporaries kept in locals, by id: those whose value holds a split point, and those that wait to be used
      /// while it is computed, which would otherwise be on the stack when the function stops.
      StoredTemps: Set<int>
      /// How many states there are.
      Count: int }

/// The intrinsics that can stop the task running (see `WasmScheduler`): a wait for a task, and the end of a scope.
let private waitingIntrinsics =
    set [ NyxCompiler.Intrinsics.taskWait; NyxCompiler.Intrinsics.scopeExit; NyxCompiler.Intrinsics.hostWait; NyxCompiler.Intrinsics.scopeWaitAny; NyxCompiler.Intrinsics.genYield ]

/// Whether `expr` is a split point, given the functions that are state machines.
let isSplit (machines: Set<string>) (expr: Expr) : bool =
    match expr.Kind with
    | Call(name, _) -> machines.Contains name
    | CallRuntime(name, _, _) -> waitingIntrinsics.Contains name
    | CallClosure(closure, _, _) ->
        match closure.Type with
        | IrFunction(_, _, _, waits) -> waits
        | _ -> false
    | _ -> false

/// How many states the split point `expr` has: two for a call of a function value when every wait first yields
/// (`WasmOptions.YieldAtEveryWait`), one for the yield and one for the call, and one otherwise.
let statesOf (yieldAtEveryWait: bool) (expr: Expr) : int =
    match expr.Kind with
    | CallClosure _ when yieldAtEveryWait -> 2
    | _ -> 1

/// The expressions directly inside `expr` whose code is its own, in the order they are written: not the guards of a
/// match's patterns, which `guardsOf` gives.
let private children (expr: Expr) : Expr list =
    match expr.Kind with
    | Let(_, value, rest) -> [ value; rest ]
    | Do(value, rest) -> [ value; rest ]
    | If(_, ifTrue, ifFalse) -> [ ifTrue; ifFalse ]
    | Match(_, arms) -> arms |> List.map _.Body
    | ForEach loop -> [ loop.Body ]
    | While loop -> [ loop.Condition; loop.Body ]
    | Dup(_, body)
    | Drop(_, body)
    | Release(_, body)
    | FreeToken(_, body)
    | DropAfter(body, _)
    | Reuse(_, body) -> [ body ]
    | _ -> []

/// The guards of the patterns of `expr`'s arms, if it is a match.
let private guardsOf (expr: Expr) : Expr list =
    let rec guards (pattern: Pattern) =
        match pattern with
        | PGuard(_, guard) -> [ guard ]
        | _ -> Pattern.parts pattern |> List.collect guards
    match expr.Kind with
    | Match(_, arms) -> arms |> List.collect (fun arm -> arm.Patterns |> List.collect guards)
    | _ -> []

/// Whether `expr` holds a split point anywhere in it.
let private holdsSplit (machines: Set<string>) (expr: Expr) : bool =
    CoreIrRewrite.allExprs expr |> Seq.exists (isSplit machines)

/// The functions among `functions` that are state machines: each that calls a function value that can wait, or a
/// function that is one, but not `start`, which runs them (see `TaskLowering`).
let stateMachines (start: string) (functions: Function list) : Set<string> =
    let calls =
        functions
        |> List.map (fun func ->
            let callees = HashSet<string>()
            let mutable waits = false
            for expr in CoreIrRewrite.allExprs func.Body do
                match expr.Kind with
                | Call(name, _) -> callees.Add name |> ignore
                | CallClosure _
                | CallRuntime _ when isSplit Set.empty expr -> waits <- true
                | _ -> ()
            func.Name, (waits, callees))
        |> List.filter (fun (name, _) -> name <> start)
    let rec grow (machines: Set<string>) =
        let more =
            calls
            |> List.filter (fun (name, (waits, callees)) -> not (machines.Contains name) && (waits || callees |> Seq.exists machines.Contains))
            |> List.map fst
        if more.IsEmpty then machines else grow (Set.union machines (Set.ofList more))
    grow Set.empty

/// The value a link of a chain computes, if it computes one: a `Let`'s or a `Do`'s.
let private linkValue (link: Expr) : Expr option =
    match link.Kind with
    | Let(_, value, _)
    | Do(value, _) -> Some value
    | _ -> None

/// The variables the counting `expr` does reads (see `CoreIrCounting`): what it dups or drops, the object it releases
/// and the fields it keeps of it, and the token it reuses or frees.
let private countingVars (expr: Expr) : Var list =
    match expr.Kind with
    | Dup(vars, _)
    | Drop(vars, _)
    | DropAfter(_, vars) -> vars
    | Release(released, _) -> released.Object :: (released.Fields |> List.choose (fun (_, var, _) -> var))
    | Reuse(token, _)
    | FreeToken(token, _) -> [ token ]
    | _ -> []

/// The variables each expression in `body` reads, by the expression (itself, not one equal to it).
let private readsIn (body: Expr) : Dictionary<Expr, Set<int>> =
    let reads = Dictionary<Expr, Set<int>>(HashIdentity.Reference)
    let own (expr: Expr) =
        let atoms = CoreIrRewrite.directAtoms expr |> List.choose (function AVar var -> Some var.Id | _ -> None)
        let vars = CoreIrRewrite.directVars expr @ countingVars expr |> List.map _.Id
        Set.ofList (atoms @ vars)
    let rec visit (expr: Expr) : Set<int> =
        match reads.TryGetValue expr with
        | true, found -> found
        | _ ->
            // A chain is walked from its end back, so that a long one cannot overflow the stack.
            let links, last = Chain.split expr
            let mutable found = Set.unionMany (own last :: ((children last @ guardsOf last) |> List.map visit))
            reads.[last] <- found
            for link in List.rev links do
                found <- Set.unionMany [ own link; found; linkValue link |> Option.map visit |> Option.defaultValue Set.empty ]
                reads.[link] <- found
            found
    visit body |> ignore
    reads

/// The variables a for loop's streams walk with.
let private streamVars (loop: ForLoop) : Var list =
    loop.Streams |> List.collect (fun stream -> [ stream.Cursor; stream.End; stream.Element ])

/// `scope` with the token `released` binds, if it binds one.
let private withToken (released: Released) (scope: Map<int, Var>) : Map<int, Var> =
    match released.Token with
    | Some token -> scope |> Map.add token.Id token
    | None -> scope

/// The split points of `func`, a state machine, given the functions that are state machines; none when it has none.
/// A split point in a pattern's guard or a while loop's condition is not supported yet (`cannot`).
let plan (machines: Set<string>) (yieldAtEveryWait: bool) (func: Function) : SplitPlan option =
    let states = Dictionary<Expr, int>(HashIdentity.Reference)
    let ranges = Dictionary<Expr, int * int>(HashIdentity.Reference)
    let saved = Dictionary<int, Var list>()
    let stored = HashSet<int>()
    let reads = readsIn func.Body
    let readsOf (expr: Expr) = match reads.TryGetValue expr with | true, found -> found | _ -> Set.empty
    let mutable next = 1

    // The temporaries that wait to be used where a split point's value is computed are kept in locals, as is the
    // temporary that holds it, in the order the emitter would have waited on them (see `CoreIrEmit.Sequence`).
    let storeAroundSplits (links: Expr list) =
        let waiting = ResizeArray<Var>()
        for link in links do
            match link.Kind with
            | Let(var, value, _) when var.Kind = Temp ->
                if holdsSplit machines value then
                    for temp in waiting do
                        stored.Add temp.Id |> ignore
                    waiting.Clear()
                    stored.Add var.Id |> ignore
                else waiting.Add var
            | _ -> ()

    // Numbers the split points in `expr` in the order they are written, noting what each keeps: `scope` is what is in
    // scope there, by id, and `after` what the code after `expr` reads. Notes the range of states inside each
    // expression that has any.
    let rec walk (scope: Map<int, Var>) (after: Set<int>) (expr: Expr) : unit =
        let first = next
        let links, last = Chain.split expr
        if not links.IsEmpty then storeAroundSplits links
        let mutable scope = scope
        let followers = if links.IsEmpty then [] else List.tail links @ [ last ]
        for link, following in List.zip links followers do
            match link.Kind with
            | Let(var, value, _) ->
                walk scope (Set.union after (readsOf following)) value
                scope <- scope |> Map.add var.Id var
            | Do(value, _) -> walk scope (Set.union after (readsOf following)) value
            // A release binds the token of the block it gives up, which a later allocation reuses.
            | Release(released, _) -> scope <- withToken released scope
            | _ -> ()
        walkLast scope after last
        if next > first then ranges.[expr] <- (first, next - 1)

    and walkLast (scope: Map<int, Var>) (after: Set<int>) (expr: Expr) : unit =
        let first = next
        if isSplit machines expr then
            let keep = scope |> Map.filter (fun id _ -> after.Contains id) |> Map.values |> List.ofSeq
            states.[expr] <- next
            let count = statesOf yieldAtEveryWait expr
            if count = 2 then
                // The yield comes before the call reads the function it calls and its arguments, so it keeps them too;
                // a temporary among them is kept in a local, so that it is computed before the yield.
                let arguments = CoreIrRewrite.directAtoms expr |> List.choose (function AVar var -> Some var | _ -> None)
                for argument in arguments do
                    if argument.Kind = Temp then stored.Add argument.Id |> ignore
                let calling = readsOf expr
                saved.[next] <- (scope |> Map.filter (fun id _ -> after.Contains id || calling.Contains id) |> Map.values |> List.ofSeq)
                    @ (arguments |> List.filter (fun var -> var.Kind = Temp))
                saved.[next + 1] <- keep
            else saved.[next] <- keep
            next <- next + count
        else
            if guardsOf expr |> List.exists (holdsSplit machines) then
                CoreIrLowerTypes.cannot "a call that can wait in a pattern's guard"
            match expr.Kind with
            | If(_, ifTrue, ifFalse) ->
                walk scope after ifTrue
                walk scope after ifFalse
            | Match(_, arms) ->
                for arm in arms do
                    let bound = arm.Patterns |> List.collect Pattern.binds
                    walk (bound |> List.fold (fun acc var -> acc |> Map.add var.Id var) scope) after arm.Body
            | ForEach loop ->
                let looping = Set.union after (readsOf expr)
                let inLoop = streamVars loop |> List.fold (fun acc var -> acc |> Map.add var.Id var) scope
                walk inLoop (Set.unionMany [ looping; streamVars loop |> List.map _.Id |> Set.ofList ]) loop.Body
            | While loop ->
                if holdsSplit machines loop.Condition then
                    CoreIrLowerTypes.cannot "a call that can wait in a while loop's condition"
                walk scope (Set.union after (readsOf expr)) loop.Body
            | Release(released, body) -> walk (withToken released scope) after body
            | Dup(_, body)
            | Drop(_, body)
            | FreeToken(_, body)
            | Reuse(_, body) -> walk scope after body
            | DropAfter(body, vars) -> walk scope (Set.union after (vars |> List.map _.Id |> Set.ofList)) body
            | _ -> ()
        if next > first then ranges.[expr] <- (first, next - 1)

    let parameters = func.Params |> List.fold (fun acc var -> acc |> Map.add var.Id var) Map.empty
    walk parameters Set.empty func.Body
    if next = 1 then None
    else
        Some
            { States = states
              Ranges = ranges
              Saved = saved |> Seq.map (fun pair -> pair.Key, pair.Value) |> Map.ofSeq
              StoredTemps = Set.ofSeq stored
              Count = next - 1 }

/// Whether `expr` holds a split point, by the plan: whether it has states inside it.
let holds (plan: SplitPlan) (expr: Expr) : bool = plan.Ranges.ContainsKey expr
