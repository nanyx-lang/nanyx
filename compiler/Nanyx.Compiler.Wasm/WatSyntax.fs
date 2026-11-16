/// WebAssembly text read as the s-expressions it is written in, for `WasmBinary` to assemble: lists, atoms (keywords,
/// `$names`, numbers and `key=value` immediates) and strings, with comments left out.
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
    let skipSpace () =
        let mutable going = true
        while going && i < text.Length do
            if Char.IsWhiteSpace text.[i] then i <- i + 1
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
    // The nodes up to the `)` that closes the list being read, or the end of the text at the top level.
    let rec nodes (topLevel: bool) : SExpr list =
        let items = ResizeArray<SExpr>()
        let mutable closed = false
        while not closed do
            skipSpace ()
            if i >= text.Length then
                if topLevel then closed <- true else raise (WatSyntaxError("a list that is never closed", i))
            else
                match text.[i] with
                | '(' ->
                    i <- i + 1
                    items.Add(List(nodes false))
                | ')' ->
                    if topLevel then raise (WatSyntaxError("a ) that closes nothing", i))
                    i <- i + 1
                    closed <- true
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
    nodes true
