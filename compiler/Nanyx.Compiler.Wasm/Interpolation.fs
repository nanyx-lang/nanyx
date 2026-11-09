/// The code for an interpolated string, which both ways of generating WebAssembly text emit (`CodeGen.fs` from the
/// syntax tree, `CoreIrEmit.fs` from the core IR).
///
/// Each `{...}` hole is evaluated exactly once, up front, into a hole local (see `holeLocalName`). A length pass then
/// adds up the string's length from the text and those locals, the string is allocated, and a write pass writes the
/// text and the holes into it. Evaluating a hole in the write pass would be wrong twice over: its side effects would
/// run again, and anything it allocated would land on top of the output buffer.
module internal Transpiler.Wasm.Interpolation

open System.Text
open Parser.Program
open Transpiler.Wasm.WasmRuntime

/// What a hole holds, which decides how it is measured and written.
type HoleKind =
    | StringHole
    | IntHole
    | BoolHole
    /// A float, formatted to a string when the hole is evaluated (formatting allocates, which the length and write
    /// passes must not), and from then on a string.
    | FloatHole
    /// A tag: each case the hole's type names, with its code and its payload's kind ("string", "int" or "bool") if it
    /// has one. It prints as `#name`, or `#name(payload)` with a string payload in quotes.
    | TagHole of (string * int * string option) list

/// The scratch locals a function that builds interpolated strings declares, besides its hole locals.
let scratchLocals =
    [ "__interp_len"
      "__interp_base"
      "__interp_cursor"
      "__interp_ptr"
      "__interp_copy_len"
      "__interp_src"
      "__interp_tmp"
      "__interp_start"
      "__interp_lo"
      "__interp_hi"
      "__interp_swap" ]

/// The local the hole `index` of an interpolated string at nesting depth `depth` is evaluated into. A string inside a
/// hole of another is one deeper: its holes are evaluated while the outer string's earlier holes are already held,
/// so they must not share locals (#326). Sibling strings at one depth share them, as they are evaluated in turn.
let holeLocalName (depth: int) (index: int) = $"__interp_hole_{depth}_{index}"

let staticBytes (bytes: byte array) =
    bytes
    |> Array.toList
    |> List.map (fun b ->
        $"local.get $__interp_cursor\n    i32.const {int b}\n    i32.store8\n    local.get $__interp_cursor\n    i32.const 1\n    i32.add\n    local.set $__interp_cursor")
    |> String.concat "\n    "

let intLength exprCode =
    $"{exprCode}\n    local.set $__interp_tmp\n    local.get $__interp_tmp\n    i32.const 0\n    i32.lt_s\n    if\n      local.get $__interp_len\n      i32.const 1\n      i32.add\n      local.set $__interp_len\n      local.get $__interp_tmp\n      i32.const -1\n      i32.mul\n      local.set $__interp_tmp\n    end\n    local.get $__interp_tmp\n    i32.const 0\n    i32.eq\n    if\n      local.get $__interp_len\n      i32.const 1\n      i32.add\n      local.set $__interp_len\n    else\n      loop $interp_int_len\n        local.get $__interp_len\n        i32.const 1\n        i32.add\n        local.set $__interp_len\n        local.get $__interp_tmp\n        i32.const 10\n        i32.div_u\n        local.set $__interp_tmp\n        local.get $__interp_tmp\n        i32.const 0\n        i32.ne\n        br_if $interp_int_len\n      end\n    end"

let intWrite exprCode =
    $"{exprCode}\n    local.set $__interp_tmp\n    local.get $__interp_tmp\n    i32.const 0\n    i32.lt_s\n    if\n      local.get $__interp_cursor\n      i32.const 45\n      i32.store8\n      local.get $__interp_cursor\n      i32.const 1\n      i32.add\n      local.set $__interp_cursor\n      local.get $__interp_tmp\n      i32.const -1\n      i32.mul\n      local.set $__interp_tmp\n    end\n    local.get $__interp_tmp\n    i32.const 0\n    i32.eq\n    if\n      local.get $__interp_cursor\n      i32.const 48\n      i32.store8\n      local.get $__interp_cursor\n      i32.const 1\n      i32.add\n      local.set $__interp_cursor\n    else\n      local.get $__interp_cursor\n      local.set $__interp_start\n      loop $interp_digits\n        local.get $__interp_cursor\n        local.get $__interp_tmp\n        i32.const 10\n        i32.rem_u\n        i32.const 48\n        i32.add\n        i32.store8\n        local.get $__interp_cursor\n        i32.const 1\n        i32.add\n        local.set $__interp_cursor\n        local.get $__interp_tmp\n        i32.const 10\n        i32.div_u\n        local.set $__interp_tmp\n        local.get $__interp_tmp\n        i32.const 0\n        i32.ne\n        br_if $interp_digits\n      end\n      local.get $__interp_start\n      local.set $__interp_lo\n      local.get $__interp_cursor\n      i32.const 1\n      i32.sub\n      local.set $__interp_hi\n      block $interp_rev_done\n        loop $interp_rev\n          local.get $__interp_lo\n          local.get $__interp_hi\n          i32.ge_u\n          br_if $interp_rev_done\n          local.get $__interp_lo\n          i32.load8_u\n          local.set $__interp_swap\n          local.get $__interp_lo\n          local.get $__interp_hi\n          i32.load8_u\n          i32.store8\n          local.get $__interp_hi\n          local.get $__interp_swap\n          i32.store8\n          local.get $__interp_lo\n          i32.const 1\n          i32.add\n          local.set $__interp_lo\n          local.get $__interp_hi\n          i32.const 1\n          i32.sub\n          local.set $__interp_hi\n          br $interp_rev\n        end\n      end\n    end"

