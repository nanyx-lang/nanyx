/// `Format` and `format` (#3173): a value written by a format spec, after Python's format-spec mini-language, for the
/// numbers, strings, chars, bools, instants and unit quantities, a spec a type doesn't take, and a spec written in a
/// hole.
module TranspilerWasmTests.Features.StdLib.Format

open Xunit
open TranspilerWasmTestHelpers

/// A program that writes each value, written as source, by its spec, one to a line, with a `|` after it so that the
/// padding shows, and what it should write.
let private formats (cases: (string * string * string) list) =
    let source =
        cases
        |> List.map (fun (value, spec, _) -> $"println(format({value}, \"{spec}\") ++ \"|\")")
        |> String.concat "\n"
    runSource (source + "\n") |> assertRan (cases |> List.map (fun (_, _, written) -> written + "|\n") |> String.concat "")

[<Fact>]
let ``Run - an int is written by fill, align, sign, base, grouping and width`` () =
    formats
        [ "42", "", "42"
          "42", "d", "42"
          "42", "05d", "00042"
          "-42", "05", "-0042"
          "42", "+d", "+42"
          "42", " d", " 42"
          "-42", "-", "-42"
          "42", "<5", "42   "
          "42", ">5", "   42"
          "42", "^6", "  42  "
          "42", "*^7", "**42***"
          "-42", "=+6", "-   42"
          "42", "=+6", "+   42"
          "1234567", ",", "1,234,567"
          "1234567", "_", "1_234_567"
          "255", "b", "11111111"
          "255", "o", "377"
          "255", "x", "ff"
          "255", "X", "FF"
          "255", "#b", "0b11111111"
          "255", "#o", "0o377"
          "255", "#x", "0xff"
          "255", "#X", "0XFF"
          "255", "#010x", "0x000000ff"
          "65535", "_x", "ffff"
          "1048575", "_x", "f_ffff"
          "7", ".2f", "7.00"
          "7", "e", "7.000000e+00"
          "7", "%", "700.000000%" ]

[<Fact>]
let ``Run - the widest ints, and a U64 past them, are written in each base`` () =
    formats
        [ "int.maxValue", ",", "9,223,372,036,854,775,807"
          "int.minValue", "d", "-9223372036854775808"
          "int.minValue", "x", "-8000000000000000"
          "int.minValue", "b", "-1000000000000000000000000000000000000000000000000000000000000000"
          "U64.maxValue", "", "18446744073709551615"
          "U64.maxValue", ",", "18,446,744,073,709,551,615"
          "U64.maxValue", "x", "ffffffffffffffff"
          "U64.maxValue", "#X", "0XFFFFFFFFFFFFFFFF"
          "U64.maxValue", "o", "1777777777777777777777"
          "U64.maxValue", "b", String.replicate 64 "1"
          "I8.fromInt(-5)", "03", "-05"
          "U8.fromInt(200)", "x", "c8"
          "I16.fromInt(-300)", "+,", "-300"
          "I32.fromInt(70000)", ",", "70,000"
          "U16.fromInt(65535)", "#x", "0xffff"
          "U32.fromInt(10)", "b", "1010" ]

