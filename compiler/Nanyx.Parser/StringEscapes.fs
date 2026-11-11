namespace Parser.Program

open System
open System.Text

/// The backslash escapes of double-quoted strings: `\n`, `\t`, `\r`, `\\`, `\"`, `\{`, `\}`, and `\u{...}` for any code
/// point, written in hex. Single-quoted strings are raw and have none. The parser decodes them (see
/// `Lexical.escapeSequence`), so the text in the syntax tree is what the string holds; the formatter writes the text
/// back as it was written, checking it with `decode`.
module StringEscapes =
    /// Each character that stands for another after a backslash, with the character it stands for.
    let simple = [ 'n', '\n'; 't', '\t'; 'r', '\r'; '\\', '\\'; '"', '"'; '{', '{'; '}', '}' ]

    /// The escapes, as a diagnostic lists them.
    let written = "`\\n`, `\\t`, `\\r`, `\\\\`, `\\\"`, `\\{`, `\\}` and `\\u{...}`"

    /// The text the escape `\u{digits}` stands for: None unless `digits` is one to six hex digits naming a code point
    /// that is not a surrogate.
    let tryCodePoint (digits: string) : string option =
        if digits.Length >= 1 && digits.Length <= 6 && digits |> Seq.forall Uri.IsHexDigit then
            let value = Convert.ToInt32(digits, 16)
            if value <= 0x10FFFF && (value < 0xD800 || value > 0xDFFF) then Some (Char.ConvertFromUtf32 value) else None
        else
            None

    /// The text that `raw`, written between double quotes with no holes in it, stands for: None when it has a
    /// backslash that is not one of the escapes.
    let decode (raw: string) : string option =
        let out = StringBuilder()
        let rec from i =
            if i >= raw.Length then
                Some (out.ToString())
            elif raw[i] <> '\\' then
                out.Append raw[i] |> ignore
                from (i + 1)
            elif i + 1 >= raw.Length then
                None
            else
                match simple |> List.tryFind (fun (c, _) -> c = raw[i + 1]) with
                | Some (_, decoded) ->
                    out.Append decoded |> ignore
                    from (i + 2)
                | None when raw[i + 1] = 'u' && i + 2 < raw.Length && raw[i + 2] = '{' ->
                    match raw.IndexOf('}', i + 3) with
                    | -1 -> None
                    | close ->
                        tryCodePoint (raw.Substring(i + 3, close - i - 3))
                        |> Option.bind (fun decoded ->
                            out.Append decoded |> ignore
                            from (close + 1))
                | None -> None
        from 0

    /// `text` as it can be written between double quotes: a backslash, a double quote and an opening brace escaped,
    /// and line breaks and tabs written as escapes.
    let escape (text: string) : string =
        let out = StringBuilder()
        for c in text do
            match c with
            | '\n' -> out.Append "\\n"
            | '\t' -> out.Append "\\t"
            | '\r' -> out.Append "\\r"
            | '\\' -> out.Append "\\\\"
            | '"' -> out.Append "\\\""
            | '{' -> out.Append "\\{"
            | c -> out.Append c
            |> ignore
        out.ToString()
