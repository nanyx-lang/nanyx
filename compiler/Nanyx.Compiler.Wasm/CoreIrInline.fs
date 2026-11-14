/// Inlining in the core IR, after lowering and specialising and before reference counting (see
/// `docs/src/content/work-in-progress/inlining.md`). A call is replaced by the body of the function it calls when the
/// function is a handler, or is a lambda's and the closure over it is called once, or is small. A function in a cycle of
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

/// The most core IR nodes a function has to be inlined for its size alone.
let sizeLimit = 40

/// The most core IR nodes a function grows to by inlining; nothing more is inlined into it after that.
let private growthLimit = 4000

/// How many times a function's body is gone over, each time inlining what the time before exposed: a handler's block,
/// then the members its `use` binds.
let private rounds = 4

/// Every expression in `e`, each once, in no particular order.
let private allExprs (e: Expr) : Expr seq =
    seq {
        let pending = Stack<Expr>()
        pending.Push e
        while pending.Count > 0 do
            let current = pending.Pop()
            yield current
            for child in Expr.children current do
                pending.Push child
    }

/// How many core IR nodes `e` has.
let private size (e: Expr) = Seq.length (allExprs e)

/// The atoms `e` reads directly.
let private directAtoms (e: Expr) : Atom list =
    match e.Kind with
    | Atom atom
    | MakeTag(_, atom)
    | Field(atom, _)
    | Assign(_, atom)
    | SetField(_, _, atom)
    | Dbg atom
    | If(atom, _, _) -> [ atom ]
    | Prim(_, atoms)
    | Call(_, atoms)
    | MakeList atoms
    | CallRuntime(_, atoms, _)
    | MakeClosure(_, _, atoms)
    | CallClosure(_, atoms) -> atoms
    | MakeRecord fields -> List.map snd fields
    | UpdateRecord(record, updates) -> record :: List.map snd updates
    | ForEach loop -> ForLoop.sourceAtoms loop
    | Interpolate _
    | BoxFloat _
    | Panic _
    | While _
    | Let _
    | Do _
    | Match _
    | Dup _
    | Drop _
    | DropAfter _
    | Release _
    | Reuse _ -> []

/// The variables `e` reads directly, other than through its atoms.
let private directVars (e: Expr) : Var list =
    match e.Kind with
    | CallClosure(var, _)
    | Assign(var, _)
    | SetField(var, _, _) -> [ var ]
    | Match(scrutinees, _) -> scrutinees
    | Interpolate parts -> parts |> List.choose (function Hole(var, _) -> Some var | Text _ -> None)
    | _ -> []

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

/// `e` with each variable, bound or read, replaced as `var` says, and each loop's depth as `depth` says.
let rec private mapVars (var: Var -> Var) (depth: int -> int) (e: Expr) : Expr =
    let go = mapVars var depth
    let atom (a: Atom) =
        match a with
        | AVar v -> AVar(var v)
        | _ -> a
    let atoms = List.map atom
    let rec pattern (p: Pattern) =
        match p with
        | PBind v -> PBind(var v)
        | PGuard(v, guard) -> PGuard(var v, go guard)
        | PTag(name, inner) -> PTag(name, Option.map pattern inner)
        | PFields fields -> PFields(fields |> List.map (fun (slot, inner) -> slot, pattern inner))
        | PAll parts -> PAll(List.map pattern parts)
        | PList(elements, rest) -> PList(List.map pattern elements, Option.map pattern rest)
        | PWildcard
        | PInt _
        | PBool _
        | PString _ -> p
    let source (s: ForSource) =
        match s with
        | OverList list -> OverList(atom list)
        | OverRange(first, last) -> OverRange(atom first, atom last)
        | OverRangeFrom first -> OverRangeFrom(atom first)
    let kind =
        match e.Kind with
        | Atom a -> Atom(atom a)
        | Prim(op, args) -> Prim(op, atoms args)
        | Call(name, args) -> Call(name, atoms args)
        | MakeTag(name, payload) -> MakeTag(name, atom payload)
        | MakeRecord fields -> MakeRecord(fields |> List.map (fun (name, value) -> name, atom value))
        | Field(record, slot) -> Field(atom record, slot)
        | UpdateRecord(record, updates) -> UpdateRecord(atom record, updates |> List.map (fun (slot, value) -> slot, atom value))
        | Assign(target, value) -> Assign(var target, atom value)
        | SetField(target, slot, value) -> SetField(var target, slot, atom value)
        | MakeList items -> MakeList(atoms items)
        | CallRuntime(name, args, returnsValue) -> CallRuntime(name, atoms args, returnsValue)
        | MakeClosure(name, arity, captured) -> MakeClosure(name, arity, atoms captured)
        | CallClosure(closure, args) -> CallClosure(var closure, atoms args)
        | Interpolate parts -> Interpolate(parts |> List.map (function Hole(v, holeType) -> Hole(var v, holeType) | text -> text))
        | Dbg value -> Dbg(atom value)
        | BoxFloat _
        | Panic _ -> e.Kind
        | ForEach loop ->
            ForEach
                { Streams =
                    loop.Streams
                    |> List.map (fun stream ->
                        { Source = source stream.Source
                          Cursor = var stream.Cursor
                          End = var stream.End
                          Element = var stream.Element })
                  Depth = depth loop.Depth
                  Body = go loop.Body }
        | While loop -> While { Condition = go loop.Condition; Depth = depth loop.Depth; Body = go loop.Body }
        // A chain is walked in a loop, so that a long one cannot overflow the stack.
        | Let _
        | Do _ ->
            let links, last = Chain.split e
            let mapped =
                links
                |> List.map (fun link ->
                    match link.Kind with
                    | Let(v, value, _) -> { link with Kind = Let(var v, go value, link) }
                    | Do(value, _) -> { link with Kind = Do(go value, link) }
                    | _ -> internalError "inlining after reference counting")
            (Chain.rebuild Chain.relink mapped (go last)).Kind
        | If(condition, ifTrue, ifFalse) -> If(atom condition, go ifTrue, go ifFalse)
        | Match(scrutinees, arms) ->
            Match(List.map var scrutinees, arms |> List.map (fun arm -> { Patterns = List.map pattern arm.Patterns; Body = go arm.Body }))
        | Dup _
        | Drop _
        | DropAfter _
        | Release _
        | Reuse _ -> internalError "inlining after reference counting"
    { e with Kind = kind }