[<Fact>]
let ``Run - a float is written fixed, with an exponent, the shorter of those, or as a percentage`` () =
    formats
        [ "3.14159", "", "3.14159"
          "3.14159", ".2f", "3.14"
          "3.14159", "10.3f", "     3.142"
          "3.14159", "<10.3f", "3.142     "
          "-12.5", "08.2f", "-0012.50"
          "1234.5", ",.1f", "1,234.5"
          "1234567.891", "_.2f", "1_234_567.89"
          "1.0e16", ",.0f", "10,000,000,000,000,000"
          "1.0e22", ".1f", "10000000000000000000000.0"
          "2.5", ".0f", "3"
          "9.995", ".2f", "9.99"
          "1.5", "#.0f", "2."
          "12345.678", ".3e", "1.235e+04"
          "1234.5678", "e", "1.234568e+03"
          "1234.5678", "10.2E", "  1.23E+03"
          "1.0e300", ".2e", "1.00e+300"
          "0.0", "e", "0.000000e+00"
          "123456789.0", "g", "1.23457e+08"
          "0.0001234", "g", "0.0001234"
          "100.0", "g", "100"
          "1.0", "g", "1"
          "1.0e-300", ".3g", "1e-300"
          "1.0e-5", "g", "1e-05"
          "1.0e-5", "#g", "1.00000e-05"
          "123.456", "G", "123.456"
          "3.14159", "10.3", "      3.14"
          "0.25", ".0%", "25%"
          "0.5", "%", "50.000000%"
          "F32.fromInt(3)", ".3f", "3.000" ]

[<Fact>]
let ``Run - a negative zero, not a number and the infinities are written as Python writes them`` () =
    formats
        [ "-0.0", ".1f", "-0.0"
          "-0.0", "z.1f", "0.0"
          "-0.001", ".2f", "-0.00"
          "-0.001", "z.2f", "0.00"
          "-0.0", "+.1f", "-0.0"
          "0.0", "+.1f", "+0.0"
          "0.0 / 0.0", "f", "nan"
          "0.0 / 0.0", "F", "NAN"
          "1.0 / 0.0", "e", "inf"
          "1.0 / 0.0", "G", "INF"
          "-1.0 / 0.0", "8.2f", "    -inf"
          "1.0 / 0.0", "+", "+inf" ]

[<Fact>]
let ``Run - a string, a char and a bool are padded and cut, counting characters`` () =
    formats
        [ "\"ab\"", "", "ab"
          "\"ab\"", "5", "ab   "
          "\"ab\"", ">5", "   ab"
          "\"ab\"", "*^6", "**ab**"
          "\"abcdef\"", ".3", "abc"
          "\"abcdef\"", ">5.2s", "   ab"
          "\"héllo\"", "^9", "  héllo  "
          "\"héllo\"", ".2", "hé"
          "\"ab\"", "é<4", "abéé"
          "true", ">6", "  true"
          "false", "", "false"
          "char.fromInt(233) ?? crash(\"no\")", "3", "é  " ]

[<Fact>]
let ``Run - an instant is written by strftime's directives in UTC, before 1970 too`` () =
    let source =
        """def at = { text: string ->
  match Instant.fromIsoString(text)
    | #some(i) -> i
    | _ -> crash("not ISO 8601: {text}")
}

println(format(Instant.epoch, "%A %d %B %Y"))
println(format(Instant.epoch, "%F %T %j %p %I %z %%"))
println(format(Instant.epoch, "iso"))
println(format(Instant.epoch, ""))
println(format(at("1969-12-31T23:59:59.25Z"), "%F %T.%f %j %a %A %b %B %y %I%p"))
println(format(at("1900-03-01T13:05:09Z"), "%Y %m %d %H:%M:%S %I %p %j %a"))
println(format(at("2024-12-31T00:00:00Z"), "%j %a, day %d"))
"""
    runSource source
    |> assertRan (
        String.concat
            ""
            [ "Thursday 01 January 1970\n"
              "1970-01-01 00:00:00 001 AM 12 +0000 %\n"
              "1970-01-01T00:00:00Z\n"
              "1970-01-01T00:00:00Z\n"
              "1969-12-31 23:59:59.250000 365 Wed Wednesday Dec December 69 11PM\n"
              "1900 03 01 13:05:09 01 PM 060 Thu\n"
              "366 Tue, day 31\n" ])

