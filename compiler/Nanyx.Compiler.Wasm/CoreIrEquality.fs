/// The equality functions of a module: for each type `==` compares values of that is not a number, a bool or unit, one
/// that compares two values of it by their structure. Records and tuples compare field by field, lists element by
/// element, tags by their code and then their payload, strings by their bytes, and floats as IEEE 754 numbers. Every
/// type is concrete, so each function knows how to compare each of its parts. Functions have no equality, and never
/// reach here. An equality function only reads its operands: it borrows them, as every runtime helper does.
module Transpiler.Wasm.CoreIrEquality

open System.Collections.Generic
open Transpiler.Wasm.Identifiers
open Transpiler.Wasm.WasmRuntime
open Transpiler.Wasm.Wat
open Transpiler.Wasm.CoreIr

/// The name of the equality function of `ty`.
let equalsName (ty: IrType) = "__nanyx_equals_" + sanitizeIdentifier (IrType.mangle ty)

/// Whether comparing values of `ty` needs an equality function of its own, rather than an instruction or a helper.
let private needsFunction (ty: IrType) =
    match ty with
    | IrRecord _
    | IrList _
    | IrTags _ -> true
    | IrInt
    | IrBool
    | IrUnit
    | IrFloat
    | IrString
    | IrFunction _
    | IrError -> false

/// The types of the operands of the calls to the runtime helper `helper` in `functions`, and the parts of those types
/// (`parts` gives a type's own), each once, in the order they are first needed, where `needsFunction` holds: the types
/// a module needs a function of its own of, each written by its operands' structure.
let collectFor (helper: string) (needsFunction: IrType -> bool) (parts: IrType -> IrType list) (functions: Function list) : IrType list =
    let types = ResizeArray<IrType>()
    let seen = HashSet<IrType>()

    let rec add (ty: IrType) =
        if needsFunction ty && seen.Add ty then
            types.Add ty
            parts ty |> List.iter add

    let rec expr (e: Expr) =
        match e.Kind with
        | CallRuntime(name, [ first; _ ], _) when name = helper -> add (atomType first)
        | Let(_, value, body) ->
            expr value
            expr body
        | Do(first, rest) ->
            expr first
            expr rest
        | If(_, ifTrue, ifFalse) ->
            expr ifTrue
            expr ifFalse
        | Match(_, arms) ->
            for arm in arms do
                arm.Patterns |> List.iter pattern
                expr arm.Body
        | ForEach loop -> expr loop.Body
        | Dup(_, body)
        | Drop(_, body)
        | DropAfter(body, _)
        | Release(_, body)
        | Reuse(_, body) -> expr body
        | Atom _
        | Prim _
        | Call _
        | MakeTag _
        | MakeRecord _
        | Field _
        | UpdateRecord _
        | MakeList _
        | CallRuntime _
        | MakeClosure _
        | CallClosure _
        | Interpolate _
        | Dbg _
        | BoxFloat _
        | Panic _ -> ()

    and pattern (p: Pattern) =
        match p with
        | PGuard(_, guard) -> expr guard
        | PTag(_, payload) -> payload |> Option.iter pattern
        | PFields fields -> fields |> List.iter (snd >> pattern)
        | PAll parts -> parts |> List.iter pattern
        | PList(elements, rest) ->
            elements |> List.iter pattern
            rest |> Option.iter pattern
        | PBind _
        | PWildcard
        | PInt _
        | PBool _
        | PString _ -> ()

    for func in functions do
        expr func.Body
    List.ofSeq types

/// The types whose equality functions the functions of a module call, in the order they are first needed: each type
/// `==` compares at, and the parts of those types that need a function of their own too.
let collect (functions: Function list) : IrType list =
    let parts (ty: IrType) =
        match ty with
        | IrList element -> [ element ]
        | IrRecord fields -> fields |> List.map snd
        | IrTags tags -> tags |> List.choose snd
        | _ -> []
    collectFor Helpers.valueEquals needsFunction parts functions

/// The code that compares the two values `loadA` and `loadB` leave on the stack, as values of type `ty`, and leaves 1
/// when they are equal.
let private compare (ty: IrType) (loadA: string list) (loadB: string list) : string list =
    match ty with
    | IrInt
    | IrBool
    | IrUnit -> loadA @ loadB @ [ "i32.eq" ]
    | IrFloat -> loadA @ [ "f64.load" ] @ loadB @ [ "f64.load"; "f64.eq" ]
    | IrString -> loadA @ loadB @ [ $"call ${Helpers.stringEquals}" ]
    | IrRecord _
    | IrList _
    | IrTags _ -> loadA @ loadB @ [ $"call ${equalsName ty}" ]
    | IrFunction _
    | IrError -> failwith $"Internal error: an equality of {IrType.show ty}, which has none"

