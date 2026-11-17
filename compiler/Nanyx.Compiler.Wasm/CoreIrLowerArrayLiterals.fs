/// Lowers the array a collection literal's items are passed to its type's `fromArray` as (`__arrayOf`, see
/// `CollectionSyntax`) in one block, built in place: its length is the number of items and the length of each spread,
/// each item is written into it, and each spread's elements are copied in, so the items are never a list.
module internal Transpiler.Wasm.CoreIrLowerArrayLiterals

open NyxCompiler
open Parser.Program
open Transpiler.Wasm.WasmRuntime
open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.CoreIrLowerTypes

/// A part of a literal's items, in the order written: an item, or a spread of an array or of a list.
type Part =
    | Item of Expression
    | SpreadArray of Expression
    | SpreadList of Expression

/// The parts of `items`, the list of a literal's items as the parser writes it: runs of items joined with `++` to
/// spreads (see `CollectionSyntax.listOf`). `isList` says whether an expression is a list, as each `++` the literal is
/// joined with is. Any other list is spread whole.
let rec parts (isList: Expression -> bool) (items: Expression) : Part list =
    match items.Kind with
    | ListExpr elements -> elements |> List.map Item
    | BinaryOp("++", None, left, right) when isList items -> parts isList left @ parts isList right
    | FunctionCall(name, _, [ operand ]) when name = CollectionSyntax.spreadArray -> [ SpreadArray operand ]
    | FunctionCall(name, _, [ operand ]) when name = CollectionSyntax.spreadList -> [ SpreadList operand ]
    | _ -> [ SpreadList items ]

let private letIn (var: Var) (value: Expr) (body: Expr) = { Kind = Let(var, value, body); Type = body.Type }

let private doThen (value: Expr) (body: Expr) = { Kind = Do(value, body); Type = body.Type }

/// The array of type `ty` made of `parts`, passed on to `k`. `atoms` lowers the parts' expressions to atoms that can wait
/// while code that stores runs, in the order written; `coerce` adapts each item to the element type, as a list literal's
/// are; `newLocal` names a local.
let arrayOf
    (atoms: Expression list -> (Atom list -> Expr) -> Expr)
    (coerce: Atom list -> IrType list -> (Atom list -> Expr) -> Expr)
    (newLocal: string -> IrType -> Var)
    (ty: IrType)
    (parts: Part list)
    (k: Expr -> Expr)
    : Expr =
    let element =
        match ty with
        | IrArray element -> element
        | other -> internalError $"a collection literal's array of type '{IrType.show other}'"
    let expression = function Item e | SpreadArray e | SpreadList e -> e
    atoms (parts |> List.map expression) (fun lowered ->
        let items = List.zip parts lowered |> List.choose (function Item _, atom -> Some atom | _ -> None)
        coerce items (items |> List.map (fun _ -> element)) (fun coerced ->
            // Each part with its atom, the items adapted to the element type.
            let rec pair (parts: (Part * Atom) list) (coerced: Atom list) =
                match parts, coerced with
                | (Item e, _) :: rest, atom :: others -> (Item e, atom) :: pair rest others
                | (Item _, _) :: _, [] -> internalError "fewer items adapted than written"
                | part :: rest, _ -> part :: pair rest coerced
                | [], _ -> []
            let paired = pair (List.zip parts lowered) coerced
            // Each spread's array, and its length, held in locals, since each is read twice: a list is made an array
            // first, one block for its elements.
            let rec spreads (remaining: (Part * Atom) list) (held: (Part * Atom * (Var * Var) option) list) =
                match remaining with
                | [] -> build (List.rev held)
                | (Item _ as part, atom) :: rest -> spreads rest ((part, atom, None) :: held)
                | (part, atom) :: rest ->
                    let source = newLocal "__spread" ty
                    let length = newLocal "__spread_length" IrInt
                    let value =
                        match part with
                        | SpreadList _ -> { Kind = CallRuntime(Intrinsics.arrayOfList, [ atom ], true); Type = ty }
                        | _ -> { Kind = Atom atom; Type = ty }
                    let body = spreads rest ((part, atom, Some (source, length)) :: held)
                    letIn source value (letIn length { Kind = CallRuntime(Intrinsics.arrayLength, [ AVar source ], true); Type = IrInt } body)
            // The array at its length, each part written at its place in turn.
            and build (held: (Part * Atom * (Var * Var) option) list) =
                let sum (left: Atom) (right: Atom) (k: Atom -> Expr) =
                    match left, right with
                    | AInt a, AInt b -> k (AInt(a + b))
                    | _ ->
                        let total = newLocal "__at" IrInt
                        letIn total { Kind = Prim(Binary "+", [ left; right ]); Type = IrInt } (k (AVar total))
                let rec length (remaining: (Part * Atom * (Var * Var) option) list) (acc: Atom) (k: Atom -> Expr) =
                    match remaining with
                    | [] -> k acc
                    | (_, _, None) :: rest -> sum acc (AInt 1L) (fun acc -> length rest acc k)
                    | (_, _, Some (_, spreadLength)) :: rest -> sum acc (AVar spreadLength) (fun acc -> length rest acc k)
                length held (AInt 0L) (fun total ->
                    let array = newLocal "__literal" ty
                    let rec write (remaining: (Part * Atom * (Var * Var) option) list) (at: Atom) =
                        match remaining with
                        | [] -> k { Kind = Atom(AVar array); Type = ty }
                        | (_, item, None) :: rest ->
                            let written = { Kind = CallRuntime(Intrinsics.arrayWrite, [ AVar array; at; item ], false); Type = IrUnit }
                            sum at (AInt 1L) (fun next -> doThen written (write rest next))
                        | (_, _, Some (source, spreadLength)) :: rest ->
                            let copied = { Kind = CallRuntime(Helpers.arrayCopyInto, [ AVar array; at; AVar source ], false); Type = IrUnit }
                            sum at (AVar spreadLength) (fun next -> doThen copied (write rest next))
                    letIn array { Kind = CallRuntime(Intrinsics.arrayNew, [ total ], true); Type = ty } (write held (AInt 0L)))
            spreads paired []))
