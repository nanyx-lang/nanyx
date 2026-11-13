/// The WebAssembly text that `CodeGen.fs` and `CoreIrEmit.fs` share: how tags are coded and which instruction an operator
/// is. Names are in `Identifiers.fs`.
module internal Transpiler.Wasm.Wat

/// The i32 instruction for an operator on integers and booleans.
let wasmBinaryInstruction op =
    match op with
    | "+" -> Some "i32.add"
    | "-" -> Some "i32.sub"
    | "*" -> Some "i32.mul"
    | "/" -> Some "i32.div_s"
    | "%" -> Some "i32.rem_s"
    | "==" -> Some "i32.eq"
    | "!=" -> Some "i32.ne"
    | "<" -> Some "i32.lt_s"
    | "<=" -> Some "i32.le_s"
    | ">" -> Some "i32.gt_s"
    | ">=" -> Some "i32.ge_s"
    | "and" -> Some "i32.and"
    | "or" -> Some "i32.or"
    | _ -> None

/// The i64 instruction for an operator on ints that has one: not `+`, `-` and `*`, which call the helpers that stop the
/// program when the result does not fit (see `WasmRuntime.Helpers.intAdd`).
let wasmIntInstruction op =
    match op with
    | "/" -> Some "i64.div_s"
    | "%" -> Some "i64.rem_s"
    | "==" -> Some "i64.eq"
    | "!=" -> Some "i64.ne"
    | "<" -> Some "i64.lt_s"
    | "<=" -> Some "i64.le_s"
    | ">" -> Some "i64.gt_s"
    | ">=" -> Some "i64.ge_s"
    | _ -> None

/// The name of the function type `call_indirect` calls a function of `parameters` returning `result` (WebAssembly
/// value types) through. A function of `i32`s alone keeps the name of its arity.
let indirectTypeName (parameters: string list) (result: string) =
    if parameters @ [ result ] |> List.forall ((=) "i32") then $"ctx_fn_{parameters.Length}"
    else $"""ctx_fn_{parameters.Length}_{String.concat "_" parameters}_to_{result}"""

/// A tag's code: odd, so a tag without a payload (which is its code) can't be mistaken for a pointer to the cell
/// of a tag with one.
let tagCode (id: int) = (id <<< 1) ||| 1

/// The f64 instruction for an operator on floats; comparisons leave an i32, arithmetic an f64 to box.
let wasmFloatInstruction op =
    match op with
    | "+" -> Some "f64.add"
    | "-" -> Some "f64.sub"
    | "*" -> Some "f64.mul"
    | "/" -> Some "f64.div"
    | "==" -> Some "f64.eq"
    | "!=" -> Some "f64.ne"
    | "<" -> Some "f64.lt"
    | "<=" -> Some "f64.le"
    | ">" -> Some "f64.gt"
    | ">=" -> Some "f64.ge"
    | _ -> None

let private memoryInstruction =
    System.Text.RegularExpressions.Regex(@"\b(?:i32|i64|f32|f64)\.(?:load|store)|\bmemory\.(?:size|grow|copy|fill|init)\b")

/// Whether WebAssembly text has an instruction that reads, writes or grows linear memory, so the module that holds it
/// must declare one.
let usesMemory (text: string) = memoryInstruction.IsMatch text

let wasmFloatConst (value: float) =
    if System.Double.IsNaN value then "nan"
    elif System.Double.IsPositiveInfinity value then "inf"
    elif System.Double.IsNegativeInfinity value then "-inf"
    else value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
