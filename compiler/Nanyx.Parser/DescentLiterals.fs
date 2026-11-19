/// The recursive-descent parser's literals: the grammar of `Lexical.fs`'s literals, read from the layout pass's tokens.
/// The lexer reads a number without its sign, and the FParsec parser reads a sign as part of a literal, so a `+` or `-`
/// written straight before a number is read with it here.
module Parser.Program.DescentLiterals

open System
open System.Globalization
open Parser.Program.Lexer
open Parser.Program.Layout
open Parser.Program.DescentCursor

/// Whether `digits`, a number's digits, have each `_` between two digits, as the FParsec parser requires.
let private wellGrouped (digits: string) =
    not (digits.StartsWith "_" || digits.EndsWith "_" || digits.Contains "__")

/// The `+` or `-` at the cursor, when a number follows it straight away, as text: empty when there is none.
let private sign (c: Cursor) =
    let isSign = c.Is(Operator, "-") || c.Is(Operator, "+")
    let numberNext = match c.At 1 with Code next -> next.Kind = Number && Some next.Start = (c.Token |> Option.map _.End) | _ -> false
    if isSign && numberNext then c.Text(c.Take()) else ""

/// Whether the number written as `text` is an integer: no fraction and no exponent.
let private isInteger (text: string) =
    let lower = text.ToLowerInvariant()
    lower.StartsWith "0x" || lower.StartsWith "0o" || lower.StartsWith "0b" || not (text.Contains '.' || lower.Contains 'e')

/// An integer, with its sign if one is written straight before it.
let intLiteral (c: Cursor) : Literal * SourceSpan =
    let start = c.Start
    let mark = c.Index
    let signText = sign c
    let token = c.Expect Number
    let text = c.Text token
    if not (isInteger text) then
        c.Index <- mark
        raise NoMatch
    // A single `.` after the digits makes it a float, `1.`, which `literal` reads.
    if c.Adjacent && c.Is Dot then
        c.Index <- mark
        raise NoMatch
    let lower = text.ToLowerInvariant()
    let value =
        if lower.StartsWith "0x" || lower.StartsWith "0o" || lower.StartsWith "0b" then
            if text.Contains '_' then c.GiveUp "a `_` in a hexadecimal, octal or binary number"
            let radix = match lower[1] with 'x' -> 16 | 'o' -> 8 | _ -> 2
            let magnitude =
                try Convert.ToInt64(text.Substring 2, radix)
                with _ -> c.GiveUp "a hexadecimal, octal or binary number too large for 64 bits"
            bigint (if signText = "-" then -magnitude else magnitude)
        else
            if not (wellGrouped text) then c.GiveUp "a `_` in a number that isn't between two digits"
            Numerics.BigInteger.Parse(signText + text.Replace("_", ""), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)
    IntLit value, c.SpanFrom start

/// A float: digits with a fraction or an exponent, or digits and a `.` alone, `1.`.
let private floatLiteral (c: Cursor) : Literal * SourceSpan =
    let start = c.Start
    let signText = sign c
    let token = c.Expect Number
    let text = c.Text token
    let lower = text.ToLowerInvariant()
    if lower.StartsWith "0x" || lower.StartsWith "0o" || lower.StartsWith "0b" then raise NoMatch
    let written =
        if isInteger text then
            // `1.`: the lexer reads the `.` apart, since no digit follows it.
            if not (c.Adjacent && c.Is Dot) then raise NoMatch
            c.Take() |> ignore
            if c.Adjacent then c.GiveUp "a `.` after a number with a name or number straight after it"
            text + "."
        elif not (text.Contains '.') then c.GiveUp "a number with an exponent and no fraction"
        else text
    let parts = written.Split([| '.'; 'e'; 'E'; '+'; '-' |])
    if parts |> Array.exists (fun part -> part <> "" && not (wellGrouped part)) then c.GiveUp "a `_` in a number that isn't between two digits"
    FloatLit(Double.Parse(signText + written.Replace("_", ""), CultureInfo.InvariantCulture)), c.SpanFrom start

/// A string with no holes, double-quoted with its escapes decoded, or single-quoted and raw.
let stringLiteral (c: Cursor) : Literal * SourceSpan =
    let token = c.Expect StringLiteral
    let text = c.Text token
    let inner = text.Substring(1, text.Length - 2)
    c.NoteStringText (token.Start + 1) (token.End - 1)
    let value =
        if text.StartsWith "'" then inner
        else
            match StringEscapes.decode inner with
            | Some decoded -> decoded
            | None -> c.GiveUp "a backslash in a string that isn't an escape"
    StringLit value, c.SpanOf token

/// A literal: an integer, a float, `true` or `false`, or a string with no holes (`Lexical.literalNoWs`).
let literal (c: Cursor) : Literal * SourceSpan =
    match c.Attempt(fun () -> intLiteral c) with
    | Some literal -> literal
    | None ->
        match c.Attempt(fun () -> floatLiteral c) with
        | Some literal -> literal
        | None ->
            if c.Is(Name, "true") || c.Is(Name, "false") then
                let token = c.Take()
                BoolLit(c.Text token = "true"), c.SpanOf token
            else stringLiteral c
