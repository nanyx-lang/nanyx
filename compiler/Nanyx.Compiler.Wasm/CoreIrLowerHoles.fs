/// Lowering a block a statement at a time. The lowering passes each value to the code that goes after it (see
/// `FunctionLowering.Complex`), so lowering the rest of a block from inside a statement's lowering recurses once per
/// statement. Instead, each statement is lowered to its lets and dos around a hole, the rest of the block is lowered
/// after it, and the rest is then put in the hole.
module internal Transpiler.Wasm.CoreIrLowerHoles

open Transpiler.Wasm.CoreIr

/// A hole for the rest of a block to go in. Each is a value of its own, told apart from any other by reference.
let hole () : Expr = { Kind = Atom AUnit; Type = IrUnit }

/// `lowered`, a statement's lets and dos, with `rest` in place of `hole` at the end of them. Each let and do has the
/// type of what follows it, so each is given `rest`'s.
let rec plug (hole: Expr) (rest: Expr) (lowered: Expr) : Expr =
    if LanguagePrimitives.PhysicalEquality lowered hole then rest
    else
        match lowered.Kind with
        | Let(var, value, body) ->
            let body = plug hole rest body
            { Kind = Let(var, value, body); Type = body.Type }
        | Do(value, body) ->
            let body = plug hole rest body
            { Kind = Do(value, body); Type = body.Type }
        | _ -> internalError "a block's statement was lowered with the rest of the block somewhere other than after its lets"

/// A block's `statements` lowered by `statement`, which lowers the first of the statements it is given around a hole,
/// with the scope the rest is lowered in and that rest, or else lowers all of them: each statement in turn, and then
/// each one's rest put in its hole, from the last.
let lowerBlock (statement: 'scope -> 'statement list -> Expr * (Expr * 'scope * 'statement list) option) (scope: 'scope) (statements: 'statement list) : Expr =
    let rec lower scope statements (lowered: (Expr * Expr) list) =
        match statement scope statements with
        | value, None -> lowered |> List.fold (fun rest (lowered, hole) -> plug hole rest lowered) value
        | first, Some (hole, scope, rest) -> lower scope rest ((first, hole) :: lowered)
    lower scope statements []
