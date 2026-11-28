/// One table of format specs, each with a value it is written for and whether that value's type takes it. The
/// compiler's `FormatSpecs`, which checks a spec written in a hole as the program compiles, and the prelude's `fmtSpec`
/// and `tryFormat`s, which check one as it runs, are both tested against it, so that a change to one reader that the
/// other doesn't make fails a test.
module FormatSpecTable

/// Whether a value takes a spec, or, if not, the byte of the spec it stops being one at and what it could have there.
type Outcome =
    | Takes
    | Rejects of at: int * expected: string

/// A value's type, as Nanyx writes it, the value as source, a spec, and what the type says of the spec.
let rows: (string * string * string * Outcome) list =
    let integerKinds = "one of `b`, `d`, `e`, `E`, `f`, `F`, `g`, `G`, `o`, `x`, `X` or `%`"
    let floatKinds = "one of `e`, `E`, `f`, `F`, `g`, `G` or `%`"
    let directive = "a directive such as `%Y`, `%m` or `%d`, or `%%` for a `%`"
    [ "int", "42", "", Takes
      "int", "42", "d", Takes
      "int", "42", "*^+z", Rejects(3, "no `z`: an integer has no negative zero")
      "int", "42", "<+#010,d", Takes
      "int", "42", "#_b", Takes
      "int", "42", "#_x", Takes
      "int", "42", ",x", Rejects(0, "`_` between the digits of a number not in decimal")
      "int", "42", ".2d", Rejects(0, "no precision: an integer has no digits after a point")
      "int", "42", ".2f", Takes
      "int", "42", "e", Takes
      "int", "42", "%", Takes
      "int", "42", "s", Rejects(0, integerKinds)
      "int", "42", "5..2", Rejects(2, "a digit after the `.`")
      "int", "42", "1234567890", Rejects(9, "a width of at most 9 digits")
      "int", "42", ".1234567890f", Rejects(10, "a precision of at most 9 digits")
      "int", "42", "5dd", Rejects(2, "the end of the spec")
      "int", "42", "5;", Rejects(1, "a type such as `d`, `f` or `s`, or the end of the spec")
      "int", "42", "é>5", Takes
      "int", "42", "é>5;", Rejects(4, "a type such as `d`, `f` or `s`, or the end of the spec")
      "I8", "I8.fromInt(-5)", "03", Takes
      "U64", "U64.maxValue", "#X", Takes
      "U64", "U64.maxValue", "z", Rejects(0, "no `z`: an integer has no negative zero")
      "float", "1.5", "", Takes
      "float", "1.5", "+z#012_.3e", Takes
      "float", "1.5", "=+8.2%", Takes
      "float", "1.5", "g", Takes
      "float", "1.5", "d", Rejects(0, floatKinds)
      "float", "1.5", ".2q", Rejects(2, floatKinds)
      "float", "1.5", ".", Rejects(1, "a digit after the `.`")
      "F32", "F32.fromInt(3)", ".3f", Takes
      "F32", "F32.fromInt(3)", "x", Rejects(0, floatKinds)
      "string", "\"ab\"", "", Takes
      "string", "\"ab\"", "*^6.1s", Takes
      "string", "\"ab\"", "0<5", Takes
      "string", "\"ab\"", "d", Rejects(0, "`s`, or no type")
      "string", "\"ab\"", "=5", Rejects(0, "`<`, `>` or `^`: text has no sign to pad after")
      "string", "\"ab\"", "+", Rejects(0, "no sign: text isn't a number")
      "string", "\"ab\"", "z", Rejects(0, "no `z`: text isn't a number")
      "string", "\"ab\"", "#", Rejects(0, "no `#`: text isn't a number")
      "string", "\"ab\"", "05", Rejects(0, "a width that doesn't start with `0`, or a fill and an align before it")
      "string", "\"ab\"", ",", Rejects(0, "no grouping: text isn't a number")
      "char", "char.fromInt(97) ?? crash(\"no\")", "^3", Takes
      "bool", "true", ">6", Takes
      "bool", "true", "x", Rejects(0, "`s`, or no type")
      "Instant", "Instant.epoch", "", Takes
      "Instant", "Instant.epoch", "iso", Takes
      "Instant", "Instant.epoch", "%Y-%m-%d %H:%M:%S.%f %j %a %A %b %B %y %I%p %z %F %T %%", Takes
      "Instant", "Instant.epoch", "%Q", Rejects(1, directive)
      "Instant", "Instant.epoch", "100%", Rejects(4, directive)
      "Instant", "Instant.epoch", "isoish", Takes
      "Time", "1500ms", "", Takes
      "Time", "1500ms", ".1f ms", Takes
      "Time", "1500ms", "+08.2f", Takes
      "Time", "1500ms", " ms", Takes
      "Time", "1500ms", "*^9.1e s", Takes
      "Time", "1500ms", ".1f parsec", Rejects(4, "the name of a unit of Time: `d`, `hr`, `min`, `s`, `ms`, `us` or `ns`")
      "Time", "1500ms", ".1d ms", Rejects(2, floatKinds)
      "Time", "1500ms", "x", Rejects(0, floatKinds)
      "Time", "1500ms", ".1f ms ", Rejects(3, "the end of the spec")
      "Time", "1500ms", ".1f s/ms", Rejects(4, "units that make Time: `s/ms` makes a number")
      "Time", "1500ms", ".1f s*s", Rejects(4, "units that make Time: `s*s` makes Time * Time")
      "Time * Time", "1500ms * 2s", ".1f s*s", Takes
      "Time * Time", "1500ms * 2s", "8.3e ms*s", Takes
      "Time * Time", "1500ms * 2s", ".1f s", Rejects(4, "units that make Time * Time: `s` makes Time")
      "Time * Time", "1500ms * 2s", ".1f s/s", Rejects(4, "units that make Time * Time: `s/s` makes a number")
      "Time * Time", "1500ms * 2s", ".1f s*parsec", Rejects(6, "the name of a unit of Time * Time: `d`, `hr`, `min`, `s`, `ms`, `us` or `ns`")
      "Time * Time", "1500ms * 2s", ".1f s**s", Rejects(6, "the name of a unit of Time * Time: `d`, `hr`, `min`, `s`, `ms`, `us` or `ns`")
      "Time * Time", "1500ms * 2s", ".1f s*", Rejects(6, "the name of a unit of Time * Time: `d`, `hr`, `min`, `s`, `ms`, `us` or `ns`") ]
