/// Lowers a record literal with a spread, `(...a, f = v, ...b)` (see `SpreadSyntax`): its parts are evaluated in the
/// order written, and each field of the record it makes is read from the last part that has it.
module internal Transpiler.Wasm.CoreIrLowerSpread

open Parser.Program
open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.CoreIrLowerTypes

let private letIn (var: Var) (value: Expr) (body: Expr) = { Kind = Let(var, value, body); Type = body.Type }

/// The record of type `ty` made from `parts`, passed on to `k`. Each part's value is lowered with `lower` and held in a
/// local of `newLocal`, as is each field read from a spread record: a spread record has several fields read from it,
/// and a field written out may be replaced by a later part.
let record (newLocal: IrType -> Var) (lower: Expression -> (Expr -> Expr) -> Expr) (ty: IrType) (parts: SpreadPart list) (k: Expr -> Expr) : Expr =
    let fields =
        match ty with
        | IrRecord fields -> fields
        | other -> cannot $"a record spread of type {IrType.show other}"
    // Where each field comes from: the last part that has it, of those held in `held`, the last first.
    let source (held: (SpreadPart * Var) list) (name: string) =
        held
        |> List.pick (fun (part, var) ->
            match part, var.Type with
            | SpreadField(field, _), _ when field = name -> Some (Choice1Of2 var)
            | SpreadRecord _, IrRecord spread ->
                spread |> List.tryFindIndex (fst >> (=) name) |> Option.map (fun slot -> Choice2Of2 (var, slot, snd spread[slot]))
            | _ -> None)
    let made (held: (SpreadPart * Var) list) =
        let reads, fieldAtoms =
            fields
            |> List.map (fun (name, _) ->
                match source held name with
                | Choice1Of2 var -> None, (name, AVar var)
                | Choice2Of2 (var, slot, fieldTy) ->
                    let read = newLocal fieldTy
                    Some (read, { Kind = Field(AVar var, slot); Type = fieldTy }), (name, AVar read))
            |> List.unzip
        List.foldBack (fun (var, value) body -> letIn var value body) (List.choose id reads) (k { Kind = MakeRecord fieldAtoms; Type = ty })
    let rec go held remaining =
        match remaining with
        | [] -> made held
        | part :: rest ->
            let value = match part with SpreadRecord record -> record | SpreadField(_, value) -> value
            lower value (fun lowered ->
                let var = newLocal lowered.Type
                letIn var lowered (go ((part, var) :: held) rest))
    go [] parts
