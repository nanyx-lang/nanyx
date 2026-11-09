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
