namespace NyxCompiler

/// A format spec written in a hole, `"{price:.2f}"`, read as the prelude's `fmtSpec` reads one as the program runs,
/// after Python's format-spec mini-language, and checked against what the hole's value is, as that value's
/// `tryFormat` checks it, so that a spec it would reject is an error as the program compiles (NYX0307). The two readers
/// are tested against one table of specs, `compiler/FormatSpecTable.fs`: a change to one is a change to the other.
/// Like `fmtSpec`, it counts bytes of the spec's UTF-8: `At` is the byte the spec stops being one at.
module internal FormatSpecs =
    /// What a value written by a spec is, as far as the specs it takes go.
    [<RequireQualifiedAccess>]
    type Formatted =
        /// An `int` or a sized integer.
        | Integer
        /// A `float` or an `F32`.
        | Float
        /// A `string`, a `char` or a `bool`.
        | Text
        | Instant
        /// A unit quantity of the dimension written `dimension`, whose units the module sees are `units`, the largest
        /// first.
        | Measured of dimension: string * units: string list

    /// Where a spec stops being one, a byte of it counting from 0, and what it could have there, as `BadFormat` says.
    type Bad = { At: int; Expected: string }

    /// A spec's parts, each where it is written, or -1, as the prelude's `FormatSpec` has them.
    type private Parsed =
        { FillGiven: bool
          Align: byte
          AlignAt: int
          SignAt: int
          ZAt: int
          AlternateAt: int
          ZeroAt: int
          Grouping: byte
          GroupingAt: int
          PrecisionAt: int
          Kind: byte
          KindAt: int }

    let private noByte = 0uy

    /// How many bytes the UTF-8 character that starts with `lead` has.
    let private charLength (lead: int) = if lead < 128 then 1 elif lead < 224 then 2 elif lead < 240 then 3 else 4

    let private isAlign (b: int) = b = int '<' || b = int '>' || b = int '^' || b = int '='

    let private isDigit (b: int) = b >= int '0' && b <= int '9'

    /// `spec` read as `[[fill]align][sign][z][#][0][width][grouping][.precision][type]`, as `fmtSpec` reads it.
    let private read (spec: byte array) : Result<Parsed, Bad> =
        let length = spec.Length
        let at (i: int) = if i < length then int spec[i] else -1
        let digitsEnd (from: int) =
            let mutable i = from
            while isDigit (at i) do
                i <- i + 1
            i
        let firstLength = charLength (at 0)
        let fillGiven = length > firstLength && isAlign (at firstLength)
        let alignAt = if fillGiven then firstLength elif isAlign (at 0) then 0 else -1
        let afterAlign = if alignAt >= 0 then alignAt + 1 else 0
        let signAt = if at afterAlign = int '+' || at afterAlign = int '-' || at afterAlign = int ' ' then afterAlign else -1
        let afterSign = if signAt >= 0 then afterAlign + 1 else afterAlign
        let zAt = if at afterSign = int 'z' then afterSign else -1
        let afterZ = if zAt >= 0 then afterSign + 1 else afterSign
        let alternateAt = if at afterZ = int '#' then afterZ else -1
        let afterAlternate = if alternateAt >= 0 then afterZ + 1 else afterZ
        let zeroAt = if at afterAlternate = int '0' then afterAlternate else -1
        let afterZero = if zeroAt >= 0 then afterAlternate + 1 else afterAlternate
        let widthEnd = digitsEnd afterZero
        let groupingAt = if at widthEnd = int ',' || at widthEnd = int '_' then widthEnd else -1
        let afterGrouping = if groupingAt >= 0 then widthEnd + 1 else widthEnd
        let dotAt = if at afterGrouping = int '.' then afterGrouping else -1
        let precisionEnd = if dotAt >= 0 then digitsEnd (dotAt + 1) else afterGrouping
        let kind = at precisionEnd
        let isKind = (kind >= int 'A' && kind <= int 'Z') || (kind >= int 'a' && kind <= int 'z') || kind = int '%'
        let kindAt = if isKind then precisionEnd else -1
        let finish = if isKind then precisionEnd + 1 else precisionEnd
        let bad at expected = Error { At = at; Expected = expected }
        if widthEnd - afterZero > 9 then bad (afterZero + 9) "a width of at most 9 digits"
        elif dotAt >= 0 && precisionEnd = dotAt + 1 then bad (dotAt + 1) "a digit after the `.`"
        elif dotAt >= 0 && precisionEnd - dotAt > 10 then bad (dotAt + 10) "a precision of at most 9 digits"
        elif finish < length then bad finish (if isKind then "the end of the spec" else "a type such as `d`, `f` or `s`, or the end of the spec")
        else
            Ok
                { FillGiven = fillGiven
                  Align = (if alignAt >= 0 then spec[alignAt] else noByte)
                  AlignAt = alignAt
                  SignAt = signAt
                  ZAt = zAt
                  AlternateAt = alternateAt
                  ZeroAt = zeroAt
                  Grouping = (if groupingAt >= 0 then spec[groupingAt] else noByte)
                  GroupingAt = groupingAt
                  PrecisionAt = dotAt
                  Kind = (if isKind then byte kind else noByte)
                  KindAt = kindAt }

    let private floatKinds = "eEfFgG%"

    let private isFloatKind (kind: byte) = kind = noByte || floatKinds.Contains(char kind)

    /// What an integer says of `parsed`, as `fmtIntegerKind` and `fmtInteger` do.
    let private integer (parsed: Parsed) : Bad option =
        let bad at expected = Some { At = at; Expected = expected }
        match char parsed.Kind with
        | kind when parsed.Kind <> noByte && floatKinds.Contains kind -> None
        | kind when parsed.Kind <> noByte && not ("dboxX".Contains kind) ->
            bad parsed.KindAt "one of `b`, `d`, `e`, `E`, `f`, `F`, `g`, `G`, `o`, `x`, `X` or `%`"
        | kind ->
            let decimal = parsed.Kind = noByte || kind = 'd'
            if parsed.PrecisionAt >= 0 then bad parsed.PrecisionAt "no precision: an integer has no digits after a point"
            elif parsed.ZAt >= 0 then bad parsed.ZAt "no `z`: an integer has no negative zero"
            elif parsed.Grouping = byte ',' && not decimal then bad parsed.GroupingAt "`_` between the digits of a number not in decimal"
            else None

    /// What a float says of `parsed`, as `fmtFloatParsed` does.
    let private float (parsed: Parsed) : Bad option =
        if isFloatKind parsed.Kind then None else Some { At = parsed.KindAt; Expected = "one of `e`, `E`, `f`, `F`, `g`, `G` or `%`" }

    /// What text says of `parsed`, as `fmtText` does.
    let private text (parsed: Parsed) : Bad option =
        let bad at expected = Some { At = at; Expected = expected }
        if parsed.Kind <> noByte && parsed.Kind <> byte 's' then bad parsed.KindAt "`s`, or no type"
        elif parsed.Align = byte '=' then bad parsed.AlignAt "`<`, `>` or `^`: text has no sign to pad after"
        elif parsed.SignAt >= 0 then bad parsed.SignAt "no sign: text isn't a number"
        elif parsed.ZAt >= 0 then bad parsed.ZAt "no `z`: text isn't a number"
        elif parsed.AlternateAt >= 0 then bad parsed.AlternateAt "no `#`: text isn't a number"
        elif parsed.ZeroAt >= 0 && not parsed.FillGiven then
            bad parsed.ZeroAt "a width that doesn't start with `0`, or a fill and an align before it"
        elif parsed.GroupingAt >= 0 then bad parsed.GroupingAt "no grouping: text isn't a number"
        else None

    /// The directives `fmtDirective` knows, each the byte after a `%`.
    let private directives = "YymdjHIMSfpaAbBz%FT"

    /// What an instant says of `spec`, its directives each a `%` and one of `directives`, as `fmtStrftime` does.
    let private instant (spec: byte array) : Bad option =
        let expected = "a directive such as `%Y`, `%m` or `%d`, or `%%` for a `%`"
        let rec from (i: int) =
            if i >= spec.Length then None
            elif spec[i] <> byte '%' then from (i + 1)
            elif i + 1 < spec.Length && directives.Contains(char spec[i + 1]) then from (i + 2)
            else Some { At = i + 1; Expected = expected }
        if spec = "iso"B then None else from 0

    /// `units` as a spec's `expected` lists them, as `fmtUnitList` does.
    let rec private unitList (units: string list) =
        match units with
        | [] -> "no unit: the module sees none"
        | [ name ] -> $"`{name}`"
        | [ name; last ] -> $"`{name}` or `{last}`"
        | name :: rest -> $"`{name}`, {unitList rest}"

    /// What a unit quantity says of `spec`, as `__measuredTryFormat` does: a float's spec, then one of `units` after a
    /// space, or a float's spec alone. A spec a float rejects that has text after its last space names no unit.
    let private measured (dimension: string) (units: string list) (spec: byte array) : Bad option =
        let named =
            units
            |> List.tryPick (fun name ->
                let written = System.Text.Encoding.UTF8.GetBytes(name: string)
                let at = spec.Length - written.Length
                if at >= 1 && spec[at - 1] = byte ' ' && spec[at..] = written then Some at else None)
        let numberEnd = named |> Option.map (fun at -> at - 1) |> Option.defaultValue spec.Length
        let numberSpec = spec[.. numberEnd - 1]
        let floatBad =
            match read numberSpec with
            | Error bad -> Some bad
            | Ok parsed -> float parsed
        match floatBad, named with
        | Some bad, None ->
            match Array.tryFindIndexBack ((=) (byte ' ')) numberSpec with
            | Some space when space + 1 < numberEnd -> Some { At = space + 1; Expected = $"the name of a unit of {dimension}: {unitList units}" }
            | _ -> Some bad
        | bad, _ -> bad

    /// Why a value that is `formatted` doesn't take `spec`, as its `tryFormat` would give `#badFormat`, or `None` when it
    /// takes it. Every value takes the empty spec.
    let check (formatted: Formatted) (spec: string) : Bad option =
        let bytes = System.Text.Encoding.UTF8.GetBytes spec
        let readBy (takes: Parsed -> Bad option) =
            match read bytes with
            | Error bad -> Some bad
            | Ok parsed -> takes parsed
        if bytes.Length = 0 then None
        else
            match formatted with
            | Formatted.Integer -> readBy integer
            | Formatted.Float -> readBy float
            | Formatted.Text -> readBy text
            | Formatted.Instant -> instant bytes
            | Formatted.Measured(dimension, units) -> measured dimension units bytes

    /// The character of `spec` that its byte `at` is in, counting from 0, for a span on it.
    let charAt (spec: string) (at: int) : int =
        let bytes = System.Text.Encoding.UTF8.GetBytes spec
        System.Text.Encoding.UTF8.GetCharCount(bytes, 0, min at bytes.Length)
