/// The functions a component's core module exports for the definitions the program marks `@component` (see
/// `NyxCompiler.ComponentInterface`): for each, a function that takes its arguments as the canonical ABI passes them,
/// calls the definition's function, and gives its result back as the canonical ABI returns it, and a post-return
/// function that frees what the result held once the host has copied it (see the Components page of the Deep dives).
module internal Transpiler.Wasm.ComponentExports

open NyxCompiler
open Transpiler.Wasm.WasmAllocator
open Transpiler.Wasm.WasmRuntime

/// The core types a value of `ty` is passed as: a string as its address and its length in bytes.
let private flat (ty: WitType) =
    match ty with
    | WitType.S64
    | WitType.U64 -> [ "i64" ]
    | WitType.F32 -> [ "f32" ]
    | WitType.F64 -> [ "f64" ]
    | WitType.String -> [ "i32"; "i32" ]
    | WitType.Bool
    | WitType.S8
    | WitType.S16
    | WitType.S32
    | WitType.U8
    | WitType.U16
    | WitType.U32 -> [ "i32" ]

/// The function an export of the definition `definition` calls, as the backend lowered it: its name, how many
/// parameters it takes, and whether it takes over its parameter at each index (see `CoreIrCounting`).
type ExportTarget =
    { Function: string
      Arity: int
      TakesOver: int -> bool }

/// The code that drops the string `$local` holds: gives up its reference, and frees it if that was the last.
let private dropString (local: string) =
    [ $"  local.get {local}"
      $"  i32.const {Header.withInfo}"
      "  i32.sub"
      $"  call ${Helpers.release}"
      "  if"
      $"    local.get {local}"
      $"    i32.const {Header.withInfo}"
      "    i32.sub"
      $"    call ${Helpers.free}"
      "  end" ]

/// The functions that export `func` under `name`, calling `target`, numbered `index` among the module's exports. A
/// string argument arrives in a block the host allocated through `cabi_realloc`, which the module owns: it is copied
/// into a string, and the block freed. A string result is returned in a block of its address and length, which
/// `cabi_post_` frees once the host has copied the string, giving up its reference to the string too.
let private exportFunctions (index: int) (name: string) (func: ComponentFunction) (target: ExportTarget) =
    let wrapper = $"$__nanyx_export_{index}"
    let parameters = func.Parameters |> List.mapi (fun position (_, ty) -> position, ty)
    let header =
        [ for position, ty in parameters do
              match ty with
              | WitType.String -> $"(param $p{position} i32) (param $p{position}_len i32)"
              | ty -> $"(param $p{position} {List.head (flat ty)})"
          match func.Result with
          | Some WitType.String -> "(result i32)"
          | Some ty -> $"(result {List.head (flat ty)})"
          | None -> ()
          for position, ty in parameters do
              if ty = WitType.String then $"(local $s{position} i32)"
          match func.Result with
          | Some WitType.String -> "(local $result i32) (local $area i32)"
          | Some ty -> $"(local $result {List.head (flat ty)})"
          | None -> () ]
        |> String.concat " "
    let lift =
        [ for position, ty in parameters do
              if ty = WitType.String then
                  $"  local.get $p{position}"
                  $"  local.get $p{position}_len"
                  $"  call ${Helpers.stringFromMemory}"
                  $"  local.set $s{position}"
                  // An empty string's block may be none at all.
                  $"  local.get $p{position}_len"
                  "  if"
                  $"    local.get $p{position}"
                  $"    call ${Helpers.free}"
                  "  end" ]
    let call =
        [ for position, ty in parameters do
              if ty = WitType.String then $"  local.get $s{position}" else $"  local.get $p{position}"
          // A function of no parameters takes `()`.
          if parameters.IsEmpty && target.Arity = 1 then "  i32.const 0"
          $"  call ${target.Function}" ]
    let released =
        [ for position, ty in parameters do
              if ty = WitType.String && not (target.TakesOver position) then yield! dropString $"$s{position}" ]
    let result =
        match func.Result with
        | None -> [ "  drop"; yield! released ]
        | Some WitType.String ->
            [ "  local.set $result"
              yield! released
              "  i32.const 8"
              $"  call ${Helpers.alloc}"
              "  local.tee $area"
              "  local.get $result"
              "  i32.store"
              "  local.get $area"
              "  local.get $result"
              $"  call ${Helpers.stringLength}"
              "  i32.store offset=4"
              "  local.get $area" ]
        | Some _ -> [ "  local.set $result"; yield! released; "  local.get $result" ]
    [ $"(func {wrapper} (export \"{name}\") {header}"
      yield! lift
      yield! call
      yield! result
      ")"
      match func.Result with
      | Some WitType.String ->
          $"(func {wrapper}_post (export \"cabi_post_{name}\") (param $area i32) (local $result i32)"
          "  local.get $area"
          "  i32.load"
          "  local.set $result"
          yield! dropString "$result"
          "  local.get $area"
          $"  call ${Helpers.free}"
          ")"
      | _ -> () ]
    |> String.concat "\n"

/// The functions that export each of `exports`, a function with the name the module exports it under, which calls the
/// function `target` gives for its definition.
let functions (exports: (string * ComponentFunction) list) (target: string -> ExportTarget) =
    exports
    |> List.mapi (fun index (name, func) -> exportFunctions index name func (target func.Export.Definition))
    |> String.concat "\n"

/// The runtime helpers the functions of `exports` call.
let helpers (exports: (string * ComponentFunction) list) =
    let strings =
        exports
        |> List.exists (fun (_, func) -> func.Result = Some WitType.String || func.Parameters |> List.exists (snd >> (=) WitType.String))
    if strings then [ Helpers.stringFromMemory; Helpers.stringLength; Helpers.release; Helpers.alloc; Helpers.free ] else []