[<Fact>]
let ``Run - a spec a type doesn't take is #badFormat from tryFormat, with where and why`` () =
    let source =
        """def show = { result: #some(string) | #badFormat(BadFormat) ->
  match result
    | #some(text) -> println(text)
    | #badFormat(bad) -> println("{bad.of} '{bad.spec}' at {bad.at}: {bad.expected}")
}

show(3.0 \tryFormat(".2q"))
show(3 \tryFormat(".2d"))
show(3 \tryFormat("5..2"))
show(3 \tryFormat(",b"))
show(3 \tryFormat("zd"))
show(3 \tryFormat("s"))
show("x" \tryFormat("+"))
show("x" \tryFormat("=5"))
show("x" \tryFormat("05"))
show("x" \tryFormat(","))
show(true \tryFormat("d"))
show(Instant.epoch \tryFormat("%Q"))
show(Instant.epoch \tryFormat("100%"))
show(3 \tryFormat("1234567890"))
show(3 \tryFormat("5dd"))
show(3 \tryFormat("5;"))
"""
    runSource source
    |> assertRan (
        String.concat
            ""
            [ "float '.2q' at 2: one of `e`, `E`, `f`, `F`, `g`, `G` or `%`\n"
              "int '.2d' at 0: no precision: an integer has no digits after a point\n"
              "int '5..2' at 2: a digit after the `.`\n"
              "int ',b' at 0: `_` between the digits of a number not in decimal\n"
              "int 'zd' at 0: no `z`: an integer has no negative zero\n"
              "int 's' at 0: one of `b`, `d`, `e`, `E`, `f`, `F`, `g`, `G`, `o`, `x`, `X` or `%`\n"
              "string '+' at 0: no sign: text isn't a number\n"
              "string '=5' at 0: `<`, `>` or `^`: text has no sign to pad after\n"
              "string '05' at 0: a width that doesn't start with `0`, or a fill and an align before it\n"
              "string ',' at 0: no grouping: text isn't a number\n"
              "bool 'd' at 0: `s`, or no type\n"
              "Instant '%Q' at 1: a directive such as `%Y`, `%m` or `%d`, or `%%` for a `%`\n"
              "Instant '100%' at 4: a directive such as `%Y`, `%m` or `%d`, or `%%` for a `%`\n"
              "int '1234567890' at 9: a width of at most 9 digits\n"
              "int '5dd' at 2: the end of the spec\n"
              "int '5;' at 1: a type such as `d`, `f` or `s`, or the end of the spec\n" ])

[<Fact>]
let ``Run - format stops the program on a spec the type doesn't take, naming the spec, the type and why`` () =
    let outcome = runSource "println(\"before\")\nprintln(format(3.0, \"q\"))\n"
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("before\n", outcome.StandardOutput)
    Assert.StartsWith(
        "crash: 'q' is no format spec for a 'float': at 0, it could have one of `e`, `E`, `f`, `F`, `g`, `G` or `%`\n",
        outcome.StandardError)

[<Fact>]
let ``Run - float.format is format's shorthand, and writes not a number and the infinities as toString does`` () =
    runSource
        """println(float.format(2.5, decimals = 0))
println(float.format(-1.875, decimals = 2))
println(float.format(-0.001, decimals = 2))
println(float.format(1.005, decimals = 2))
println(float.format(3.0, decimals = -1))
println(float.format(0.0 / 0.0, decimals = 2))
println(float.format(-1.0 / 0.0, decimals = 2))
"""
    |> assertRan "3\n-1.88\n0.00\n1.00\n3\nNaN\n-Infinity\n"

[<Fact>]
let ``Run - a unit quantity is written in the unit its spec names after a space, or else in the one toString picks`` () =
    runSource
        """dimension Length(float)
@baseUnit unit m: Length
unit km: Length = 1000m
unit cm: Length = 0.01m

println(format(1500ms, ".1f ms"))
println(format(1500ms, ".2f"))
println(format(1500ms, ""))
println(format(1500ms, "8.3f s") ++ "|")
println(format(-3ms, "+.2f"))
println(format(0ms, ".1f"))
println(format(1000000000ns, ",.0f ns"))
println(format(2.5km, ".1f m"))
println(format(2.5km, ""))
println(format(0.25m, ".0f cm"))
"""
    |> assertRan "1500.0ms\n1.50s\n1.5s\n   1.500s|\n-3.00ms\n0.0s\n1,000,000,000ns\n2500.0m\n2.5km\n25cm\n"

