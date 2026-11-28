---
title: prelude
description: What every module sees without importing anything
order: 2
---

The prelude, `nanyx/prelude`, is imported into every module, so what it defines needs no import. It is written across several files, one for each topic, and each has a page here:

- this page: `crash`, `ignore`, `bool.fromString` and `memory`
- [contexts](prelude-contexts): equality, ordering, hashing, joining, and converting to and from text
- [numbers](prelude-numbers): arithmetic on `int` and `float`, powers, bitwise functions on ints, and reading floats
- [sized numbers](prelude-sized-numbers): arithmetic on `I8` to `U64` and `F32`
- [chars](prelude-chars): a `char`'s code point, its text, and whether it is a digit, a letter or whitespace
- [time](prelude-time): the `Time` dimension and its units, `ns` to `d`
- [instant](prelude-instant): `Instant`, a point in time, and ISO 8601
- [format](prelude-format): writing a value by a format spec, `format(3.14159, ".2f")`
- [conversions](prelude-conversions): between `int`, `float` and the sized number types
- [lists](prelude-lists): functions on `list(a)`
- [sequences](prelude-sequences): lazy sequences, `seq(a)`, and ranges
- [maps and sets](prelude-maps): functions on `map(k, v)` and `set(a)`
- [arrays](prelude-arrays): `array.at`, which calling an array is
- [results and options](prelude-results): `proc`, and the helpers for option-like unions

# Discarding a value

`ignore` is for a result you don't need, without binding it to a name you never use. It is what the [official linter](../standard-tools/official-linter)'s fix for an unused result writes:

```nanyx fragment
ignore(readLine())
```

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

What every module sees without importing anything.

The compiler imports this module, unqualified, into every other module, the rest of the standard
library included. A module's own definitions and its explicit imports take precedence over the
names here, so a module can define its own `proc`. It is written across this file and the files
beside it that start `@partial module prelude`, one for each topic: lists, maps and sets, numbers
and so on.

## `crash`

```nanyx fragment
def crash: string -> undefined
```

Stops the program: writes `crash: ` and `message` on a line to standard error, then the stack, a
line for each function the program was in, innermost first, and exits with code 1. It never
returns, so its result is `undefined` and a call fits wherever a value of any type is expected:
`if n > 0 then n else crash("n must be positive")` is an `int`.

## `orCrash`

```nanyx fragment
def orCrash: #some(a) | r, string -> a
```

The value in `#some(v)`, or, for anything else, stops the program with `message` as [`crash`](#crash) does.
It unwraps an option or a result where a missing value is a bug, and reads well at the end of a
pipeline: `token \float.fromString \orCrash("Invalid number: '{token}'")`.

## `ignore`

```nanyx fragment
def ignore: a -> ()
```

Throws its argument away and gives back `()`, for a result that isn't needed. It reads well at
the end of a pipeline: `items \list.map(process) \ignore`.

## `bool.fromString`

```nanyx fragment
def bool.fromString: string -> #some(bool) | #invalidBool
```

The boolean `s` writes, `true` or `false` exactly as [`bool.toString`](prelude-contexts#booltostring) writes them, or
`#invalidBool` for anything else (other capitalisation, spaces, an empty string).

## `Memory`

```nanyx fragment
scoped context Memory = protected ()
```

The context mutation needs: a cell can be declared with `mut` only where it is, and a lambda that uses a cell
needs it too. It is nominal, so `memory` is the only way to get one, and scoped, so nothing that needs it
can leave the `memory` block that supplies it.

## `memory`

```nanyx fragment
def memory: [c] ([Memory, c] () -> a) -> a
```

Runs `block` with `Memory`, so that it can declare cells with `mut` and change them with `:=`, and
gives back its result. Nothing that uses a cell can leave the block: only what the cells hold. The block can
name its `Memory`, `memory { context m -> ... }`, and `use m` in an inner block makes the cells after it belong
to this one. When it ends, it frees the references made while it was the innermost block running, unless a
value stored into an outer block's reference, or its result, moved them out, so a cycle through one is
freed too.

<!-- End of the generated reference. -->
