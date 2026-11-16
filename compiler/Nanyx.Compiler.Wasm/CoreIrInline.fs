/// Inlining in the core IR, after lowering and specialising and before reference counting (see
/// `docs/src/content/work-in-progress/inlining.md`). A call is replaced by the body of the function it calls when the
/// function is a handler called once (or one called more often that is not large), or is a lambda's and the closure over
/// it is called once, or is small. A function in a cycle of
/// calls is never inlined, so inlining ends.
///
/// The callee's body is placed where the call was, with its parameters bound to the call's arguments and every variable
/// it binds made new, so that none shares a local with the caller's, and its loops numbered inside the caller's. A call
/// is inlined where a sequence can hold the body: as what a chain ends in, or as the value of a `let` or a `do`, a
/// temporary's among them. Every temporary of the chain still waiting to be used becomes a local first, since the body
/// stores before it reads what it is given.
module internal Transpiler.Wasm.CoreIrInline

open System.Collections.Generic
open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.CoreIrRewrite

/// The most core IR nodes a function has to be inlined for its size alone.
let sizeLimit = 40

/// The most core IR nodes a handler has to be inlined where it is called more than once.
let private handlerLimit = 4 * sizeLimit

/// The most core IR nodes a function grows to by inlining; nothing more is inlined into it after that.
let private growthLimit = 4000

/// How many times a function's body is gone over, each time simplifying and inlining what the time before exposed: a
/// handler's block, then the members its `use` binds, then the continuations they are given, and so on down a workflow.
let private rounds = 16

