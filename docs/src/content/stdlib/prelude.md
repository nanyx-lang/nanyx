---
title: prelude
description: What every module sees without importing anything
order: 2
---

The prelude, `nanyx/prelude`, is imported into every module, so what it defines needs no import. It is written across several files, one for each topic, and each has a page here:

- this page: `panic`, `ignore`, `bool.fromString` and `memory`
- [contexts](prelude-contexts): equality, ordering, hashing, joining, and converting to and from text
- [numbers](prelude-numbers): arithmetic on `int` and `float`, powers, and reading floats
- [sized numbers](prelude-sized-numbers): arithmetic on `I8` to `U64` and `F32`
- [conversions](prelude-conversions): between `int`, `float` and the sized number types
- [lists](prelude-lists): functions on `list(a)`
- [maps and sets](prelude-maps): functions on `map(k, v)` and `set(a)`
- [results and options](prelude-results): `Result(a, e)`, `proc`, and the helpers for option-like unions

# Discarding a value

`ignore` is for a result you don't need, without binding it to a name you never use. It is what the [official linter](../standard-tools/official-linter)'s fix for an unused result writes:

```nanyx fragment
ignore(readLine())
```

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ by `nanyx stdlib-docs`: edit those, not this. -->

## `panic`

```nanyx fragment
def panic: string -> undefined
```

Stops the program: writes `panic: ` and `message` on a line to standard error, and exits with
code 1. It never returns, so its result is `undefined` and a call fits wherever a value of any
type is expected: `if n > 0 then n else panic("n must be positive")` is an `int`.

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
def memory: [c] ([Memory & c] Memory -> a) -> a
```

Runs `block` with `Memory`, so that it can declare cells with `mut` and change them with `:=`, and
gives back its result. Nothing that uses a cell can leave the block: only what the cells hold. The block is given
its instance, `memory { m -> ... }`, and `use m` in an inner block makes the cells after it belong to this one.

<!-- End of the generated reference. -->
