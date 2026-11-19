/// Top-level values built once (see "Top-level values" in `docs/src/content/compiler-internals/memory-management.md`). A
/// top-level value is lowered to a function of no parameters, one per type it is used at when it is generic. Each such
/// function that computes its value is emitted under `buildName` of its name, and its own name is a getter: the first
/// read builds the value into a global, and every read hands out one more reference to it. The start function drops
/// what was built once the program has run.
module internal Transpiler.Wasm.CoreIrCachedValues

open Transpiler.Wasm.CoreIr

/// Whether `func` is a value built once: it was lowered from a top-level value (`isValue` of its definition), takes
/// nothing (no context members, say), and computes its value. A constant, such as a literal or a closure that captures
/// nothing, costs nothing to compute, so it is left as it is, to be inlined.
let isCached (isValue: string -> bool) (func: Function) =
    func.Params.IsEmpty
    && isValue func.Source
    && (match func.Body.Kind with
        | Atom _ -> false
        | _ -> true)

/// The name the function that computes a cached value is emitted under.
let buildName (name: string) = name + "__build"

let private builtGlobal (name: string) = name + "__built"

let private valueGlobal (name: string) = name + "__value"

/// The globals of a cached value: whether it is built yet, and the value.
let globals (func: Function) : string list =
    let valueType = CoreIrLayout.valueType func.Result
    [ $"  (global ${builtGlobal func.Name} (mut i32) (i32.const 0))"
      $"  (global ${valueGlobal func.Name} (mut {valueType}) ({valueType}.const 0))" ]

/// The getter of a cached value, exported under `exportName` if given: it builds the value on its first read, and gives
/// each read a reference of its own, as the function it stands for did by computing a new value. Its body starts with
/// the marker of the definition's place, where `shownFile` names files for markers, as the function that builds it does.
let getter (shownFile: (string -> string) option) (func: Function) (exportName: string option) : string =
    let defined =
        match func.Place, shownFile with
        | Some place, Some shownFile -> [ "    " + CoreIrEmit.definitionMarker shownFile place ]
        | _ -> []
    let dup =
        if CoreIrDrop.isHeapType func.Result then [ $"    call ${CoreIrDrop.dupName func.Result}" ] else []
    let export =
        match exportName with
        | Some exported -> [ $"""  (export "{exported}" (func ${func.Name}))""" ]
        | None -> []
    [ $"  (func ${func.Name} (result {CoreIrLayout.valueType func.Result})"
      yield! defined
      $"    global.get ${builtGlobal func.Name}"
      "    i32.eqz"
      "    if"
      $"      call ${buildName func.Name}"
      $"      global.set ${valueGlobal func.Name}"
      "      i32.const 1"
      $"      global.set ${builtGlobal func.Name}"
      "    end"
      $"    global.get ${valueGlobal func.Name}"
      yield! dup
      "  )"
      yield! export ]
    |> String.concat "\n"

/// The code at the end of the start function that drops each cached value that was built, and marks it unbuilt, so a
/// later read (a call to an exported function) builds it again.
let teardown (cached: Function list) : string list =
    cached
    |> List.map (fun func ->
        [ $"global.get ${builtGlobal func.Name}"
          "if"
          if CoreIrDrop.isHeapType func.Result then
              $"  global.get ${valueGlobal func.Name}"
              $"  call ${CoreIrDrop.dropName func.Result}"
          "  i32.const 0"
          $"  global.set ${builtGlobal func.Name}"
          "end" ]
        |> String.concat "\n    ")
