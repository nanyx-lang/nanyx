/// WebAssembly text read as the s-expressions it is written in, for `WasmBinary` to assemble: lists, atoms (keywords,
/// `$names`, numbers and `key=value` immediates) and strings, with comments left out but the markers the backend writes:
/// places (`;;@ main.nyx:12`, see `CoreIrEmit.placeMarker`), probes and definitions.
module Transpiler.Wasm.WatSyntax

open System
open System.Text

/// A node of WebAssembly text.
type SExpr =
    /// `( ... )`.
    | List of SExpr list
    /// A keyword, a `$name`, a number or a `key=value` immediate, as written.
    | Atom of string
    /// A string, as the bytes its escapes stand for.
    | Text of byte array
    /// A place marker, `;;@ main.nyx:12:5`: the code after it, up to the next, was made from source at this line and
    /// column of this file. To WebAssembly it is a comment. Code inlined from other functions has a frame for each call
    /// it was inlined through (`;;@ prelude.nyx:18:3 | crash | main.nyx:12:5`), innermost first, each with the name of
    /// the function whose code it is; the outermost's is the function the marker is in, so it has none. A place written
    /// `file:line` has column 0, none.
    | Placed of frames: (string option * string * int * int) list
    /// A probe's marker, `;;@probe 3 statement` or `;;@probe 1 entry count` (see `CoreIrEmit.Probe`): the probe of this
    /// number and kind, and for an entry the function's name, is at the place of the last place marker before it. To
    /// WebAssembly it is a comment.
    | Probed of id: int * kind: string * functionName: string option
    /// A definition's marker, `;;@def main.nyx:3`, first in a function's body (see `CoreIrEmit.definitionMarker`): the
    /// function was defined on this line of this file. To WebAssembly it is a comment.
    | Defined of file: string * line: int
    /// A file of the standard library's, `;;@lib prelude.nyx`, among the module's fields: the places in it are the
    /// library's, not the program's (see `CoreIrEmit.libraryMarker`). To WebAssembly it is a comment.
    | Library of file: string

/// WebAssembly text that is not well formed: what is wrong, and the offset into the text where it was found.
exception WatSyntaxError of message: string * offset: int

/// The bytes the string literal whose text (between the quotes) is `body` stands for: UTF-8, with `\n`, `\t`, `\r`,
/// `\\`, `\'`, `\"`, `\u{hex}` and two hex digits for a byte.
let private stringBytes (body: string) (offset: int) =
    let bytes = ResizeArray<byte>()
    let mutable i = 0
    let isHex (c: char) = Uri.IsHexDigit c
    while i < body.Length do
        match body.[i] with
        | '\\' when i + 1 < body.Length ->
            let next = body.[i + 1]
            match next with
            | 'n' -> bytes.Add 10uy; i <- i + 2
            | 't' -> bytes.Add 9uy; i <- i + 2
            | 'r' -> bytes.Add 13uy; i <- i + 2
            | '\\' | '\'' | '"' -> bytes.Add(byte next); i <- i + 2
            | 'u' when i + 2 < body.Length && body.[i + 2] = '{' ->
                let close = body.IndexOf('}', i + 3)
                if close < 0 then raise (WatSyntaxError("an unterminated \\u{...} escape", offset + i))
                let code = Convert.ToInt32(body.Substring(i + 3, close - i - 3).Replace("_", ""), 16)
                bytes.AddRange(Encoding.UTF8.GetBytes(Char.ConvertFromUtf32 code))
                i <- close + 1
            | _ when i + 2 < body.Length && isHex next && isHex body.[i + 2] ->
                bytes.Add(Convert.ToByte(body.Substring(i + 1, 2), 16))
                i <- i + 3
            | _ -> raise (WatSyntaxError($"an unknown escape \\{next}", offset + i))
        | c when Char.IsHighSurrogate c && i + 1 < body.Length ->
            bytes.AddRange(Encoding.UTF8.GetBytes(body.Substring(i, 2)))
            i <- i + 2
        | c ->
            bytes.AddRange(Encoding.UTF8.GetBytes(string c))
            i <- i + 1
    bytes.ToArray()

/// Whether `c` ends an atom.
let private endsAtom (c: char) = Char.IsWhiteSpace c || c = '(' || c = ')' || c = '"' || c = ';'

