/// The functions a component's core module exports for the definitions the program marks `@component` (see
/// `NyxCompiler.ComponentInterface`): for each, a function that takes its arguments as the canonical ABI passes them,
/// calls the definition's function, and gives its result back as the canonical ABI returns it, and a post-return
/// function that frees what the result points to once the host has copied it (see the Components page of the Deep
/// dives). Values are lifted and lowered by `ComponentValues`.
module internal Transpiler.Wasm.ComponentExports

open NyxCompiler
open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.ComponentValues
open Transpiler.Wasm.WasmRuntime

/// The most core values an export's parameters are passed as before the canonical ABI passes them in memory instead.
let private maxFlatParameters = 16

/// The function an export of a definition calls, as the backend lowered it: its name, the Nanyx types of its
/// parameters and its result, and whether it takes over its parameter at each index (see `CoreIrCounting`).
type ExportTarget =
    { Function: string
      Parameters: IrType list
      Result: IrType
      TakesOver: int -> bool
      /// The indirect type of the function's resume entry, when it is a state machine, which the export runs to the
      /// end (see `TaskLowering.blockOn`).
      Resumes: string option }

/// How the functions write tags and records, and note the runtime helpers they call.
type ExportContext =
    { TagCode: string -> int
      RecordLayout: string list -> int
      UseHelper: string -> unit }

let private indented (lines: string list) = lines |> List.map (fun line -> "  " + line)

/// The functions that export `func` under `name`, calling `target`, numbered `index` among the module's exports.
///
/// Its arguments are lifted to Nanyx values: from the core values they are passed as, or, when those come to more than
/// `maxFlatParameters`, from a block the host allocated through `cabi_realloc`, which is freed. The blocks a string or
/// a list points to are the module's too, and are freed once they are copied. Each argument the function doesn't take
/// over is dropped after the call. A result passed as at most one core value is returned as it; any other is written
/// to a new block, whose address is returned, and `cabi_post_` and the export's name frees it, with the blocks it
/// points to, once the host has copied it. The Nanyx result is dropped once it has been lowered.
let private exportFunctions (context: ExportContext) (index: int) (name: string) (func: ComponentFunction) (target: ExportTarget) =
    let generator = Generator(context.TagCode, context.RecordLayout, context.UseHelper)
    let wrapper = $"$__nanyx_export_{index}"
    let types = func.Parameters |> List.map snd
    // A function of no parameters takes `()`.
    let parameterIrs = if types.IsEmpty then [] else target.Parameters
    let flatParameters = types |> List.collect flat
    let inMemory = flatParameters.Length > maxFlatParameters
    let header =
        if inMemory then [ "(param $args i32)" ]
        else flatParameters |> List.mapi (fun position core -> $"(param $p{position} {core})")
    let arguments = parameterIrs |> List.map (fun ir -> generator.Local(CoreIrLayout.valueType ir))
    let lift =
        if inMemory then
            [ for (ty, ir), argument, offset in List.zip3 (List.zip types parameterIrs) arguments (offsets types) do
                  yield! generator.LiftMemory ty ir "$args" offset
                  $"local.set {argument}"
              "local.get $args"
              generator.Call Helpers.free ]
        else
            let starts = types |> List.scan (fun start ty -> start + (flat ty).Length) 0
            [ for ((ty, ir), argument), start in List.zip (List.zip (List.zip types parameterIrs) arguments) (List.take types.Length starts) do
                  let values = flat ty |> List.mapi (fun offset core -> $"$p{start + offset}", core)
                  yield! generator.LiftFlat ty ir values
                  $"local.set {argument}" ]
    let call =
        [ for argument in arguments do
              $"local.get {argument}"
          if types.IsEmpty && target.Parameters.Length = 1 then "i32.const 0"
          $"call ${target.Function}"
          match target.Resumes with
          | Some resumeType -> TaskLowering.blockOn (generator.Local(CoreIrLayout.valueType target.Result)) (generator.Local "i32") resumeType
          | None -> () ]
    let released =
        List.zip parameterIrs arguments
        |> List.indexed
        |> List.collect (fun (position, (ir, argument)) -> if target.TakesOver position then [] else generator.Drop ir argument)
    let result = generator.Local(CoreIrLayout.valueType target.Result)
    let resultType, returned =
        match func.Result with
        | None -> "", [ "drop"; yield! released ]
        | Some ty when (flat ty).Length <= 1 ->
            let core = List.head (flat ty)
            let lowered = generator.Local core
            $"(result {core})",
            [ $"local.set {result}"
              yield! generator.LowerFlat ty target.Result result
              $"local.set {lowered}"
              yield! generator.Drop target.Result result
              yield! released
              $"local.get {lowered}" ]
        | Some ty ->
            let area = generator.Local "i32"
            "(result i32)",
            [ $"local.set {result}"
              yield! generator.Alloc (alignment ty) [ $"i32.const {size ty}" ]
              $"local.set {area}"
              yield! generator.LowerMemory ty target.Result result area 0
              yield! generator.Drop target.Result result
              yield! released
              $"local.get {area}" ]
    let body = lift @ call @ returned
    let post =
        match func.Result with
        | Some ty when (flat ty).Length > 1 ->
            let freeing = Generator(context.TagCode, context.RecordLayout, context.UseHelper)
            let frees = freeing.FreeMemory ty "$area" 0
            let free = freeing.Call Helpers.free
            let declarations = freeing.Declarations |> String.concat " "
            [ $"(func {wrapper}_post (export \"cabi_post_{name}\") (param $area i32) {declarations}"
              yield! indented frees
              "  local.get $area"
              $"  {free}"
              ")" ]
        | _ -> []
    let parameters = String.concat " " header
    let declarations = generator.Declarations |> String.concat " "
    [ $"(func {wrapper} (export \"{name}\") {parameters} {resultType} {declarations}"
      yield! indented body
      ")"
      yield! post ]
    |> String.concat "\n"

/// The functions that export each of `exports`, a function with the name the module exports it under, which calls the
/// function `target` gives for its definition.
let functions (context: ExportContext) (exports: (string * ComponentFunction) list) (target: string -> ExportTarget) =
    exports
    |> List.mapi (fun index (name, func) -> exportFunctions context index name func (target func.Export.Definition))
    |> String.concat "\n"
