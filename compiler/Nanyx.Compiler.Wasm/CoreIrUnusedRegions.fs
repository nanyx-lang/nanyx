/// The regions of `memory` blocks that nothing can put an object on, left out (#2418). Each block's lambda runs between
/// `region_enter` and `region_exit` (`memory` in the prelude), and a region is read only when a box is made on it
/// (`CoreIrRegions.tracked`), when the block ends, and by promotion. A block whose cells are all locals, and which calls
/// nothing that can make a box, needs none: without this, such a block allocated and freed a region at every run.
///
/// It is decided for each function once inlining is done, since a block is usually inlined where `memory` is called: a
/// function that makes no box, calls no function value, and calls only functions that can't make one either, needs no
/// region of its own, so its `region_enter`s and `region_exit`s go. A block that keeps its region inside one of these
/// has the region of the block around the function's caller as its outer one, which is where promotion moves what it
/// gives back.
module internal Transpiler.Wasm.CoreIrUnusedRegions

open System.Collections.Generic
open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.CoreIrRewrite

/// Whether `body` can put an object on the region innermost where it runs by itself: it makes a box, or calls a function
/// value, which could.
let private makesBoxes (body: Expr) =
    allExprs body
    |> Seq.exists (fun e ->
        match e.Kind with
        | CallRuntime(name, _, _) -> name = WasmRegions.Names.regionTrack
        | CallClosure _ -> true
        | _ -> false)

/// The functions `body` calls by name.
let private callees (body: Expr) =
    allExprs body |> Seq.choose (fun e -> match e.Kind with Call(name, _) -> Some name | _ -> None) |> Set.ofSeq

/// `e` without its calls of `region_enter` and `region_exit`, each now `()`. A chain is walked in a loop, so that a long
/// one cannot overflow the stack.
let rec private withoutRegions (e: Expr) : Expr =
    match e.Kind with
    | CallRuntime(name, _, _) when name = WasmRegions.Names.regionEnter || name = WasmRegions.Names.regionExit ->
        { e with Kind = Atom AUnit; Type = IrUnit }
    | Let _
    | Do _ ->
        let links, last = Chain.split e
        let links =
            links
            |> List.map (fun link ->
                match link.Kind with
                | Let(var, value, following) -> { link with Kind = Let(var, withoutRegions value, following) }
                | Do(value, following) -> { link with Kind = Do(withoutRegions value, following) }
                | _ -> link)
        Chain.rebuild Chain.relink links (withoutRegions last)
    | _ -> Expr.map withoutRegions e

/// `functions`, a whole module's once inlining is done, each without the regions of its blocks when nothing it runs can
/// make a box. A function that calls one not among `functions` keeps its own.
let withoutUnused (functions: Function list) : Function list =
    let known = functions |> List.map _.Name |> HashSet
    let boxing = HashSet<string>(functions |> List.filter (fun f -> makesBoxes f.Body) |> List.map _.Name)
    let calls = functions |> List.map (fun f -> f.Name, callees f.Body)
    let mutable changed = true
    while changed do
        changed <- false
        for name, called in calls do
            if not (boxing.Contains name) && called |> Set.exists (fun callee -> boxing.Contains callee || not (known.Contains callee)) then
                boxing.Add name |> ignore
                changed <- true
    functions |> List.map (fun f -> if boxing.Contains f.Name then f else { f with Body = withoutRegions f.Body })