let stringCopy exprCode =
    $"{exprCode}\n    local.set $__interp_ptr\n    local.get $__interp_ptr\n    i32.const 4\n    i32.sub\n    i32.load\n    local.set $__interp_copy_len\n    local.get $__interp_ptr\n    local.set $__interp_src\n    block $interp_copy_done\n      loop $interp_copy\n        local.get $__interp_copy_len\n        i32.const 0\n        i32.eq\n        br_if $interp_copy_done\n        local.get $__interp_cursor\n        local.get $__interp_src\n        i32.load8_u\n        i32.store8\n        local.get $__interp_cursor\n        i32.const 1\n        i32.add\n        local.set $__interp_cursor\n        local.get $__interp_src\n        i32.const 1\n        i32.add\n        local.set $__interp_src\n        local.get $__interp_copy_len\n        i32.const 1\n        i32.sub\n        local.set $__interp_copy_len\n        br $interp_copy\n      end\n    end"

let boolLength exprCode =
    $"{exprCode}\n    if\n      local.get $__interp_len\n      i32.const 4\n      i32.add\n      local.set $__interp_len\n    else\n      local.get $__interp_len\n      i32.const 5\n      i32.add\n      local.set $__interp_len\n    end"

let boolWrite exprCode =
    let trueBytes = staticBytes (Encoding.UTF8.GetBytes "true")
    let falseBytes = staticBytes (Encoding.UTF8.GetBytes "false")
    $"{exprCode}\n    if\n      {trueBytes}\n    else\n      {falseBytes}\n    end"

let textLength (text: string) =
    $"local.get $__interp_len\n    i32.const {Encoding.UTF8.GetByteCount text}\n    i32.add\n    local.set $__interp_len"

let textWrite (text: string) = staticBytes (Encoding.UTF8.GetBytes text)

let private stringLength exprCode =
    $"{exprCode}\n    i32.const 4\n    i32.sub\n    i32.load\n    local.get $__interp_len\n    i32.add\n    local.set $__interp_len"

let private tagPayloadLength typeName payloadCode =
    match typeName with
    | "string" ->
        let quotes = textLength "\"\""
        $"{quotes}\n    {stringLength payloadCode}"
    | "int" -> intLength payloadCode
    | _ -> boolLength payloadCode

let private tagPayloadWrite typeName payloadCode =
    match typeName with
    | "string" ->
        let quote = textWrite "\""
        $"{quote}\n    {stringCopy payloadCode}\n    {quote}"
    | "int" -> intWrite payloadCode
    | _ -> boolWrite payloadCode

/// Each case of a tag hole in turn: the code picks the tag at run time. Calls `Helpers.tagCode`.
let private tagHole (valueCode: string) cases (renderText: string -> string) (renderPayload: string -> string -> string) =
    cases
    |> List.map (fun (name, code, payloadTypeName) ->
        let body =
            match payloadTypeName with
            | None -> renderText $"#{name}"
            | Some typeName ->
                let payloadCode = $"{valueCode}\n    i32.load offset=4"
                let opening = renderText ("#" + name + "(")
                let closing = renderText ")"
                $"{opening}\n    {renderPayload typeName payloadCode}\n    {closing}"
        $"{valueCode}\n    call ${Helpers.tagCode}\n    i32.const {code}\n    i32.eq\n    if\n      {body}\n    end")
    |> String.concat "\n    "

/// Adds the length of the hole `valueCode` reads to `$__interp_len`.
let holeLength (kind: HoleKind) (valueCode: string) =
    match kind with
    | StringHole
    | FloatHole -> stringLength valueCode
    | IntHole -> intLength valueCode
    | TagHole cases -> tagHole valueCode cases textLength tagPayloadLength
    | BoolHole -> boolLength valueCode

/// Writes the hole `valueCode` reads at `$__interp_cursor`.
let holeWrite (kind: HoleKind) (valueCode: string) =
    match kind with
    | StringHole
    | FloatHole -> stringCopy valueCode
    | IntHole -> intWrite valueCode
    | TagHole cases -> tagHole valueCode cases textWrite tagPayloadWrite
    | BoolHole -> boolWrite valueCode

/// The string, once its holes are in their locals: the length pass, the allocation (by `Helpers.stringAlloc`) and the
/// write pass, leaving the string's pointer.
let measureAndWrite (lengthCode: string) (writeCode: string) =
    $"i32.const 0\n    local.set $__interp_len\n    {lengthCode}\n    local.get $__interp_len\n    call ${Helpers.stringAlloc}\n    local.tee $__interp_base\n    local.set $__interp_cursor\n    {writeCode}\n    local.get $__interp_base"

/// The whole string: `evalCode` evaluates its holes into their locals, then `measureAndWrite`.
let build (evalCode: string) (lengthCode: string) (writeCode: string) =
    $"{evalCode}\n    {measureAndWrite lengthCode writeCode}"

/// What a debug build writes when it reaches the hole at `span` that has no known type, because of an error deferred to
/// run time, and the hole did not stop the program itself: the location as a deferred error's, then why.
let unknownHoleMessage (span: SourceSpan) =
    let location =
        if span.File = "" then $"{span.StartLine + 1}:{span.StartColumn + 1}: "
        else $"{span.File}:{span.StartLine + 1}:{span.StartColumn + 1}: "
    $"{location}error: the value interpolated here has no known type, because of an error deferred from compile time\n"