/// The s-expressions `text` is written as, in order.
let read (text: string) : SExpr list =
    let mutable i = 0
    let isMarker (at: int) = at + 2 < text.Length && text.[at] = ';' && text.[at + 1] = ';' && text.[at + 2] = '@'
    let skipSpace () =
        let mutable going = true
        while going && i < text.Length do
            if Char.IsWhiteSpace text.[i] then i <- i + 1
            elif isMarker i then going <- false
            elif text.[i] = ';' && i + 1 < text.Length && text.[i + 1] = ';' then
                while i < text.Length && text.[i] <> '\n' do
                    i <- i + 1
            elif text.[i] = '(' && i + 1 < text.Length && text.[i + 1] = ';' then
                // Block comments nest.
                let start = i
                let mutable depth = 1
                i <- i + 2
                while depth > 0 do
                    if i + 1 >= text.Length then raise (WatSyntaxError("an unterminated block comment", start))
                    elif text.[i] = '(' && text.[i + 1] = ';' then depth <- depth + 1; i <- i + 2
                    elif text.[i] = ';' && text.[i + 1] = ')' then depth <- depth - 1; i <- i + 2
                    else i <- i + 1
            else going <- false
    // The nodes read so far of the list being read, or of the top level, and below it those of each list it is in. A
    // stack rather than recursion, as code can nest thousands of lists deep (an else-if chain is an `if` in an `if`).
    let mutable items = ResizeArray<SExpr>()
    let outer = System.Collections.Generic.Stack<ResizeArray<SExpr>>()
    let mutable finished = false
    while not finished do
        skipSpace ()
        if i >= text.Length then
            if outer.Count = 0 then finished <- true else raise (WatSyntaxError("a list that is never closed", i))
        else
            match text.[i] with
            | ';' ->
                // A place marker: `file:line:column`, then a function's name and the `file:line:column` of its call
                // for each call the code was inlined through. A definition's marker is `file:line`. Each file runs to
                // the `:` before the numbers.
                let start = i
                let lineEnd = match text.IndexOf('\n', i) with -1 -> text.Length | found -> found
                let written = text.Substring(i + 3, lineEnd - i - 3).Trim()
                let parts = written.Split(" | ")
                let numberAfter (written: string) =
                    let colon = written.LastIndexOf ':'
                    match (if colon < 0 then None else Some(Int32.TryParse(written.Substring(colon + 1)))) with
                    | Some(true, number) -> Some(written.Substring(0, colon), number)
                    | _ -> None
                let place (written: string) =
                    match numberAfter written with
                    | Some(rest, last) ->
                        match numberAfter rest with
                        | Some(file, line) -> file, line, last
                        | None -> rest, last, 0
                    | None -> raise (WatSyntaxError("a place marker that is not ;;@ file:line:column", start))
                // A function's name, after `entry`, may hold spaces, as one in backticks does.
                match written.Split(' ', 4) with
                | _ when written.StartsWith "lib " -> items.Add(Library(written.Substring("lib ".Length).Trim()))
                | _ when written.StartsWith "def " ->
                    let file, line, _ = place (written.Substring "def ".Length)
                    items.Add(Defined(file, line))
                | [| "probe"; id; kind |]
                | [| "probe"; id; kind; _ |] as probe ->
                    match Int32.TryParse id with
                    | true, id -> items.Add(Probed(id, kind, Array.tryItem 3 probe))
                    | _ -> raise (WatSyntaxError("a probe's marker that is not ;;@probe number kind", start))
                | _ ->
                    if parts.Length % 2 = 0 then raise (WatSyntaxError("a place marker with a function but not its call", start))
                    let frames =
                        [ for index in 0..2 .. parts.Length - 1 ->
                              let file, line, column = place parts.[index]
                              (if index + 1 < parts.Length then Some parts.[index + 1] else None), file, line, column ]
                    items.Add(Placed frames)
                i <- lineEnd
            | '(' ->
                i <- i + 1
                outer.Push items
                items <- ResizeArray<SExpr>()
            | ')' ->
                if outer.Count = 0 then raise (WatSyntaxError("a ) that closes nothing", i))
                i <- i + 1
                let list = List(List.ofSeq items)
                items <- outer.Pop()
                items.Add list
            | '"' ->
                let start = i
                i <- i + 1
                while i < text.Length && text.[i] <> '"' do
                    i <- i + (if text.[i] = '\\' then 2 else 1)
                if i >= text.Length then raise (WatSyntaxError("an unterminated string", start))
                items.Add(Text(stringBytes (text.Substring(start + 1, i - start - 1)) (start + 1)))
                i <- i + 1
            | _ ->
                let start = i
                while i < text.Length && not (endsAtom text.[i]) do
                    i <- i + 1
                items.Add(Atom(text.Substring(start, i - start)))
    List.ofSeq items
