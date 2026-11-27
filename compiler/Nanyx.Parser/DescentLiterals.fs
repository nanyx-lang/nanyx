/// The recursive-descent parser's literals, read from the layout pass's tokens. The lexer reads a number without its
/// sign, and a sign is part of a literal, so a `+` or `-` written straight before a number is read with it here.
module Parser.Program.DescentLiterals

open System
open System.Globalization
open Parser.Program.Lexer
open Parser.Program.Layout
open Parser.Program.DescentCursor

/// Whether `digits`, a number's digits, have each `_` between two digits.
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
    Lexer.isRadixNumber text 0 || not (text.Contains '.' || lower.Contains 'e')

/// Reports the first run of digits and `_`s in `text`, a number written at `start`, whose `_` isn't between two digits:
/// the whole number's digits, then its fraction's, then its exponent's.
let private reportBadGroups (c: Cursor) (start: int) (text: string) =
    let groups =
        let runAt (offset: int) =
            let mutable finish = offset
            while finish < text.Length && (Char.IsAsciiDigit text[finish] || text[finish] = '_') do
                finish <- finish + 1
            offset, text.Substring(offset, finish - offset)
        let whole = runAt 0
        let fraction = match text.IndexOf '.' with -1 -> [] | dot -> [ runAt (dot + 1) ]
        let exponent =
            match text.IndexOfAny [| 'e'; 'E' |] with
            | -1 -> []
            | e -> [ runAt (if e + 1 < text.Length && (text[e + 1] = '+' || text[e + 1] = '-') then e + 2 else e + 1) ]
        whole :: fraction @ exponent
    match groups |> List.tryFind (fun (_, digits) -> digits <> "" && not (wellGrouped digits)) with
    | Some(offset, digits) -> c.Report (start + offset) $"A `_` in a number goes between two digits: '{digits}' is not a number"
    | None -> ()

/// Whether `token`, a number, has a unit written straight after it, as `500ms` has.
let private hasUnit (c: Cursor) (token: Token) = Lexer.numberEnd c.Source token.Start < token.End

/// An integer, with its sign if one is written straight before it.
let intLiteral (c: Cursor) : Literal * SourceSpan =
    let start = c.Start
    let mark = c.Index
    let signText = sign c
    let token = c.Expect Number
    let text = c.Text token
    if not (isInteger text) || hasUnit c token then
        c.Index <- mark
        raise NoMatch
    // A single `.` after the digits makes it a float, `1.`, which `literal` reads.
    if c.Adjacent && c.Is Dot then
        c.Index <- mark
        raise NoMatch
    let lower = text.ToLowerInvariant()
    let value =
        if Lexer.isRadixNumber text 0 then
            if text.Contains '_' then c.GiveUp "a `_` in a hexadecimal, octal or binary number"
            let radix = match lower[1] with 'x' -> 16 | 'o' -> 8 | _ -> 2
            let magnitude =
                try Convert.ToInt64(text.Substring 2, radix)
                with _ -> c.GiveUp "a hexadecimal, octal or binary number too large for 64 bits"
            bigint (if signText = "-" then -magnitude else magnitude)
        else
            reportBadGroups c token.Start text
            Numerics.BigInteger.Parse(signText + text.Replace("_", ""), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)
    IntLit value, c.SpanFrom start

/// Reports `token`, a number written with an exponent and no fraction, `1e5` or `1e3ms`: as in a float, an exponent
/// comes after a fraction.
let private exponentWithoutFraction (c: Cursor) (token: Token) : 'a =
    let written = c.Text token
    let fixedText = written.Insert(written.IndexOfAny [| 'e'; 'E' |], ".0")
    c.ReportBetween token.Start token.End $"An exponent comes after a fraction: write '{fixedText}', not '{written}'"