/// The chain `value` starts, with `link` of what it ends in in place of what it ends in, and each link of the type of
/// `rest`: how a value that is a chain is flattened into the chain it is a value in, since a let's or a do's value is
/// never a let itself.
let private flattened (value: Expr) (rest: Expr) (link: Expr -> Expr) : Expr =
    let links, last = Chain.split value
    Chain.rebuild (fun (outer: Expr) next -> { Chain.relink outer next with Type = rest.Type }) links (link last)

/// `let var = value in rest`, with a value that is a chain flattened into the chain it starts.
let private bindValue (var: Var) (value: Expr) (rest: Expr) : Expr =
    flattened value rest (fun last -> Expr.make (Let(var, last, rest)) rest.Type)

/// `do value then rest`, with a value that is a chain flattened as `bindValue` flattens one.
let private doValue (value: Expr) (rest: Expr) : Expr =
    flattened value rest (fun last -> Expr.make (Do(last, rest)) rest.Type)

/// Whether `f` is a handler: a function whose body ends in a call through one of its parameters, as one that runs its
/// lambda parameter under a `use` does.
let private isHandler (f: Function) =
    match snd (Chain.split f.Body) with
    | { Kind = CallClosure(closure, _) } -> f.Params |> List.exists (fun p -> p.Id = closure.Id)
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
        // The closure each local holds, when a `let` gives it one over a lifted function, and how often each variable is
        // named, its binding included.
        let closures = Dictionary<int, string>()
        let named = Dictionary<int, int>()
        let count (v: Var) = named.[v.Id] <- (match named.TryGetValue v.Id with | true, n -> n + 1 | _ -> 1)
        for e in allExprs body do
            match e.Kind with
            | Let(v, { Kind = MakeClosure(name, _, _) }, _)
            | Let(v, { Kind = Atom(AStaticClosure(name, _, _)) }, _) -> closures.[v.Id] <- name
            | _ -> ()
            match e.Kind with
            | Let(v, _, _) -> count v
            | _ -> ()
            for atom in directAtoms e do
                match atom with
                | AVar v -> count v
                | _ -> ()
            directVars e |> List.iter count
        let mutable changed = false
        let mutable grown = size body

        // The function a call is inlined from, with what it is given, if it is to be.
        let site (call: Expr) : (Function * Atom list) option =
            // A lambda's first parameter is its environment, the closure read as a record of what it captures.
            let target, args, always, typed =
                match call.Kind with
                | Call(name, args) -> Some name, args, isHandler, (fun (callee: Function) -> List.forall2 (fun (param: Var) arg -> param.Type = atomType arg) callee.Params args)
                | CallClosure(closure, args) ->
                    match closures.TryGetValue closure.Id with
                    | true, name ->
                        let typed (callee: Function) =
                            match callee.Params with
                            | { Type = IrRecord _ } :: values -> List.forall2 (fun (param: Var) arg -> param.Type = atomType arg) values args
                            | _ -> false
                        Some name, AVar closure :: args, (fun _ -> named.[closure.Id] = 2), typed
                    | _ -> None, [], (fun _ -> false), (fun _ -> false)
                | _ -> None, [], (fun _ -> false), (fun _ -> false)
            match target |> Option.bind (fun name -> match functions.TryGetValue name with | true, callee -> Some callee | _ -> None) with
            | Some callee when
                not (cyclic.Contains callee.Name)
                && callee.Name <> f.Name
                && callee.Params.Length = args.Length
                && typed callee
                && callee.Result = call.Type
                && callee.Body.Type = callee.Result
                && grown < growthLimit
                ->
                let calleeSize = size callee.Body
                if always callee || calleeSize <= sizeLimit then
                    grown <- grown + calleeSize
                    Some(callee, args)
                else None
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

/// `functions` with calls inlined into each but the start function, `start`.
let inlineCalls (start: string option) (functions: Function list) : Function list =
    let byName = functions |> List.map (fun f -> f.Name, f) |> Map.ofList
    let edges = byName |> Map.map (fun _ f -> references f.Body)
    let cyclic = inCycles byName edges
    let current = Dictionary<string, Function>()
    for f in functions do
        current.[f.Name] <- f
    for name in calleesFirst byName edges do
        if Some name <> start then
            let f = current.[name]
            let inliner = Inliner(current, cyclic, f)
            let mutable body = f.Body
            let mutable round = 0
            let mutable changed = true
            while changed && round < rounds do
                let next, didChange = inliner.Round body
                body <- next
                changed <- didChange
                round <- round + 1
            current.[name] <- { f with Body = body }
    functions |> List.map (fun f -> current.[f.Name])
