/// The functions a component's core module exports for the definitions the program marks `@component` (see
/// `NyxCompiler.ComponentInterface`): for each, a function that takes its arguments as the canonical ABI passes them,
/// calls the definition's function, and gives its result back as the canonical ABI returns it, and a post-return
/// function that frees what the result points to once the host has copied it (see the Components page of the Deep
/// dives). An export that can wait, an `async func`, is lifted with the callback ABI instead, and gives its result with
/// `task.return` (`ComponentCallback`). Values are lifted and lowered by `ComponentValues`.
module internal Transpiler.Wasm.ComponentExports

open NyxCompiler
open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.ComponentValues
open Transpiler.Wasm.WasmRuntime
open Transpiler.Wasm.WatText

/// The most core values an export's parameters are passed as before the canonical ABI passes them in memory instead.
let private maxFlatParameters = 16

/// The function an export of a definition calls, as the backend lowered it: its name, the Nanyx types of its
/// parameters and its result, and whether it takes over its parameter at each index (see `CoreIrCounting`).
type ExportTarget =
    { Function: string
      Parameters: IrType list
      Result: IrType
      TakesOver: int -> bool
      /// The indirect type of the function's resume entry, when it is a state machine, which only an `async func` can
      /// be: its export is lifted with the callback ABI (see `ComponentCallback`).
      Resumes: string option }

/// How the functions write tags and records, and note the runtime helpers they call.
type ExportContext =
    { TagCode: string -> int
      RecordLayout: string list -> int
      UseHelper: string -> unit }


/// The functions that export `func` under `name`, calling `target`, numbered `index` among the module's exports.
///
/// Its arguments are lifted to Nanyx values: from the core values they are passed as, or, when those come to more than
/// `maxFlatParameters`, from a block the host allocated through `cabi_realloc`, which is freed. The blocks a string or
/// a list points to are the module's too, and are freed once they are copied. Each argument the function doesn't take
/// over is dropped after the call. A result passed as at most one core value is returned as it; any other is written
/// to a new block, whose address is returned, and `cabi_post_` and the export's name frees it, with the blocks it
/// points to, once the host has copied it. The Nanyx result is dropped once it has been lowered.
/// The parameters of the function that exports `func`, calling `target`, as `generator` declares them: the header's
/// `param`s, the locals its arguments are lifted into, the code that lifts them, and the Nanyx types of its arguments.
let private lifted (generator: Generator) (func: ComponentFunction) (target: ExportTarget) =
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
    String.concat " " header, arguments, lift, parameterIrs

/// The call of `target` with `arguments`, a function of no parameters taking `()`.
let private callOf (func: ComponentFunction) (target: ExportTarget) (arguments: string list) =
    [ for argument in arguments do
          $"local.get {argument}"
      if func.Parameters.IsEmpty && target.Parameters.Length = 1 then "i32.const 0"
      $"call ${target.Function}" ]

let private exportFunctions (context: ExportContext) (index: int) (name: string) (func: ComponentFunction) (target: ExportTarget) =
    let generator = Generator(context.TagCode, context.RecordLayout, context.UseHelper)
    let wrapper = $"$__nanyx_export_{index}"
    let parameters, arguments, lift, parameterIrs = lifted generator func target
    // Only an `async func` can wait, and its export is `asyncEntry`'s.
    if target.Resumes.IsSome then internalError $"the export '{name}' can wait, but isn't an async func"
    let call = callOf func target arguments
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
              yield! indent frees
              "  local.get $area"
              $"  {free}"
              ")" ]
        | _ -> []
    let declarations = generator.Declarations |> String.concat " "
    [ $"(func {wrapper} (export \"{name}\") {parameters} {resultType} {declarations}"
      yield! indent body
      ")"
      yield! post ]
    |> String.concat "\n"

/// The most core values `task.return` takes a result as before it takes it in memory instead.
let private maxFlatResults = 16

