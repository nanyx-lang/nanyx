/// What the two ways of generating WebAssembly text share (`CodeGen.fs` from the syntax tree, `CoreIrEmit.fs` from the
/// core IR): how tags are coded and which instruction an operator is. Names are in `Identifiers.fs`.
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

let wasmFloatConst (value: float) =
    if System.Double.IsNaN value then "nan"
    elif System.Double.IsPositiveInfinity value then "inf"
    elif System.Double.IsNegativeInfinity value then "-inf"
    else value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
