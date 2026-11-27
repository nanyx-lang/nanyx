/// Writing WebAssembly text that the compiler emits as lines of code: a body indented under its parent, and the lines
/// joined into one piece of text.
module internal Transpiler.Wasm.WatText

/// `code` indented by two spaces, as a body is under the line that opens it.
let indent (code: string list) = code |> List.map (fun line -> "  " + line)

/// `code` indented by two spaces, as a body is under the line that opens it, joined into one piece of text.
let lines (code: string list) = indent code |> String.concat "\n"
