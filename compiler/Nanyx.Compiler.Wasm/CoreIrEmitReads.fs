/// What the emitter reads off the IR before it writes an expression's code (see `CoreIrEmit`): whether the code is
/// marked with its place, the temporaries it computes as part of itself, and which variables a probe in a chain may
/// write.
module internal Transpiler.Wasm.CoreIrEmitReads

open System.Collections.Generic
open Transpiler.Wasm.CoreIr

/// Whether the code of an expression of `kind` gets a marker of where it was written: what calls or can trap, which a
/// stack names the line of.
let marked (kind: ExprKind) =
    match kind with
    | Prim _
    | Call _
    | MakeTag _
    | MakeRecord _
    | UpdateRecord _
    | MakeList _
    | CallRuntime _
    | CopyArrayInto _
    | CallImport _
    | MakeClosure _
    | CallClosure _
    | Interpolate _
    | Dbg _
    | Crash _
    | CrashWith _
    | Probe _ -> true
    | Atom _
    | Field _
    | Assign _
    | SetField _
    | FloatConstant _
    | ForEach _
    | While _
    | Let _
    | Do _
    | If _
    | Match _
    | Dup _
    | Drop _
    | DropAfter _
    | Release _
    | Reuse _
    | FreeToken _ -> false

/// The temporaries an expression uses directly, which it computes as part of its own code.
let rec tempsUsedBy (expr: Expr) : Var list =
    let ofAtoms atoms = atoms |> List.choose (function AVar v when v.Kind = Temp -> Some v | _ -> None)
    match expr.Kind with
    | Atom atom -> ofAtoms [ atom ]
    | Prim(_, atoms)
    | Call(_, atoms)
    | MakeList atoms
    | CallRuntime(_, atoms, _)
    | CallImport(_, atoms)
    | MakeClosure(_, _, atoms)
    | CallClosure(_, atoms, _) -> ofAtoms atoms
    | CopyArrayInto(target, at, source) -> ofAtoms [ target; at; source ]
    | MakeRecord fields -> ofAtoms (List.map snd fields)
    | UpdateRecord(record, updates) -> ofAtoms (record :: List.map snd updates)
    | MakeTag(_, atom)
    | Field(atom, _)
    | Assign(_, atom)
    | SetField(_, _, atom)
    | If(atom, _, _) -> ofAtoms [ atom ]
    | Dbg(_, text)
    | CrashWith text -> ofAtoms [ text ]
    | ForEach loop -> ofAtoms (ForLoop.sourceAtoms loop)
    | Dup(_, body)
    | Drop(_, body)
    | DropAfter(body, _)
    | Release(_, body)
    | Reuse(_, body)
    | FreeToken(_, body) -> tempsUsedBy body
    | Let _
    | Do _
    | Match _
    | While _
    | Interpolate _
    | FloatConstant _
    | Crash _
    | Probe _ -> []

/// Which variables a probe in a chain may write (see `CoreIrProbes`): those the rest of the chain after it reads but does
/// not bind itself. One it never reads again may have been given up already, so a probe leaves it out. Where each
/// variable is last read and last bound is worked out once for the chain, whose places are its links' indexes and, for
/// what it ends in, the number of links, so that a chain of n probes takes time in proportion to n, not n² (#2488).
type ChainReads(links: Expr list, last: Expr) =
    let lastRead = Dictionary<int, int>()
    let lastBound = Dictionary<int, int>()
    do
        let parts = (links |> List.map (fun link -> Chain.relink link (Expr.make (Atom AUnit) IrUnit))) @ [ last ]
        parts
        |> List.iteri (fun place part ->
            for e in CoreIrRewrite.allExprs part do
                for atom in CoreIrRewrite.directAtoms e do
                    match atom with
                    | AVar var -> lastRead.[var.Id] <- place
                    | _ -> ()
                for var in CoreIrRewrite.directVars e do
                    lastRead.[var.Id] <- place
                match e.Kind with
                | Let(var, _, _) -> lastBound.[var.Id] <- place
                | Match(_, arms) ->
                    for arm in arms do
                        for pattern in arm.Patterns do
                            for var in Pattern.binds pattern do
                                lastBound.[var.Id] <- place
                | ForEach loop ->
                    for stream in loop.Streams do
                        for var in [ stream.Element; stream.Cursor; stream.End ] do
                            lastBound.[var.Id] <- place
                | _ -> ())

    /// Whether a probe at `place` may write `var`: what follows it reads it, and does not bind it.
    member _.AliveAfter (place: int) (var: Var) =
        let after (places: Dictionary<int, int>) = match places.TryGetValue var.Id with | true, at -> at > place | _ -> false
        after lastRead && not (after lastBound)
