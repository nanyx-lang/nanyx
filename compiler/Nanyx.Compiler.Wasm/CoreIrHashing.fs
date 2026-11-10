/// The hash functions of a module: for each type the compiler's own `Hash` hashes (see `WellKnownContexts.hashing`),
/// one that hashes a value of it by its structure, so that two values `==` finds equal by their structure have the same
/// hash. Ints, bools and unit are their own hash; a float is hashed by its bits, with `-0.0` as `0.0`; a string by its
/// bytes (FNV-1a); records and tuples field by field, lists element by element, and tags by their code and then their
/// payload, each part's hash folded into the whole's. Functions have no hash, and never reach here. A hash function
/// only reads its operand: it borrows it, as every runtime helper does.
module Transpiler.Wasm.CoreIrHashing

open Transpiler.Wasm.Identifiers
open Transpiler.Wasm.WasmRuntime
open Transpiler.Wasm.Wat
open Transpiler.Wasm.CoreIr

/// The name of the hash function of `ty`.
let hashName (ty: IrType) = "__nanyx_hash_" + sanitizeIdentifier (IrType.mangle ty)

/// The parts of a value of `ty` whose hashes its hash is made of.
let private parts (ty: IrType) : IrType list =
    match ty with
    | IrRecord fields -> fields |> List.map snd
    | IrList element -> [ element ]
    | IrTags tags -> tags |> List.choose snd
    | _ -> []

/// The types whose hash functions the functions of a module call, in the order they are first needed: each type the
/// hash intrinsic hashes, and the parts of those types.
let collect (functions: Function list) : IrType list =
    CoreIrEquality.collectFor Helpers.valueHash (fun _ -> true) parts functions

/// Folds the hash the code `part` leaves into `$h`, as `$h * 31 + part`.
let private fold (part: string list) = [ "local.get $h"; "i32.const 31"; "i32.mul"; yield! part; "i32.add"; "local.set $h" ]

/// The code that hashes the value `load` leaves on the stack, as a value of type `ty`.
let private hashOf (ty: IrType) (load: string list) = load @ [ $"call ${hashName ty}" ]

let private indent (lines: string list) = lines |> List.map (fun line -> "  " + line)

/// The locals the hash function of `ty` needs, and its body, which leaves the hash of `$a`. `tagId` gives the number of
/// each tag the module numbers.
let private hashBody (tagId: string -> int option) (ty: IrType) : string list * string list =
    match ty with
    | IrInt
    | IrBool
    | IrUnit -> [], [ "local.get $a" ]
    | IrFloat ->
        // `0.0 == -0.0`, so both hash as 0. NaN equals nothing, so its hash does not matter.
        [ "(local $x f64)"; "(local $bits i64)" ],
        [ "local.get $a"
          "f64.load"
          "local.tee $x"
          "f64.const 0"
          "f64.eq"
          "if"
          "  i32.const 0"
          "  return"
          "end"
          "local.get $x"
          "i64.reinterpret_f64"
          "local.tee $bits"
          "local.get $bits"
          "i64.const 32"
          "i64.shr_u"
          "i64.xor"
          "i32.wrap_i64" ]
    | IrString ->
        // FNV-1a over the bytes.
        [ "(local $len i32)"; "(local $i i32)"; "(local $h i32)" ],
        [ "local.get $a"
          $"call ${Helpers.stringLength}"
          "local.set $len"
          "i32.const -2128831035"
          "local.set $h"
          "block $done"
          "  loop $next"
          "    local.get $i"
          "    local.get $len"
          "    i32.ge_s"
          "    br_if $done"
          "    local.get $h"
          "    local.get $a"
          "    local.get $i"
          "    i32.add"
          "    i32.load8_u"
          "    i32.xor"
          "    i32.const 16777619"
          "    i32.mul"
          "    local.set $h"
          "    local.get $i"
          "    i32.const 1"
          "    i32.add"
          "    local.set $i"
          "    br $next"
          "  end"
          "end"
          "local.get $h" ]
    | IrRecord fields ->
        [ "(local $h i32)" ],
        [ "i32.const 17"
          "local.set $h"
          yield! fields |> List.mapi (fun slot (_, fieldTy) -> fold (hashOf fieldTy [ "local.get $a"; $"i32.load offset={slot * 4}" ])) |> List.concat
          "local.get $h" ]
    | IrList element ->
        // Cell by cell, so that a long list takes no stack. The empty list is 0.
        [ "(local $h i32)" ],
        [ "i32.const 1"
          "local.set $h"
          "block $done"
          "  loop $next"
          "    local.get $a"
          "    i32.eqz"
          "    br_if $done"
          yield! fold (hashOf element [ "local.get $a"; "i32.load" ]) |> indent |> indent
          "    local.get $a"
          "    i32.load offset=4"
          "    local.set $a"
          "    br $next"
          "  end"
          "end"
          "local.get $h" ]
    | IrTags tags ->
        // A tag without a payload is its code, which is odd, and that is its hash. One with a payload is a pointer to a
        // cell that holds its code and then its payload, whose hash is folded into the code.
        let payload payloadTy = fold (hashOf payloadTy [ "local.get $a"; "i32.load offset=4" ])
        let payloads = tags |> List.choose (fun (name, payloadTy) -> payloadTy |> Option.map (fun payloadTy -> name, payloadTy))
        let hashPayload =
            match payloads |> List.map snd |> List.distinct with
            | [] -> []
            | [ single ] -> payload single
            | _ ->
                // Each tag's payload by its own type, picked by its code.
                payloads
                |> List.choose (fun (name, payloadTy) -> tagId name |> Option.map (fun id -> id, payloadTy))
                |> List.collect (fun (id, payloadTy) ->
                    [ "local.get $a"
                      "i32.load"
                      $"i32.const {tagCode id}"
                      "i32.eq"
                      "if"
                      yield! payload payloadTy |> indent
                      "end" ])
        [ "(local $h i32)" ],
        [ "local.get $a"
          "i32.const 1"
          "i32.and"
          "if"
          "  local.get $a"
          "  return"
          "end"
          "local.get $a"
          "i32.load"
          "local.set $h"
          yield! hashPayload
          "local.get $h" ]
    | IrFunction _
    | IrError -> failwith $"Internal error: a hash function for {IrType.show ty}, which has no hash"

/// The runtime helpers the hash functions of `types` call.
let helpersUsed (types: IrType list) : string list =
    [ if types |> List.contains IrString then Helpers.stringLength ]

/// The hash functions of `types`, as WebAssembly text. `tagId` gives the number of each tag the module numbers; a tag
/// it does not is never made, so no value of it is hashed.
let render (tagId: string -> int option) (types: IrType list) : string list =
    types
    |> List.map (fun ty ->
        let locals, body = hashBody tagId ty
        [ $"(func ${hashName ty} (param $a i32) (result i32)"
          $"  ;; {IrType.show ty}"
          yield! indent locals
          yield! indent body
          ")" ]
        |> indent
        |> String.concat "\n")
