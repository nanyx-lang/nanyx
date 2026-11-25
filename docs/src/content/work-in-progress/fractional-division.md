---
title: "Proposal: Fractional division"
description: "/ divides with a fraction on every number type, and // divides to a whole number"
order: 9
---

> **Status: settled, not built.** The code on this page is proposed syntax: today `/` on two ints truncates, and the
> compiler does not read `//`.

`/` will divide with a fraction on every number type, so two ints divide to a `float`, and a new operator, `//`, will
divide to a whole number. `%` goes with `//`:

```nanyx fragment
def ratio = 7 / 2 -- 3.5
def pages = 7 // 2 -- 3
def left = 7 % 2 -- 1
def below = -7 // 2 -- -4: floored
def wrap = -7 % 2 -- 1: the sign of the right operand
```

Asking for a fraction is the common case, and writing `toFloat(x) / toFloat(y)` for it would be the price of the rare
one. It is also what [units](./units-and-dimensions#arithmetic) need: `t / 1s` reads a time in seconds with the
fraction, and `t // 1s` counts the whole seconds in it, on a time stored as an `int` as on one stored as a `float`.

# `/`

`x / y` divides with a fraction. On `float` and `F32` it is what it is today. On `int` and the sized integers, `I8` to
`U64`, it converts both operands to `float` and divides, so the result is a `float`: `1 / 3` is `0.333...`, and
`1 / 0` is infinity, as `1.0 / 0.0` is. An `I64` or `U64` past 2^53 is rounded to the nearest `float` first, as any
conversion to `float` rounds it.

The type a number type's `/` gives is an associated type of `Num`, `fraction`
([Associated types](../advanced/contexts#associated-types)):

| `a` | `fraction` |
|---|---|
| `int`, `I8` to `U64` | `float` |
| `float` | `float` |
| `F32` | `F32` |

A generic function's division has that type:

```nanyx fragment
def mean: [Num(a)] list(a) -> fraction = { xs -> list.sum(xs) / fromInt(xs.length) }

def m = mean([1, 2, 4]) -- 2.333..., a float
```

# `//` and `%`

`x // y` divides and rounds down, toward negative infinity, and `x % y` is what is left, with the sign of `y`, so that
`x == (x // y) * y + x % y` always. On `int`, `-7 // 2` is `-4` and `-7 % 2` is `1`; today `-7 / 2` is `-3` and
`-7 % 2` is `-1`. On `float` and `F32`, `//` is the floor of the quotient, as a float, and `%` follows it, as Python's
do: `-7.5 // 2.0` is `-4.0` and `-7.5 % 2.0` is `0.5`. On an integer type, `x // 0` and `x % 0` stop the program, as
`x / 0` does today.

Floored, a negative number still splits into a whole part and a remainder that is not negative, which is what reading a
time or an index by a unit wants: `-1500ms // 1s` is `-2` and `-1500ms % 1s` is `500ms`. WebAssembly's integer
division truncates (`i64.div_s`, `i64.rem_s`), so on integers the compiler adjusts its result when the remainder is
not zero and the operands' signs differ: a compare and a branch.

`math.mod`, the modulo with the sign of its second argument, is what `%` becomes, so it goes.

`//` is one token, of the same precedence as `*`, `/` and `%`, grouping to the left. It is not a comment, as Nanyx's
are `--`.

# `Num`

`//` is a member of `Num`, beside `/` and `%`, and `/` gives `fraction`:

```nanyx fragment
context Num(a) = (
  fraction: type
  zero: a
  `+`: (a, a) -> a
  `-`: (a, a) -> a
  negate: a -> a
  `*`: (a, a) -> a
  `/`: (a, a) -> fraction
  `//`: (a, a) -> a
  `%`: (a, a) -> a
  fromInt: int -> a
  isInteger: bool
)
```

Only `Num` has `/`, `//` and `%`, so each is read through it
([Operators find their context](../advanced/contexts#operators-find-their-context)), and no other prelude context ties
with it (NYX0006). A function that does not write its type and divides ints with `/` is inferred to return a `float`.

`x ** n` keeps its type, `[Num(a)] (a, int) -> a`, so `2 ** 10` is still the `int` 1024, and `isInteger` stays for it:
a negative exponent on an integer type still stops the program, and its message points to `1 / x ** n`, which now
gives the fraction.

# Changing it

This is a breaking change, made in one release with no warning period. Most code that it changes stops compiling, as
an `int` result is wanted where a `float` now comes: `xs[n / 2]`, or `n / 2` passed to an `int` parameter. Some
compiles still and means something else, which the release's own changes to the repository must find by reading:

```nanyx fragment
println("{n / 2}") -- printed 2 for n = 5, now 2.5
if n / 2 == 3 then ... -- true for n = 6 or 7, now only for 6
def back = -7 % 2 -- -1, now 1
```

The PR that builds it:

- reads every `/` and `%` on integers in `stdlib/`, `packages/`, `examples/` and the docs' examples, and makes each `//`
  or keeps it as it meant;
- updates [Num(a)](../advanced/contexts#numa) (its `mean`, and what `/` and `%` do on `int`),
  [Operators](../tour/operators), [Sized numbers](../advanced/sized-numbers) and `Num`'s doc comment in
  `prelude-numbers.nyx`, regenerates the standard library's pages, and deletes `math.mod`;
- says in the release's changelog that `int / int` is a `float`, that `//` divides to a whole number, and that `%` on a
  negative number has changed.

Done when the checker and Wasm tests cover `/`, `//` and `%` on `int`, a sized integer, `float` and `F32`, with
negative operands, `fraction` in a generic function, and an inferred function that divides ints, and this page is
deleted for a description in the docs.
