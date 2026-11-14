/// Leaving out of a module what its program can never run: the bindings of static closures that nothing reads, and the
/// functions its start does not reach.
///
/// The front end keeps every definition a module names, and the backend lowers each one that is not generic. A host's
/// `run` names every member of the contexts it `use`s, so without this each of them, and the WASI imports they need,
/// would be in every program, even one that calls none of them.
module internal Transpiler.Wasm.CoreIrReach

open System.Collections.Generic
open Transpiler.Wasm.CoreIr

/// The atoms an expression reads itself, not those of the expressions inside it.
let private atomsOf (e: Expr) : Atom list =
    match e.Kind with
    | Atom a
    | MakeTag(_, a)
    | Field(a, _)
    | Dbg a -> [ a ]
    | Prim(_, atoms)
    | Call(_, atoms)
    | MakeList atoms
    | CallRuntime(_, atoms, _)
    | MakeClosure(_, _, atoms)
    | CallClosure(_, atoms) -> atoms
    | MakeRecord fields -> fields |> List.map snd
    | UpdateRecord(record, updates) -> record :: (updates |> List.map snd)
    | Assign(_, value)
    | SetField(_, _, value) -> [ value ]
    | ForEach loop -> ForLoop.sourceAtoms loop
    | If(cond, _, _) -> [ cond ]
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

/// The functions `func` names: those it calls, and those it makes a closure over.
let private namedBy (func: Function) : string list =
    let names = ResizeArray<string>()
    let pending = Stack<Expr>()
    pending.Push func.Body
    while pending.Count > 0 do
        let e = pending.Pop()
        match e.Kind with
        | Call(name, _)
        | MakeClosure(name, _, _) -> names.Add name
        | _ -> ()
        for atom in atomsOf e do
            match atom with
            | AStaticClosure(name, _, _) -> names.Add name
            | _ -> ()
        for child in Expr.children e do
            pending.Push child
    List.ofSeq names

/// `e` with `f` applied to each expression directly inside it that is not what follows a link of a chain.
let private mapNested (f: Expr -> Expr) (e: Expr) : Expr =
    let kind =
        match e.Kind with
        | If(cond, ifTrue, ifFalse) -> If(cond, f ifTrue, f ifFalse)
        | Match(scrutinees, arms) ->
            Match(scrutinees, arms |> List.map (fun arm -> { Patterns = arm.Patterns |> List.map (Pattern.mapGuards f); Body = f arm.Body }))
        | ForEach loop -> ForEach { loop with Body = f loop.Body }
        | While loop -> While { loop with Condition = f loop.Condition; Body = f loop.Body }
        | DropAfter(body, vars) -> DropAfter(f body, vars)
        | Reuse(token, body) -> Reuse(token, f body)
        | kind -> kind
    { e with Kind = kind }

/// `e` without the bindings of the variables `unread`.
let rec private withoutBindings (unread: Set<int>) (e: Expr) : Expr =
    let links, last = Chain.split e
    let last = mapNested (withoutBindings unread) last
    links
    |> Chain.rebuild
        (fun link following ->
            match link.Kind with
            | Let(var, _, _) when unread.Contains var.Id -> following
            | Let(var, value, _) -> { link with Kind = Let(var, withoutBindings unread value, following) }
            | Do(value, _) -> { link with Kind = Do(withoutBindings unread value, following) }
            | _ -> Chain.relink link following)
        <| last

/// `func` without the bindings of static closures that nothing reads: a `use` binds each of its members, and a call
/// specialised on a member it knows (see `CoreIrLowerKnown`) never reads the binding, which would otherwise keep the
/// function the closure is over.
let withoutUnreadClosures (func: Function) : Function =
    let bound = HashSet<int>()
    let pending = Stack<Expr>()
    pending.Push func.Body
    while pending.Count > 0 do
        let e = pending.Pop()
        match e.Kind with
        | Let(var, { Kind = Atom(AStaticClosure _) }, _) -> bound.Add var.Id |> ignore
        | _ -> ()
        for child in Expr.children e do
            pending.Push child
    if bound.Count = 0 then
        func
    else
        let ids = Set.ofSeq bound
        let unread = Set.difference ids (CoreIrLowerKnown.usedOf ids func.Body)
        if unread.IsEmpty then func else { func with Body = withoutBindings unread func.Body }

/// The functions of `functions` that `start` reaches, in their order, through the functions each calls or makes a
/// closure over. Without a start, a module runs nothing of its own, and every function is kept.
let reachedFrom (start: Function option) (functions: Function list) : Function list =
    match start with
    | None -> functions
    | Some start ->
        let byName = functions |> List.map (fun func -> func.Name, func) |> dict
        let reached = HashSet<string>()
        let pending = Stack<string>(namedBy start)
        while pending.Count > 0 do
            let name = pending.Pop()
            if reached.Add name then
                match byName.TryGetValue name with
                | true, func -> namedBy func |> List.iter pending.Push
                | _ -> ()
        functions |> List.filter (fun func -> reached.Contains func.Name)
