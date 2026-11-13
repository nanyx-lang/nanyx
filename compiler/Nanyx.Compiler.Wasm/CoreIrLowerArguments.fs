/// Lowers a record passed as the one argument of a function of several parameters: each parameter takes a field.
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
            { Kind = Let(temp, { Kind = Field(record, slot); Type = temp.Type }, body); Type = body.Type }
    load (List.mapi slotOf parameters) []
