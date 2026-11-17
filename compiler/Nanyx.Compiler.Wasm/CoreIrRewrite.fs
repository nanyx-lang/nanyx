/// Walking and rewriting the core IR before reference counting, as the inliner (`CoreIrInline`) and the simplifier
/// (`CoreIrSimplify`) do: the expressions and variables it holds, renaming its variables, and placing a value that is a
/// chain into the chain around it.
module internal Transpiler.Wasm.CoreIrRewrite

open System.Collections.Generic
open Transpiler.Wasm.CoreIr

/// Every expression in `e`, each once, in no particular order.
let allExprs (e: Expr) : Expr seq =
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
let size (e: Expr) = Seq.length (allExprs e)

/// The atoms `e` reads directly.
let directAtoms (e: Expr) : Atom list =
    match e.Kind with
    | Atom atom
    | MakeTag(_, atom)
    | Field(atom, _)
    | Assign(_, atom)
    | SetField(_, _, atom)
    | If(atom, _, _) -> [ atom ]
    | Dbg(location, text) -> [ location; text ]
    | Prim(_, atoms)
    | Call(_, atoms)
    | MakeList atoms
    | CallRuntime(_, atoms, _)
    | CallImport(_, atoms)
    | MakeClosure(_, _, atoms)
    | CallClosure(_, atoms, _) -> atoms
    | CopyArrayInto(target, at, source) -> [ target; at; source ]
    | MakeRecord fields -> List.map snd fields
    | UpdateRecord(record, updates) -> record :: List.map snd updates
    | ForEach loop -> ForLoop.sourceAtoms loop
    | Interpolate _
    | FloatConstant _
    | Crash _
    | While _
    | Let _
    | Do _
    | Match _
    | Dup _
    | Drop _
    | DropAfter _
    | Release _
    | Reuse _
    | FreeToken _ -> []

/// The variables `e` reads directly, other than through its atoms.
let directVars (e: Expr) : Var list =
    match e.Kind with
    | CallClosure(var, _, _)
    | Assign(var, _)
    | SetField(var, _, _) -> [ var ]
    | Match(scrutinees, _) -> scrutinees
    | Interpolate parts -> parts |> List.choose (function Hole(var, _) -> Some var | Text _ -> None)
    | _ -> []

/// `e` with each variable, bound or read, replaced as `var` says, and each loop's depth as `depth` says.
let rec mapVars (var: Var -> Var) (depth: int -> int) (e: Expr) : Expr =
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
        | PFloat _
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
        | CopyArrayInto(target, at, source) -> CopyArrayInto(atom target, atom at, atom source)
        | CallImport(declared, args) -> CallImport(declared, atoms args)
        | MakeClosure(name, arity, captured) -> MakeClosure(name, arity, atoms captured)
        | CallClosure(closure, args, entry) -> CallClosure(var closure, atoms args, entry)
        | Interpolate parts -> Interpolate(parts |> List.map (function Hole(v, holeType) -> Hole(var v, holeType) | text -> text))
        | Dbg(location, value) -> Dbg(atom location, atom value)
        | FloatConstant _
        | Crash _ -> e.Kind
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
                    | _ -> internalError "rewriting the core IR after reference counting")
            (Chain.rebuild Chain.relink mapped (go last)).Kind
        | If(condition, ifTrue, ifFalse) -> If(atom condition, go ifTrue, go ifFalse)
        | Match(scrutinees, arms) ->
            Match(List.map var scrutinees, arms |> List.map (fun arm -> { Patterns = List.map pattern arm.Patterns; Body = go arm.Body }))
        | Dup _
        | Drop _
        | DropAfter _
        | Release _
        | Reuse _
        | FreeToken _ -> internalError "rewriting the core IR after reference counting"
    { e with Kind = kind }

/// `e` with `f` applied to each expression directly inside it that is not what follows a link of a chain.
let mapNested (f: Expr -> Expr) (e: Expr) : Expr =
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

/// The chain `value` starts, with `link` of what it ends in in place of what it ends in, and each link of the type of
/// `rest`: how a value that is a chain is flattened into the chain it is a value in, since a let's or a do's value is
/// never a let itself.
let private flattened (value: Expr) (rest: Expr) (link: Expr -> Expr) : Expr =
    let links, last = Chain.split value
    Chain.rebuild (fun (outer: Expr) next -> { Chain.relink outer next with Type = rest.Type }) links (link last)

/// `let var = value in rest`, with a value that is a chain flattened into the chain it starts.
let bindValue (var: Var) (value: Expr) (rest: Expr) : Expr =
    flattened value rest (fun last -> Expr.make (Let(var, last, rest)) rest.Type)

/// `do value then rest`, with a value that is a chain flattened as `bindValue` flattens one.
let doValue (value: Expr) (rest: Expr) : Expr =
    flattened value rest (fun last -> Expr.make (Do(last, rest)) rest.Type)
