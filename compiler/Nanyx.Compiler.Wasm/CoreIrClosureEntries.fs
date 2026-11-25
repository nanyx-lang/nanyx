/// The two entries of a function a closure is over (see "Closures have two entries" in
/// `docs/src/content/deep-dives/memory-management.md`). The function itself, in the closure's table slot, borrows
/// its environment, so a caller that holds the closure past the call neither takes a reference for it nor gives one
/// up. Its owning entry sits in the slot after it: a tail call of a closure that dies there calls it, since the caller
/// could not drop the closure after the call (see `CoreIrCounting`). The owning entry is a copy of the function that owns
/// its environment, except for a function whose closures capture nothing: those are static data, which a drop leaves
/// alone, so the function is its own owning entry. An owning entry nothing can call is left out once the calls are
/// counted.
module internal Transpiler.Wasm.CoreIrClosureEntries

open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.CoreIrRewrite

/// The name of the owning entry of the function `name`, when it is a copy.
let owningName (name: string) = name + "__owning"

/// The functions some closure in `functions` is over: those a `MakeClosure` or a static closure names.
let targets (functions: Function list) : Set<string> =
    functions
    |> Seq.collect (fun func -> allExprs func.Body)
    |> Seq.collect (fun e ->
        let made = match e.Kind with MakeClosure(name, _, _) -> [ name ] | _ -> []
        let static' = directAtoms e |> List.choose (function AStaticClosure(name, _, _) -> Some name | _ -> None)
        made @ static')
    |> Set.ofSeq

/// How many context members a call of a function value of type `ty` passes after its arguments (see `IrFunction`).
let memberCount (ty: IrType) =
    match IrType.unfold ty with
    | IrFunction(_, _, members, _) -> members.Length
    | _ -> 0

/// How many context members a closure over each function in `functions` a closure is over is called with, by the
/// function's name: the last of its parameters are those members, which it borrows (see `CoreIrCounting`).
let memberCounts (functions: Function list) : Map<string, int> =
    functions
    |> Seq.collect (fun func -> allExprs func.Body)
    |> Seq.collect (fun e ->
        let made = match e.Kind with MakeClosure(name, _, _) -> [ name, memberCount e.Type ] | _ -> []
        let static' = directAtoms e |> List.choose (function AStaticClosure(name, _, ty) -> Some(name, memberCount ty) | _ -> None)
        made @ static')
    |> Map.ofSeq

/// Whether every closure over `func` captures nothing, and so is static data (see `closureOver`): its environment holds
/// the table slot alone.
let capturesNothing (func: Function) =
    match func.Params with
    | environment :: _ -> environment.Type = IrRecord [ "0", IrWord ]
    | [] -> true

/// `functions` with each of them a closure is over, one of `over`, followed by its owning entry, unless it is its own: a
/// function whose closures capture nothing, and that is called with no context members (`members`), which the function
/// borrows and its owning entry owns.
let withOwningEntries (over: Set<string>) (members: Map<string, int>) (functions: Function list) : Function list =
    functions
    |> List.collect (fun func ->
        let called = members |> Map.tryFind func.Name |> Option.defaultValue 0
        if over.Contains func.Name && (not (capturesNothing func) || called > 0) then [ func; { func with Name = owningName func.Name } ]
        else [ func ])

/// The owning entry of each function in `functions` a closure is over that has one among them: its copy, or the
/// function itself if its closures capture nothing.
let owningEntries (functions: Function list) : Map<string, string> =
    let names = functions |> List.map _.Name |> Set.ofList
    let over = targets functions
    functions
    |> List.choose (fun func ->
        if not (over.Contains func.Name) then None
        elif names.Contains (owningName func.Name) then Some (func.Name, owningName func.Name)
        elif capturesNothing func then Some (func.Name, func.Name)
        else None)
    |> Map.ofList

/// The parameters and result of a function as WebAssembly value types: what a `call_indirect` of it names.
let signatureOf (func: Function) =
    func.Params |> List.map (_.Type >> CoreIrLayout.valueType), CoreIrLayout.valueType func.Result

/// The types of the calls in `functions`, counted, through a closure that call its owning entry, as `signatureOf` gives
/// them. Only a function of one of these types can have its owning entry called through a closure.
let owningCallTypes (functions: Function list) =
    functions
    |> Seq.collect (fun func -> allExprs func.Body)
    |> Seq.choose (fun e ->
        match e.Kind with
        | CallClosure(closure, args, Owning) -> Some ((closure.Type :: List.map atomType args) |> List.map CoreIrLayout.valueType, CoreIrLayout.valueType e.Type)
        | _ -> None)
    |> Set.ofSeq

/// `functions`, counted, without the owning entries nothing in them or in `others` calls: neither a call by name nor a
/// call through a closure of the entry's type that calls the owning entry. `owning` are the names of the owning entries.
let withoutUncalled (owning: Set<string>) (others: Function list) (functions: Function list) : Function list =
    let named =
        functions @ others
        |> Seq.collect (fun func -> allExprs func.Body)
        |> Seq.choose (fun e -> match e.Kind with Call(name, _) when owning.Contains name -> Some name | _ -> None)
        |> Set.ofSeq
    let throughClosures = owningCallTypes (functions @ others)
    functions
    |> List.filter (fun func -> not (owning.Contains func.Name) || named.Contains func.Name || throughClosures.Contains (signatureOf func))
