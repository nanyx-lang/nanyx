/// Lowers a record passed as the one argument of a function of several parameters, where each parameter takes a field,
/// and several arguments passed to a function that takes one value, which takes them as one tuple.
module internal Transpiler.Wasm.CoreIrLowerArguments

open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.CoreIrLowerTypes

/// The fields of `record`, of type `recordTy`, as the arguments of `callee`'s `parameters`, passed on to `call`. Each
/// parameter takes the field of its name, or else, when the record has a field for each parameter (a tuple), the
/// field in its place. A record with more fields (one passed for an open record type) gives only those the parameters
/// name. Each field read is held in a temporary of `newTemp`.
let fieldsAsArguments (newTemp: IrType -> Var) (callee: string) (parameters: string list) (recordTy: IrType) (record: Atom) (call: Atom list -> Expr) : Expr =
    let fields = match recordTy with IrRecord fields -> fields | _ -> []
    let slotOf index parameter =
        match fields |> List.tryFindIndex (fst >> (=) parameter) with
        | Some slot -> slot
        | None when fields.Length = parameters.Length -> index
        | None -> cannot $"a call to '{callee}' with one argument for its {parameters.Length} parameters"
    let rec load (slots: int list) (acc: Atom list) =
        match slots with
        | [] -> call (List.rev acc)
        | slot :: rest ->
            let temp = newTemp (snd fields.[slot])
            let body = load rest (AVar temp :: acc)
            Expr.make (Let(temp, Expr.make (Field(record, slot)) temp.Type, body)) body.Type
    load (List.mapi slotOf parameters) []

/// Whether a function of input type `input` takes one value that is neither a tuple nor unit, a context member's boxed
/// `a` say (see `CoreIrLowerBoxes`). A call passes it a tuple whole, `ret((x, y))`, and makes one of its arguments when
/// it has several, `ret(x, y)` (see `asTuple`), where a function whose input is a tuple takes the tuple's elements.
let takesOneValue (input: IrType) =
    match input with
    | IrUnit -> false
    | _ -> (IrType.tupleElements input).IsNone

/// `atoms`, a call's arguments, as one tuple bound by `bind`, passed on to `call`.
let asTuple (bind: Expr -> (Atom -> Expr) -> Expr) (atoms: Atom list) (call: Atom -> Expr) : Expr =
    let fields = atoms |> List.mapi (fun index atom -> string index, atom)
    bind (Expr.make (MakeRecord fields) (IrRecord(fields |> List.map (fun (name, atom) -> name, atomType atom)))) call
