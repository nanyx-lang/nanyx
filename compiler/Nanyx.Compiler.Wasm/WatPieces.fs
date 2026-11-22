/// Pieces of a function's WebAssembly text written out of line, so that an expression nested thousands deep, such as a
/// long sum or else-if chain, is written in time that grows with its length (#2351). Each level's code holds a short
/// reference to the code inside it instead of a copy of it, and the references are replaced by what they stand for once,
/// when the function's text is finished.
module Transpiler.Wasm.WatPieces

open System.Text

// What a reference starts and ends with: a character no WebAssembly text the backend writes has.
let private mark = '\u0000'

/// The pieces of one function's text, each written once by `Expand`.
type WatPieces() =
    let pieces = ResizeArray<string>()

    /// A reference to `code`, which `Expand` writes in its place. Empty code stays empty, so that it is still left out
    /// where empty code is.
    member _.Add (code: string) : string =
        if code = "" then ""
        else
            pieces.Add code
            $"{mark}{pieces.Count - 1}{mark}"

    /// `code` with each reference replaced by its piece, and each reference in that by its own, however deep.
    member _.Expand (code: string) : string =
        let text = StringBuilder()
        // The text being written, with where in it the writing is, and below it the texts it is a piece of.
        let pending = System.Collections.Generic.Stack<string * int>()
        pending.Push((code, 0))
        while pending.Count > 0 do
            let current, start = pending.Pop()
            match current.IndexOf(mark, start) with
            | -1 -> text.Append(current, start, current.Length - start) |> ignore
            | at ->
                text.Append(current, start, at - start) |> ignore
                let finish = current.IndexOf(mark, at + 1)
                let index = int (current.Substring(at + 1, finish - at - 1))
                pending.Push((current, finish + 1))
                pending.Push((pieces.[index], 0))
        text.ToString()