/// The functions `e` calls or makes a closure over, by name.
let private references (e: Expr) : string list =
    allExprs e
    |> Seq.collect (fun e ->
        let named =
            match e.Kind with
            | Call(name, _)
            | MakeClosure(name, _, _) -> [ name ]
            | _ -> []
        let static' = directAtoms e |> List.choose (function AStaticClosure(name, _, _) -> Some name | _ -> None)
        named @ static')
    |> List.ofSeq
/// Whether `f` is a handler: a function whose body ends in a call through one of its parameters, as one that runs its
/// lambda parameter under a `use` does.
let private isHandler (f: Function) =
    match snd (Chain.split f.Body) with
    | { Kind = CallClosure(closure, _, _) } -> f.Params |> List.exists (fun p -> p.Id = closure.Id)
    | _ -> false

/// The functions in a cycle of calls (one that calls itself among them), by name: those never inlined.
let private inCycles (functions: Map<string, Function>) (edges: Map<string, string list>) : Set<string> =
    // Tarjan's strongly connected components, without recursion so that a long chain of calls cannot overflow the stack.
    let index = Dictionary<string, int>()
    let low = Dictionary<string, int>()
    let onStack = HashSet<string>()
    let stack = Stack<string>()
    let cyclic = HashSet<string>()
    let mutable next = 0
    let successors name = edges |> Map.tryFind name |> Option.defaultValue [] |> List.filter functions.ContainsKey
    for root in functions.Keys do
        if not (index.ContainsKey root) then
            let work = Stack<string * string list>()
            let enter name =
                index.[name] <- next
                low.[name] <- next
                next <- next + 1
                stack.Push name
                onStack.Add name |> ignore
                work.Push((name, successors name))
            enter root
            while work.Count > 0 do
                let name, remaining = work.Pop()
                match remaining with
                | successor :: rest ->
                    work.Push((name, rest))
                    if not (index.ContainsKey successor) then enter successor
                    elif onStack.Contains successor then low.[name] <- min low.[name] index.[successor]
                | [] ->
                    if work.Count > 0 then
                        let parent, _ = work.Peek()
                        low.[parent] <- min low.[parent] low.[name]
                    if low.[name] = index.[name] then
                        let members = ResizeArray<string>()
                        let mutable popped = ""
                        while popped <> name do
                            popped <- stack.Pop()
                            onStack.Remove popped |> ignore
                            members.Add popped
                        if members.Count > 1 || successors name |> List.contains name then
                            for member' in members do
                                cyclic.Add member' |> ignore
    Set.ofSeq cyclic

/// The functions in an order in which each comes after those it calls, but for calls within a cycle.
let private calleesFirst (functions: Map<string, Function>) (edges: Map<string, string list>) : string list =
    let visited = HashSet<string>()
    let order = ResizeArray<string>()
    for root in functions.Keys do
        if visited.Add root then
            let work = Stack<string * string list>()
            work.Push((root, edges |> Map.tryFind root |> Option.defaultValue []))
            while work.Count > 0 do
                match work.Pop() with
                | name, successor :: rest ->
                    work.Push((name, rest))
                    if functions.ContainsKey successor && visited.Add successor then
                        work.Push((successor, edges |> Map.tryFind successor |> Option.defaultValue []))
                | name, [] -> order.Add name
    List.ofSeq order

/// Inlines calls into one function, `f`, given the functions it may call as they are by now.
type private Inliner(functions: Dictionary<string, Function>, cyclic: Set<string>, f: Function) =
    let mutable nextId =
        let mutable highest = f.Params |> List.fold (fun acc p -> max acc p.Id) 0
        mapVars (fun v -> highest <- max highest v.Id; v) id f.Body |> ignore
        highest
    let mutable instances = 0

    let newId () =
        nextId <- nextId + 1
        nextId

    /// The body of `callee` for a call with `args`, at `loopDepth` loops deep: its parameters stand for the arguments
    /// that are variables of their types, and are new locals let-bound to the others (a lambda's environment, which it
    /// reads as a record, to the closure), and every variable it binds is new.
    member _.Instantiate (callee: Function) (args: Atom list) (loopDepth: int) : Expr =
        instances <- instances + 1
        let suffix = $"__in{instances}"
        let renamed = Dictionary<int, Var>()
        let bindings = ResizeArray<Var * Atom>()
        for param, arg in List.zip callee.Params args do
            match arg with
            | AVar var when var.Type = param.Type -> renamed.[param.Id] <- var
            | _ ->
                let local = { param with Id = newId (); Name = param.Name + suffix; Kind = Local }
                renamed.[param.Id] <- local
                bindings.Add((local, arg))
        let var (v: Var) =
            match renamed.TryGetValue v.Id with
            | true, found -> found
            | _ ->
                let fresh = { v with Id = newId (); Name = v.Name + suffix; Kind = (if v.Kind = Param then Local else v.Kind) }
                renamed.[v.Id] <- fresh
                fresh
        let body = mapVars var (fun depth -> depth + loopDepth) callee.Body
        Seq.foldBack (fun (local, arg) rest -> Expr.make (Let(local, Expr.make (Atom arg) local.Type, rest)) rest.Type) bindings body

    /// One time over `body`: each call that is to be inlined is.
    member this.Round (body: Expr) : Expr * bool =
        // Simplifying makes variables of its own, which new ones must not share an id with.
        mapVars (fun v -> nextId <- max nextId v.Id; v) id body |> ignore
        // How often each function is called here, and the functions this body makes a closure over.
        let calls = Dictionary<string, int>()
        let closures = HashSet<string>()
        for e in allExprs body do
            match e.Kind with
            | Call(name, _) -> calls.[name] <- (match calls.TryGetValue name with | true, n -> n + 1 | _ -> 1)
            | MakeClosure(name, _, _) -> closures.Add name |> ignore
            | _ -> ()
            for atom in directAtoms e do
                match atom with
                | AStaticClosure(name, _, _) -> closures.Add name |> ignore
                | _ -> ()
        let mutable changed = false
        let mutable grown = size body

        // Whether `args` are of the types of `callee`'s parameters. A lambda's first parameter is its environment, the
        // closure (or box) read as a record of what it captures, and a tag is held alike whatever tags its type has, so
        // a function that gives or takes tags is held alike whatever tags its type has too.
        let rec heldAlike (paramTy: IrType) (argTy: IrType) =
            match paramTy, argTy with
            | IrTags _, IrTags _ -> true
            | IrFunction(input, output, members), IrFunction(input', output', members') ->
                heldAlike input input'
                && heldAlike output output'
                && members.Length = members'.Length
                && List.forall2 (fun (key, memberTy) (key', memberTy') -> key = key' && heldAlike memberTy memberTy') members members'
            | _ -> paramTy = argTy
        let typed (callee: Function) (args: Atom list) =
            List.forall2
                (fun (param: Var) arg ->
                    match param.Type, atomType arg with
                    | IrRecord _, (IrFunction _ | IrBox) -> param.Name = ClosureSyntax.envParameter
                    | paramTy, argTy -> heldAlike paramTy argTy)
                callee.Params
                args

        // The function a call is inlined from, with what it is given, if it is to be. A call through a known closure is
        // a call to its function by now (see `CoreIrSimplify`), with the closure as the environment.
        let site (call: Expr) : (Function * Atom list) option =
            match call.Kind with
            | Call(name, args) ->
                match functions.TryGetValue name with
                | true, callee when
                    not (cyclic.Contains callee.Name)
                    && callee.Name <> f.Name
                    && callee.Params.Length = args.Length
                    && typed callee args
                    && callee.Result = call.Type
                    && callee.Body.Type = callee.Result
                    && grown < growthLimit
                    ->
                    let calleeSize = size callee.Body
                    // A handler called once here is inlined whatever its size, and so is a lambda this body makes and
                    // calls once. A handler called more than once is inlined only up to `handlerLimit`: one that calls
                    // another twice would otherwise be copied twice at each level (#1664).
                    let once = calls.[name] = 1
                    let always = (isHandler callee && (once || calleeSize <= handlerLimit)) || (closures.Contains name && once)
                    if always || calleeSize <= sizeLimit then
                        grown <- grown + calleeSize
                        Some(callee, args)
                    else None
                | _ -> None
            | _ -> None

        let rec sequence (loopDepth: int) (e: Expr) : Expr =
            let links, last = Chain.split e
            let rebuilt = ResizeArray<Expr -> Expr>()
            // The temporaries bound so far in the chain that nothing has used yet, in the order they are bound, each with
            // its place among the links and its value.
            let waiting = ResizeArray<Var * int * Expr>()
            // The temporaries made locals, which what follows names by their new variables.
            let madeLocal = Dictionary<int, Var>()
            let renamed (e: Expr) =
                if madeLocal.Count = 0 then e
                else mapVars (fun v -> match madeLocal.TryGetValue v.Id with | true, local -> local | _ -> v) id e
            let claim (value: Expr) =
                let used = (directAtoms value |> List.choose (function AVar v -> Some v.Id | _ -> None)) @ (directVars value |> List.map _.Id)
                waiting.RemoveAll(fun (v, _, _) -> List.contains v.Id used) |> ignore
            let newLocal (temp: Var) =
                let local = { temp with Id = newId (); Name = $"__inlined{nextId}"; Kind = Local }
                madeLocal.[temp.Id] <- local
                local
            // The body of the function `call` calls, if it is to be inlined. Every temporary still waiting is made a local
            // first, since the body stores before it reads what it is given, and nothing is stored while a temporary
            // waits to be used. Temporaries are bound in the order they are evaluated, so evaluating each where it is
            // bound keeps that order.
            let inlined (call: Expr) =
                let waitingIds = waiting |> Seq.map (fun (v, _, _) -> v.Id) |> Set.ofSeq
                let argTemps = directAtoms call |> List.choose (function AVar v when v.Kind = Temp -> Some v.Id | _ -> None)
                if argTemps |> List.forall waitingIds.Contains then
                    site call
                    |> Option.map (fun (callee, args) ->
                        for temp, place, value in waiting do
                            let local = newLocal temp
                            rebuilt.[place] <- (fun next -> bindValue local value next)
                        waiting.Clear()
                        let args = args |> List.map (function AVar v when madeLocal.ContainsKey v.Id -> AVar madeLocal.[v.Id] | other -> other)
                        changed <- true
                        this.Instantiate callee args loopDepth)
                else None
            for link in links do
                match link.Kind with
                | Let(v, value, _) ->
                    let value = renamed value
                    match inlined value with
                    | Some body ->
                        let v = if v.Kind = Temp then newLocal v else v
                        rebuilt.Add(fun next -> bindValue v body next)
                    | None when v.Kind = Temp ->
                        claim value
                        waiting.Add((v, rebuilt.Count, value))
                        rebuilt.Add(fun next -> Expr.make (Let(v, value, next)) next.Type)
                    | None ->
                        claim value
                        let value = nested loopDepth value
                        rebuilt.Add(fun next -> Expr.make (Let(v, value, next)) next.Type)
                | Do(value, _) ->
                    let value = renamed value
                    match inlined value with
                    | Some body -> rebuilt.Add(fun next -> doValue body next)
                    | None ->
                        claim value
                        let value = nested loopDepth value
                        rebuilt.Add(fun next -> Expr.make (Do(value, next)) next.Type)
                | _ -> rebuilt.Add(fun next -> Chain.relink link next)
            let last = renamed last
            let last =
                match inlined last with
                | Some body -> body
                | None -> nested loopDepth last
            Chain.rebuild (fun rebuild next -> rebuild next) (List.ofSeq rebuilt) last

        // An expression that is not a chain, with the sequences inside it gone over.
        and nested (loopDepth: int) (e: Expr) : Expr =
            match e.Kind with
            | If(condition, ifTrue, ifFalse) -> { e with Kind = If(condition, sequence loopDepth ifTrue, sequence loopDepth ifFalse) }
            | Match(scrutinees, arms) ->
                { e with Kind = Match(scrutinees, arms |> List.map (fun arm -> { arm with Body = sequence loopDepth arm.Body })) }
            | ForEach loop -> { e with Kind = ForEach { loop with Body = sequence (loop.Depth + 1) loop.Body } }
            | While loop ->
                { e with Kind = While { loop with Condition = sequence (loop.Depth + 1) loop.Condition; Body = sequence (loop.Depth + 1) loop.Body } }
            | _ -> e

        let result = sequence 0 body
        result, changed

/// `functions` with calls inlined into each. The start function, `start`, is never inlined itself, and nor are the
/// functions in `kept`: a cached value's (see `CoreIrCachedValues`), which a call reads rather than computes.
let inlineCalls (start: string option) (kept: Set<string>) (functions: Function list) : Function list =
    let byName = functions |> List.map (fun f -> f.Name, f) |> Map.ofList
    let edges = byName |> Map.map (fun _ f -> references f.Body)
    let cyclic = inCycles byName edges + kept + Set.ofList (Option.toList start)
    let current = Dictionary<string, Function>()
    for f in functions do
        current.[f.Name] <- f
    for name in calleesFirst byName edges do
        let f = current.[name]
        let inliner = Inliner(current, cyclic, f)
        let mutable body = f.Body
        let mutable round = 0
        let mutable changed = true
        while changed && round < rounds do
            let simplified, didSimplify = CoreIrSimplify.simplify current { f with Body = body }
            let next, didInline = inliner.Round simplified
            body <- next
            changed <- didSimplify || didInline
            round <- round + 1
        current.[name] <- { f with Body = fst (CoreIrSimplify.simplify current { f with Body = body }) }
    // Only once nothing more is inlined: a temporary can be read only where it is computed, which would hide its value
    // from a caller that inlines the function.
    functions
    |> List.map (fun f -> { current.[f.Name] with Body = CoreIrSimplify.withTemporaries current.[f.Name].Body })