[<Fact>]
let ``Run - a derived dimension is written in its own units or its parts' display units, and a sized storage as a float`` () =
    runSource
        """dimension Length(float)
@baseUnit unit m: Length
unit km: Length = 1000m
dimension Speed = Length / Time
unit kmh: Speed = 1km / 1hr
dimension Count(I16)
unit things: Count

println(format(36.0km / 1hr, ".1f"))
println(format(36.0km / 1hr, ".1f kmh"))
println(format(I16.fromInt(12) * 1things, "03.0f things"))
"""
    |> assertRan "10.0m/s\n36.0kmh\n012things\n"

[<Fact>]
let ``Run - a unit its dimension doesn't have, and a spec a float doesn't take, are #badFormat for a unit quantity`` () =
    runSource
        """dimension Length(float)
@baseUnit unit m: Length
unit km: Length = 1000m

def show: [Format(a), Console] a, string -> () = { value, spec ->
  match tryFormat(value, spec)
    | #some(text) -> println(text)
    | #badFormat(bad) -> println("{bad.of} '{bad.spec}' at {bad.at}: {bad.expected}")
}

show(1500ms, ".1f parsec")
show(2.5km, ".1f s")
show(1500ms, ".1d ms")
show(1500ms, "x")
"""
    |> assertRan (
        String.concat
            ""
            [ "Time '.1f parsec' at 4: the name of a unit of Time: `d`, `hr`, `min`, `s`, `ms`, `us` or `ns`\n"
              "Length '.1f s' at 4: the name of a unit of Length: `km` or `m`\n"
              "Time '.1d ms' at 2: one of `e`, `E`, `f`, `F`, `g`, `G` or `%`\n"
              "Time 'x' at 0: one of `e`, `E`, `f`, `F`, `g`, `G` or `%`\n" ])

[<Fact>]
let ``Run - the prelude reads each spec of the shared table as the compiler does`` () =
    let literal (text: string) = "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("{", "\\{") + "\""
    let source =
        "def show: [Format(a), Console] a, string -> () = { value, spec ->\n"
        + "  match tryFormat(value, spec)\n"
        + "    | #some(_) -> println(\"takes\")\n"
        + "    | #badFormat(bad) -> println(\"{bad.at}|{bad.expected}\")\n"
        + "}\n\n"
        + (FormatSpecTable.rows
           |> List.map (fun (_, value, spec, _) -> $"show({value}, {literal spec})")
           |> String.concat "\n")
        + "\n"
    let written (outcome: FormatSpecTable.Outcome) =
        match outcome with
        | FormatSpecTable.Takes -> "takes\n"
        | FormatSpecTable.Rejects(at, expected) -> $"{at}|{expected}\n"
    runSource source |> assertRan (FormatSpecTable.rows |> List.map (fun (_, _, _, outcome) -> written outcome) |> String.concat "")

[<Fact>]
let ``Run - a hole with a spec writes its value by it, and one without uses ToString`` () =
    runSource
        """def price = 3.14159
def n = -1234567
println("{price:.2f}|{price:>9.3f}|{n:,}|{n:=+12_}|{255:#x}|{"ab":*^6}|{true:>5}|{price}")
println("{Instant.epoch:%Y-%m-%d}|{n:}|{[1, 2] \list.length:03}|{price * 2.0:e}")
"""
    |> assertRan "3.14|    3.142|-1,234,567|-  1_234_567|0xff|**ab**| true|3.14159\n1970-01-01|-1234567|002|6.283180e+00\n"