/// Returns 0 from the function unless the code before it left 1.
let private orReturnFalse = [ "i32.eqz"; "if"; "  i32.const 0"; "  return"; "end" ]

let private indent (lines: string list) = lines |> List.map (fun line -> "  " + line)

/// The body of the equality function of `ty`, which compares `$a` and `$b` and leaves 1 when they are equal.
let private equalsBody (tagId: string -> int option) (ty: IrType) : string list =
    match ty with
    | IrRecord fields ->
        [ yield!
              fields
              |> List.mapi (fun slot (_, fieldTy) ->
                  let field var = [ $"local.get ${var}"; $"i32.load offset={slot * 4}" ]
                  compare fieldTy (field "a") (field "b") @ orReturnFalse)
              |> List.concat
          "i32.const 1" ]
    | IrList element ->
        // Cell by cell along both lists, so that a long list takes no stack. The empty list is 0.
        [ "block $done"
          "  loop $next"
          "    local.get $a"
          "    i32.eqz"
          "    local.get $b"
          "    i32.eqz"
          "    i32.or"
          "    br_if $done"
          yield! compare element [ "local.get $a"; "i32.load" ] [ "local.get $b"; "i32.load" ] @ orReturnFalse |> indent |> indent
          "    local.get $a"
          "    i32.load offset=4"
          "    local.set $a"
          "    local.get $b"
          "    i32.load offset=4"
          "    local.set $b"
          "    br $next"
          "  end"
          "end"
          // One list ran out: they are equal when both did.
          "local.get $a"
          "local.get $b"
          "i32.eq" ]
    | IrTags tags ->
        // A tag without a payload is its code, which is odd; one with a payload is a pointer to a cell that holds its
        // code and then its payload. When either is a code, they are equal only when both are the same code.
        let payloads = tags |> List.choose (fun (name, payload) -> payload |> Option.map (fun payloadTy -> name, payloadTy))
        let comparePayload payloadTy = compare payloadTy [ "local.get $a"; "i32.load offset=4" ] [ "local.get $b"; "i32.load offset=4" ]
        let payloadsEqual =
            match payloads |> List.map snd |> List.distinct with
            | [] -> [ "i32.const 1" ]
            | [ single ] -> comparePayload single
            | _ ->
                // Each tag's payload by its own type, picked by the code both cells hold.
                [ yield!
                      payloads
                      |> List.choose (fun (name, payloadTy) -> tagId name |> Option.map (fun id -> id, payloadTy))
                      |> List.collect (fun (id, payloadTy) ->
                          [ "local.get $a"
                            "i32.load"
                            $"i32.const {tagCode id}"
                            "i32.eq"
                            "if"
                            yield! comparePayload payloadTy |> List.map (fun line -> "  " + line)
                            "  return"
                            "end" ])
                  "i32.const 1" ]
        [ "local.get $a"
          "local.get $b"
          "i32.or"
          "i32.const 1"
          "i32.and"
          "if"
          "  local.get $a"
          "  local.get $b"
          "  i32.eq"
          "  return"
          "end"
          "local.get $a"
          "i32.load"
          "local.get $b"
          "i32.load"
          "i32.ne"
          "if"
          "  i32.const 0"
          "  return"
          "end"
          yield! payloadsEqual ]
    | _ -> failwith $"Internal error: an equality function for {IrType.show ty}, which needs none"

/// The runtime helpers the equality functions of `types` call.
let helpersUsed (types: IrType list) : string list =
    let comparesStrings =
        types
        |> List.exists (function
            | IrRecord fields -> fields |> List.exists (snd >> (=) IrString)
            | IrList element -> element = IrString
            | IrTags tags -> tags |> List.exists (snd >> (=) (Some IrString))
            | _ -> false)
    [ if comparesStrings then Helpers.stringEquals ]

/// The equality functions of `types`, as WebAssembly text. `tagId` gives the number of each tag the module numbers; a
/// tag it does not is never made, so no value of it is compared.
let render (tagId: string -> int option) (types: IrType list) : string list =
    types
    |> List.map (fun ty ->
        [ $"(func ${equalsName ty} (param $a i32) (param $b i32) (result i32)"
          $"  ;; {IrType.show ty}"
          yield! equalsBody tagId ty |> indent
          ")" ]
        |> indent
        |> String.concat "\n")
