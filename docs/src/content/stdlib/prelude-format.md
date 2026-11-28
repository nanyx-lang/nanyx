---
title: "prelude: format"
description: Writing a value by a format spec, as Python's format specs do
order: 5.9
---

`format(value, spec)` writes a value as a format spec says: how many digits, what padding, which base, which parts of a
date. A spec is a short string after Python's format-spec mini-language, and each type that can be formatted has an
instance of the [`Format`](#format) context, which reads it. It is in the [prelude](prelude), so no import is needed.

```nanyx
println(format(3.14159, ".2f"))
println(format(1234567, ","))
println(format(255, "#x"))
println(format(0.25, ".0%"))
println("[" ++ format("total", "^9") ++ "]")
println(format(Instant.epoch, "%A %d %B %Y"))

-- 3.14
-- 1,234,567
-- 0xff
-- 25%
-- [  total  ]
-- Thursday 01 January 1970
```

A hole in a string can write its value by a spec, written after a `:` (see [Strings](../getting-started/basics#strings)).
The compiler checks a spec written there against the value's type, so one the type doesn't take is an error as the
program compiles, [NYX0307](../standard-tools/errors#nyx0307), not as it runs:

```nanyx
def price = 3.14159
def count = 1234567
println("{price:.2f} {count:,} {count:#x} {Instant.epoch:%Y-%m-%d}")

-- 3.14 1,234,567 0x12d687 1970-01-01
```

Called as a function, a spec a value's type doesn't take stops the program, with a message that names the spec, the type and where in the spec
it goes wrong. Where the spec is only known as the program runs, `x \tryFormat(spec)` gives `#badFormat` with the same
reason instead:

```nanyx
match 3.0 \tryFormat(".2q")
  | #some(text) -> println(text)
  | #badFormat(bad) -> println("at {bad.at}: {bad.expected}")

-- at 2: one of `e`, `E`, `f`, `F`, `g`, `G` or `%`
```

The prelude has an instance for `int`, `float`, the [sized numbers](prelude-sized-numbers), `string`, `char`, `bool`
and [`Instant`](prelude-instant), and the compiler makes one for each [unit quantity](../advanced/units-of-measure),
which names the unit to write it in after a space:

```nanyx
println(format(1500ms, ".1f ms"))
println(format(1500ms, ".2f s"))
println(format(1500ms, ".2f"))

-- 1500.0ms
-- 1.50s
-- 1.50s
```

A type of your own gets one by attaching a `tryFormat`, as it gets a `ToString` by
attaching a `toString`.

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

## `BadFormat`

```nanyx fragment
type BadFormat = (spec: string, at: int, expected: string, of: string)
```

Why `spec` is no format spec for a value of the type `of`: the byte of `spec` at which it stops
being one, counting from 0, and what it could have there.

## `Format`

```nanyx fragment
context Format(a) = (
  tryFormat: a, string -> #some(string) | #badFormat(BadFormat)
)
```

How a value of type `a` is written by a format spec, a string that says how: `format(3.14159,
".2f")` is `"3.14"`, and `format(Instant.epoch, "%Y-%m-%d")` is `"1970-01-01"`. Its one member
gives `#badFormat` for a spec the type doesn't take; [`format`](#format-1) writes the value, or stops the
program with the reason.

The prelude has an instance for `int`, `float`, the sized numbers, `string`, `char`, `bool` and
`Instant`, and the compiler makes one for each unit quantity. The numbers, strings, chars and bools take Python's format-spec mini-language: `[[fill]
align][sign][z][#][0][width][grouping][.precision][type]`, where

- `align` is `<` (left), `>` (right), `^` (centred) or `=` (padded after the sign), with the
  `fill` before it, any one character, a space when none is written;
- `sign` is `+` (a sign on every number), `-` (on a negative one, as without it) or a space (a
  space before a number that isn't negative);
- `z` writes a negative zero, or a negative number rounded to zero, without its sign;
- `#` writes the base of a binary, octal or hexadecimal number, `0b`, `0o` and `0x`, and always
  writes a float's point;
- `0` pads a number with zeros after its sign, as `0=` does;
- `width` is the fewest characters the text has, padded to it;
- `grouping` is `,` or `_`, written between each three digits of a number's whole part, or `_`
  between each four of a binary, octal or hexadecimal one;
- `precision` is a float's digits after its point (`f`, `e`, `%`) or its significant digits
  (`g`), or the most characters of a string, which is cut to them;
- `type` is how a number is written: `d` in decimal, `b` in binary, `o` in octal, `x` and `X` in
  hexadecimal, `e` and `E` with an exponent, `f` and `F` with a fixed point, `g` and `G` the
  shorter of those two, `%` as a percentage. An integer takes them all, and is written as a float
  by `e`, `f`, `g` and `%`. A float takes `e` to `%`. A string, a char and a bool take `s`.

A number is right-aligned and text left-aligned unless `align` says otherwise. A float is
written to about 15 significant digits, as it is held, with the digits after those zeros, and is
rounded with halves away from zero. Not a number is `nan`, and the infinities `inf` and `-inf`
(`NAN` and `INF` for `E`, `F` and `G`). The empty spec writes a value as interpolation does.

An `Instant` takes `strftime`'s directives, in UTC, with English names (see [`Instant.tryFormat`](#instanttryformat)).

A unit quantity, a value of a dimension stored as an `int`, a float or a sized integer narrower
than 64 bits, takes a float's spec, then the name of one of its dimension's units after a space:
its number is written in that unit by the spec, then the unit, so `format(1500ms, ".1f s")` is
`"1.5s"`. With no unit named, it is written in the unit its `toString` writes it in, so
`format(1500ms, ".2f")` is `"1.50s"`. The width and the padding are the number's.

A hole in a string writes its value by a spec written after a `:`, `"{price:.2f}"`, and the
compiler checks the spec against the value's type, for a type the prelude formats and a unit
quantity, whose units it knows.

## `format`

```nanyx fragment
def format: [Format(a)] a, string -> string
```

`value` written by `spec` (see [`Format`](#format)): `format(1234.5, ",.1f")` is `"1,234.5"`, and
`format(255, "#x")` is `"0xff"`. A spec the value's type doesn't take stops the program, with a
message that names the spec, the type and where the spec goes wrong. The type's `tryFormat`, as
`x \tryFormat(spec)` calls it, gives that as `#badFormat` instead. A hole with a spec, `"{x:.2f}"`,
writes its value as `format` does.

## `int.tryFormat`

```nanyx fragment
def int.tryFormat: int, string -> #some(string) | #badFormat(BadFormat)
```

`n` written by `spec` (see [`Format`](#format)): `format(42, "05d")` is `"00042"`, `format(255, "#x")`
is `"0xff"`, `format(1234567, ",")` is `"1,234,567"`, and `format(7, ".2f")` is `"7.00"`.

## `I8.tryFormat`

```nanyx fragment
def I8.tryFormat: I8, string -> #some(string) | #badFormat(BadFormat)
```

`n` written by `spec`, as an `int` is (see [`int.tryFormat`](#inttryformat)).

## `I16.tryFormat`

```nanyx fragment
def I16.tryFormat: I16, string -> #some(string) | #badFormat(BadFormat)
```

`n` written by `spec`, as an `int` is (see [`int.tryFormat`](#inttryformat)).

## `I32.tryFormat`

```nanyx fragment
def I32.tryFormat: I32, string -> #some(string) | #badFormat(BadFormat)
```

`n` written by `spec`, as an `int` is (see [`int.tryFormat`](#inttryformat)).

## `U8.tryFormat`

```nanyx fragment
def U8.tryFormat: U8, string -> #some(string) | #badFormat(BadFormat)
```

`n` written by `spec`, as an `int` is (see [`int.tryFormat`](#inttryformat)).

## `U16.tryFormat`

```nanyx fragment
def U16.tryFormat: U16, string -> #some(string) | #badFormat(BadFormat)
```

`n` written by `spec`, as an `int` is (see [`int.tryFormat`](#inttryformat)).

## `U32.tryFormat`

```nanyx fragment
def U32.tryFormat: U32, string -> #some(string) | #badFormat(BadFormat)
```

`n` written by `spec`, as an `int` is (see [`int.tryFormat`](#inttryformat)).

## `U64.tryFormat`

```nanyx fragment
def U64.tryFormat: U64, string -> #some(string) | #badFormat(BadFormat)
```

`n` written by `spec`, as an `int` is (see [`int.tryFormat`](#inttryformat)), and past the largest `int` too:
`format(U64.maxValue, "x")` is `"ffffffffffffffff"`.

## `float.tryFormat`

```nanyx fragment
def float.tryFormat: float, string -> #some(string) | #badFormat(BadFormat)
```

`x` written by `spec` (see [`Format`](#format)): `format(3.14159, ".2f")` is `"3.14"`, `format(1234.5,
",.1f")` is `"1,234.5"`, `format(0.25, ".0%")` is `"25%"`, `format(12345.678, ".3e")` is
`"1.235e+04"`, and `format(-0.0, "z.1f")` is `"0.0"`.

## `F32.tryFormat`

```nanyx fragment
def F32.tryFormat: F32, string -> #some(string) | #badFormat(BadFormat)
```

`x` written by `spec`, as a `float` is (see [`float.tryFormat`](#floattryformat)).

## `string.tryFormat`

```nanyx fragment
def string.tryFormat: string, string -> #some(string) | #badFormat(BadFormat)
```

`s` written by `spec` (see [`Format`](#format)): `format("ab", ">5")` is `"   ab"`, `format("ab", "*^6")`
is `"**ab**"`, and `format("abcdef", ".3")` is `"abc"`. Width and precision count characters.

## `char.tryFormat`

```nanyx fragment
def char.tryFormat: char, string -> #some(string) | #badFormat(BadFormat)
```

`c` written by `spec`, as a string of it is (see [`string.tryFormat`](#stringtryformat)).

## `bool.tryFormat`

```nanyx fragment
def bool.tryFormat: bool, string -> #some(string) | #badFormat(BadFormat)
```

`b` written by `spec`, as `"true"` or `"false"` is (see [`string.tryFormat`](#stringtryformat)): `format(true,
">6")` is `"  true"`.

## `Instant.tryFormat`

```nanyx fragment
def Instant.tryFormat: Instant, string -> #some(string) | #badFormat(BadFormat)
```

`i` written by `spec`, in UTC: `iso` writes it as [`Instant.toIsoString`](prelude-instant#instanttoisostring) does, and any other
spec is text with `strftime`'s directives in it, each a `%` and a letter: `%Y` the year, at least
four digits; `%y` its last two; `%m` the month, `01` to `12`; `%d` the day, `01` to `31`; `%j` the
day of the year, `001` to `366`; `%H` the hour, `00` to `23`; `%I` the hour on a 12-hour clock,
`01` to `12`, and `%p` `AM` or `PM`; `%M` the minute; `%S` the second; `%f` the microseconds,
six digits; `%a` and `%A` the day of the week, `Mon` and `Monday`; `%b` and `%B` the month, `Jan`
and `January`; `%z` the offset from UTC, always `+0000`; `%F` the date, `%Y-%m-%d`; `%T` the
time, `%H:%M:%S`; and `%%` a `%`. The names are English. `format(Instant.epoch, "%A %d %B %Y")`
is `"Thursday 01 January 1970"`.

<!-- End of the generated reference. -->