/// The export of `func`, an `async func`, under `name`, calling `target`, numbered `index` among the module's exports,
/// as an entry lifted with the callback ABI (see `ComponentCallback`), and the import of its `task.return`.
///
/// Its arguments are lifted as `exportFunctions` lifts them, and kept in globals of their own while it waits, so that
/// those the function doesn't take over can be dropped once it has finished. Its result is given with `task.return`:
/// as the core values it is passed as, or, past `maxFlatResults`, as the address of a block it is written to; either
/// way it is lowered into a new block first, which is freed with the blocks it points to once `task.return` has
/// copied it. The Nanyx result is dropped then too.
let private asyncEntry (context: ExportContext) (index: int) (name: string) (func: ComponentFunction) (target: ExportTarget) =
    let generator = Generator(context.TagCode, context.RecordLayout, context.UseHelper)
    let wrapper = $"$__nanyx_export_{index}"
    let parameters, arguments, lift, parameterIrs = lifted generator func target
    let interfaceName, functionName =
        match name.Split('#') with
        | [| interfaceName; functionName |] -> interfaceName, functionName
        | _ -> internalError $"an export named '{name}', with no interface"
    let taskReturn = $"{wrapper}_return"
    let returnParameters =
        match func.Result with
        | None -> []
        | Some ty when (flat ty).Length <= maxFlatResults -> flat ty
        | Some _ -> [ "i32" ]
    let import =
        let parameters = returnParameters |> List.map (fun core -> $" (param {core})") |> String.concat ""
        $"""  (import "[export]{interfaceName}" "[task-return]{functionName}" (func {taskReturn}{parameters}))"""
    let released =
        List.zip parameterIrs arguments
        |> List.indexed
        |> List.filter (fun (position, _) -> not (target.TakesOver position))
        |> List.map (fun (position, (ir, argument)) -> $"{wrapper}_argument_{position}", ir, argument)
    let finisher = Generator(context.TagCode, context.RecordLayout, context.UseHelper)
    let given =
        match func.Result with
        | None -> [ $"call {taskReturn}" ]
        | Some ty when (flat ty).Length <= 1 -> [ yield! finisher.LowerFlat ty target.Result "$value"; $"call {taskReturn}" ]
        | Some ty ->
            let area = finisher.Local "i32"
            [ yield! finisher.Alloc (alignment ty) [ $"i32.const {size ty}" ]
              $"local.set {area}"
              yield! finisher.LowerMemory ty target.Result "$value" area 0
              if (flat ty).Length <= maxFlatResults then yield! finisher.FlatFromMemory ty area 0 else $"local.get {area}"
              $"call {taskReturn}"
              yield! finisher.FreeMemory ty area 0
              $"local.get {area}"
              finisher.Call Helpers.free ]
    let finish =
        [ yield! given
          yield! finisher.Drop target.Result "$value"
          for kept, ir, _ in released do
              let argument = finisher.Local(CoreIrLayout.valueType ir)
              $"global.get {kept}"
              $"local.set {argument}"
              yield! finisher.Drop ir argument ]
    let entry: ComponentCallback.Entry =
        { Globals =
            released
            |> List.map (fun (kept, ir, _) ->
                let core = CoreIrLayout.valueType ir
                $"(global {kept} (mut {core}) ({core}.const 0))")
          Header = $"(func {wrapper} (export \"[async-lift]{name}\") {parameters} (result i32)"
          Locals = generator.Declarations
          Callback = $"[callback][async-lift]{name}"
          Before = [ yield! lift; for kept, _, argument in released do $"local.get {argument}"; $"global.set {kept}" ]
          Call = callOf func target arguments
          ValueType = CoreIrLayout.valueType target.Result
          CanStop = target.Resumes.IsSome
          Resume = target.Resumes |> Option.defaultValue ""
          Finish = finish
          FinishLocals = finisher.Declarations }
    entry, import

/// The exports of `exports`, each a function with the name the module exports it under, which calls the function
/// `target` gives for its definition: the functions of those lifted synchronously, the entries of those that are
/// `async func`s, and the imports of their `task.return`s.
let functions (context: ExportContext) (exports: (string * ComponentFunction) list) (target: string -> ExportTarget) =
    let indexed = exports |> List.indexed
    let synchronous =
        indexed
        |> List.filter (fun (_, (_, func)) -> not func.Async)
        |> List.map (fun (index, (name, func)) -> exportFunctions context index name func (target func.Export.Definition))
        |> String.concat "\n"
    let entries, imports =
        indexed
        |> List.filter (fun (_, (_, func)) -> func.Async)
        |> List.map (fun (index, (name, func)) -> asyncEntry context index name func (target func.Export.Definition))
        |> List.unzip
    synchronous, entries, imports