/// A float: digits with a fraction or an exponent, or digits and a `.` alone, `1.`.
let private floatLiteral (c: Cursor) : Literal * SourceSpan =
    let start = c.Start
    let signText = sign c
    let token = c.Expect Number
    let text = c.Text token
    if Lexer.isRadixNumber text 0 || hasUnit c token then raise NoMatch
    let written =
        if isInteger text then
            // `1.`: the lexer reads the `.` apart, since no digit follows it.
            if not (c.Adjacent && c.Is Dot) then raise NoMatch
            c.Take() |> ignore
            // `1._5`: the lexer reads `_5` as a name, which is the fraction's digits here.
            match c.Token with
            | Some next when c.Adjacent && next.Kind = Name && (c.Text next).StartsWith "_" ->
                let written = c.Text next
                let digits = written.Substring(0, (written |> Seq.takeWhile (fun ch -> Char.IsAsciiDigit ch || ch = '_') |> Seq.length))
                c.Report next.Start $"A `_` in a number goes between two digits: '{digits}' is not a number"
            | _ -> ()
            if c.Adjacent then c.GiveUp "a `.` after a number with a name or number straight after it"
            text + "."
        elif not (text.Contains '.') then exponentWithoutFraction c token
        else text
    let parts = written.Split([| '.'; 'e'; 'E'; '+'; '-' |])
    if parts |> Array.exists (fun part -> part <> "" && not (wellGrouped part)) then reportBadGroups c token.Start text
    FloatLit(Double.Parse(signText + written.Replace("_", ""), CultureInfo.InvariantCulture)), c.SpanFrom start

/// A number with a unit written straight after it, `500ms`, `0.5s` or `1.5e3ms`, with its sign if one is written straight
/// before it: the number exactly as written, and the unit (see `UnitSyntax`).
let private unitLiteral (c: Cursor) : Literal * SourceSpan =
    let start = c.Start
    let signText = sign c
    let token = c.Expect Number
    if not (hasUnit c token) then raise NoMatch
    let numberEnd = Lexer.numberEnd c.Source token.Start
    let text = c.Source.Substring(token.Start, numberEnd - token.Start)
    reportBadGroups c token.Start text
    // As in a float, an exponent comes after a fraction: `1.5e3ms`, not `1e3ms`.
    if text.IndexOfAny [| 'e'; 'E' |] >= 0 && not (text.Contains '.') then exponentWithoutFraction c token
    let plain = text.Replace("_", "")
    let mantissa, exponent =
        match plain.IndexOfAny [| 'e'; 'E' |] with
        | -1 -> plain, 0
        | e ->
            match Int32.TryParse(plain.Substring(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) with
            | true, value -> plain.Substring(0, e), value
            | false, _ -> c.Report token.Start $"The exponent of '{text}' is too large"
    let fractionDigits = match mantissa.IndexOf '.' with -1 -> 0 | dot -> mantissa.Length - dot - 1
    let digits = Numerics.BigInteger.Parse(mantissa.Replace(".", ""), CultureInfo.InvariantCulture)
    let number =
        { Digits = (if signText = "-" then -digits else digits)
          Exponent = exponent - fractionDigits
          Decimal = text.IndexOfAny [| '.'; 'e'; 'E' |] >= 0 }
    UnitLit(number, c.Source.Substring(numberEnd, token.End - numberEnd)), c.SpanFrom start

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
            | None -> reportBadEscape c (token.Start + 1) inner
    StringLit value, c.SpanOf token

/// A literal: an integer, a float, a number with a unit, `true` or `false`, or a string with no holes.
let literal (c: Cursor) : Literal * SourceSpan =
    match c.Attempt(fun () -> intLiteral c) with
    | Some literal -> literal
    | None ->
    match c.Attempt(fun () -> unitLiteral c) with
    | Some literal -> literal
    | None ->
        match c.Attempt(fun () -> floatLiteral c) with
        | Some literal -> literal
        | None ->
            if c.Is(Name, "true") || c.Is(Name, "false") then
                let token = c.Take()
                BoolLit(c.Text token = "true"), c.SpanOf token
            else stringLiteral c
