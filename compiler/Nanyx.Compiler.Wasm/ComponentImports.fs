/// The functions a component's core module imports for the definitions the program marks `@import` (see
/// `NyxCompiler.ComponentInterface.imports`). A call of one is a call of a foreign function (`CallImport`) of the
/// interface and the function, which the module defines itself, as its `ComponentInterface.externOf`: it lowers its
/// arguments as the canonical ABI passes them, calls the function the module imports, and lifts its result (see the
/// Components page of the Deep dives). Values are lifted and lowered by `ComponentValues`.
module internal Transpiler.Wasm.ComponentImports

open NyxCompiler
open Transpiler.Wasm.CoreIr
open Transpiler.Wasm.ComponentValues
open Transpiler.Wasm.ComponentExports
open Transpiler.Wasm.WasmRuntime
open Transpiler.Wasm.WatText

/// The most core values a call passes as arguments before the canonical ABI passes them in memory instead.
let private maxFlatParameters = 16

/// The function the module imports for the import numbered `index`.
let private importedName (index: int) = $"$__nanyx_wit_import_{index}"

/// The declaration of the function the module imports for `func`, numbered `index`: its arguments as their core values,
/// or the address of a block of them when they come to more than `maxFlatParameters`, then, for a result that comes to
/// more than one core value, the address of a block to write it to; and a result of one core value as it.
let private declaration (index: int) (func: ComponentFunction) =
    let flatParameters = func.Parameters |> List.collect (snd >> flat)
    let parameters = if flatParameters.Length > maxFlatParameters then [ "i32" ] else flatParameters
    let resultFlat = func.Result |> Option.map flat |> Option.defaultValue []
    let parameters = if resultFlat.Length > 1 then parameters @ [ "i32" ] else parameters
    let parameterText = if parameters.IsEmpty then "" else $""" (param {String.concat " " parameters})"""
    let resultText = match resultFlat with [ core ] -> $" (result {core})" | _ -> ""
    $"""  (import "{func.Export.Module}" "{func.Export.Name}" (func {importedName index}{parameterText}{resultText}))"""

/// The declarations of the functions the module imports for `imports`, in order.
let declarations (imports: (NyxCompiler.Extern * ComponentFunction) list) =
    imports |> List.mapi (fun index (_, func) -> declaration index func) |> String.concat "\n"


/// The function a call of the import `declared`, of `func`, numbered `index`, calls, taking and giving Nanyx values of
/// the types its WIT types are held as (`irTypeOf`). Its arguments, which it borrows, are written to a new block, their strings and lists to new blocks of their
/// own, and passed as their core values or by the block's address; all of it is freed after the call. A result in
/// memory is written to a block the module allocates, and its strings and lists to blocks the callee allocated
/// through `cabi_realloc`, all of which the result's lifting frees.
let private wrapper (context: ExportContext) (index: int) (declared: NyxCompiler.Extern) (func: ComponentFunction) =
    let parameterIrs = func.Parameters |> List.map (snd >> irTypeOf)
    let resultIr = func.Result |> Option.map irTypeOf |> Option.defaultValue IrUnit
    let generator = Generator(context.TagCode, context.RecordLayout, context.UseHelper)
    let types = func.Parameters |> List.map snd
    let arguments = parameterIrs |> List.mapi (fun position ir -> $"$a{position}", ir)
    let flatParameters = types |> List.collect flat
    let resultFlat = func.Result |> Option.map flat |> Option.defaultValue []
    let argumentBlock = generator.Local "i32"
    let lowered =
        if types.IsEmpty then []
        else
            let tuple = WitType.Tuple types
            [ yield! generator.Alloc (alignment tuple) [ $"i32.const {size tuple}" ]
              $"local.set {argumentBlock}"
              for (ty, (argument, ir)), offset in List.zip (List.zip types arguments) (offsets types) do
                  yield! generator.LowerMemory ty ir argument argumentBlock offset
              if flatParameters.Length > maxFlatParameters then
                  $"local.get {argumentBlock}"
              else
                  for ty, offset in List.zip types (offsets types) do
                      yield! generator.FlatFromMemory ty argumentBlock offset ]
    let resultBlock = generator.Local "i32"
    let flatResult = resultFlat |> List.tryExactlyOne |> Option.map generator.Local
    let call =
        [ match func.Result with
          | Some ty when resultFlat.Length > 1 ->
              yield! generator.Alloc (alignment ty) [ $"i32.const {size ty}" ]
              $"local.tee {resultBlock}"
          | _ -> ()
          $"call {importedName index}"
          match flatResult with
          | Some local -> $"local.set {local}"
          | None -> () ]
    let freed =
        if types.IsEmpty then []
        else
            [ for ty, offset in List.zip types (offsets types) do
                  yield! generator.FreeMemory ty argumentBlock offset
              $"local.get {argumentBlock}"
              generator.Call Helpers.free ]
    let lifted =
        match func.Result, flatResult with
        | None, _ -> []
        | Some ty, Some local -> generator.LiftFlat ty resultIr [ local, List.head resultFlat ]
        | Some ty, None ->
            [ yield! generator.LiftMemory ty resultIr resultBlock 0
              $"local.get {resultBlock}"
              generator.Call Helpers.free ]
    let body = lowered @ call @ freed @ lifted
    let parameters = arguments |> List.map (fun (argument, ir) -> $"(param {argument} {CoreIrLayout.valueType ir})") |> String.concat " "
    let result = if declared.Result.IsSome then $"(result {CoreIrLayout.valueType resultIr})" else ""
    let declarations = generator.Declarations |> String.concat " "
    [ $"(func ${CoreIrEmitImports.importName declared} {parameters} {result} {declarations}"
      yield! indent body
      ")" ]
    |> String.concat "\n"

/// The functions the calls of `imports` call.
let functions (context: ExportContext) (imports: (NyxCompiler.Extern * ComponentFunction) list) =
    imports
    |> List.mapi (fun index (declared, func) -> wrapper context index declared func)
    |> String.concat "\n"
