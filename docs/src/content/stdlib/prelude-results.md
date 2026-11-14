---
title: "prelude: results and options"
description: The Result type, the proc workflow, and helpers for option-like unions
order: 9
---

The `Result(a, e)` type and its functions, the `proc` workflow, and the helpers for option-like unions. They are in the [prelude](prelude), so no import is needed. How to handle errors with them is in the tour's [Error handling](../tour/error-handling).

# Results

`Result(a, e)` is `#some(a) | #error(e)`: a success or an error. Its functions are attached to `Result`, and called as `Result.map(r, f)` or `r \Result.map(f)`.

```nanyx
import nanyx/console

def parse: string -> Result(int, string) = { s ->
  int.fromString(s) \someToResult("not a number: {s}")
}

def half: int -> Result(int, string) = { n ->
  if n % 2 == 0 then #some(n / 2) else #error("odd: {n}")
}

println(parse("42") \Result.bind(half))
println(parse("7") \Result.bind(half))
-- #some(21)
-- #error(odd: 7)
```

# Options

There is no `Option` type. Any union that includes `#some(a)` is [option-like](../advanced/option-like-types): `#some(a) | #none` is the most common, and `int.fromString` gives a `#some(int) | #invalidInt`. These helpers keep every tag but `#some` as it is, whatever it is. They are plain functions rather than definitions attached to a type, because there is no `Option` type for them to be attached to, and they are named for the `#some` tag they act on, as `mapSome`, `someOr` and `somes` are.

```nanyx
import nanyx/console

println(["1", "x", "3"] \list.map(int.fromString) \somes)
println(int.fromString("x") \mapSome { * 2 } \someOr(0))
-- [1, 3]
-- 0
```

The [`??` operator](../tour/error-handling#falling-back-with-) is `someOrElse`: `option ?? fallback` is `someOrElse(option, { fallback })`, and the fallback is worked out only when it is needed.

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ by `nanyx stdlib-docs`: edit those, not this. -->

## `Result`

```nanyx fragment
type Result(a, e) = #some(a) | #error(e)
```

A success carrying an `a`, or an error carrying an `e`.

## `proc`

```nanyx fragment
def proc: (for: ([d] (list(x), ([d] (x, ([d] () -> #some(y) | s)) -> #some(y) | s), ([d] () -> #some(y) | s)) -> #some(y) | s), return: (a -> #some(a) | r), try: ([c] (#some(a) | r, ([c] a -> #some(b) | r)) -> #some(b) | r), zero: #some() | r)
```

The result workflow: inside `proc { ... }`, `try x` continues with the payload of `#some`, and
gives back any `#error` as is without running the rest of the block, and `return x` wraps the
value in `#some`.

```nanyx
def total = proc {
  def customer = try getCustomer(5)
  def order = try getLastOrder(customer)
  return order.total
}
```

A workflow builder is a record of keyword members. A block written after the builder's name
has each `try x` (and each `try x` nested inside an expression) turned into a call to the
builder's `try` member, with the rest of the block as its continuation, and each `return x`
into a call to its `return` member. A block that ends without `return` has `zero` as its
value, `#some()`, so a `proc` that is only run for what it does needs no `return`.

Every tag other than `#some` is a failure, not only `#error`, and the block collects them: a
`try` on a `#some(string) | #nil` and another on a `#some(int) | #notFound` make the block a
`#some(...) | #nil | #notFound`. (Two failures with the same tag must agree on its payload:
`#error(#a)` and `#error(#b)` do not combine into `#error(#a | #b)`.)

A `for` loop over a list whose body uses `try` or `return` is run by `for`, item by item. A
`try` that fails in the body ends the whole block with that failure, and a `return` ends it
with its value, as they do outside the loop: the items after it, and what follows the loop,
don't run.

## `Result.map`

```nanyx fragment
def Result.map: [c] Result(a, e), ([c] a -> b) -> Result(b, e)
```

Applies `f` to a success, and leaves an error as it is.

## `Result.mapError`

```nanyx fragment
def Result.mapError: [c] Result(a, e), ([c] e -> f) -> Result(a, f)
```

Applies `f` to an error, and leaves a success as it is.

## `Result.bind`

```nanyx fragment
def Result.bind: [c] Result(a, e), ([c] a -> Result(b, e)) -> Result(b, e)
```

Continues with `f` on a success. Both steps have the same error type: to chain a step whose
errors differ, bring them to one type with [`Result.mapError`](#resultmaperror) first. That holds for errors that
are tags too: the error is one type, so `#error(#notFound)` and `#error(#invalidInt)` do not
chain, as the other tags of [`bindSome`](#bindsome) do
([#679](https://github.com/nanyx-lang/nanyx/issues/679)).

## `Result.defaultValue`

```nanyx fragment
def Result.defaultValue: Result(a, e), a -> a
```

The success, or `fallback` for an error.

## `Result.mapOr`

```nanyx fragment
def Result.mapOr: [c] Result(a, e), b, ([c] a -> b) -> b
```

`f` applied to the success, or `default` for an error.

## `Result.orElse`

```nanyx fragment
def Result.orElse: [c] Result(a, e), ([c] e -> Result(a, f)) -> Result(a, f)
```

The success as it is, or `f` applied to the error.

## `Result.isOk`

```nanyx fragment
def Result.isOk: Result(a, e) -> bool
```

Whether the result is a success.

## `Result.isError`

```nanyx fragment
def Result.isError: Result(a, e) -> bool
```

Whether the result is an error.

## `mapSome`

```nanyx fragment
def mapSome: [c] #some(a) | r, ([c] a -> b) -> #some(b) | r
```

Applies `f` to the value of `#some`.

## `bindSome`

```nanyx fragment
def bindSome: [c] #some(a) | r, ([c] a -> #some(b) | r) -> #some(b) | r
```

Continues with `f` on the value of `#some`. The other tags of both steps are kept: `r` stands for all of them,
and a step given with other tags adds them to it, so `#some("42") \bindSome(int.fromString)` on a
`#some(string) | #notFound` is a `#some(int) | #notFound | #invalidInt`. A function that is itself
generic in both steps cannot say so yet: a union has one rest, so `#some(b) | r | s` is not a type
([#680](https://github.com/nanyx-lang/nanyx/issues/680)).

## `someOr`

```nanyx fragment
def someOr: #some(a) | r, a -> a
```

The value of `#some`, or `fallback` for any other tag.

## `someOrElse`

```nanyx fragment
def someOrElse: [c] #some(a) | r, ([c] () -> a) -> a
```

The value of `#some`, or the result of calling `fallback` for any other tag, as `??` gives it.
`fallback` runs only when it is needed.

## `isSome`

```nanyx fragment
def isSome: #some(a) | r -> bool
```

Whether the option is `#some`.

## `isNone`

```nanyx fragment
def isNone: #some(a) | r -> bool
```

Whether the option is any tag but `#some`.

## `someToResult`

```nanyx fragment
def someToResult: #some(a) | r, e -> Result(a, e)
```

The value of `#some` as a success, or `error` for any other tag.

## `mapSomeOr`

```nanyx fragment
def mapSomeOr: [c] #some(a) | r, b, ([c] a -> b) -> b
```

`f` applied to the value of `#some`, or `default` for any other tag.

## `mapSomeOrElse`

```nanyx fragment
def mapSomeOrElse: [c] #some(a) | r, ([c] () -> b), ([c] a -> b) -> b
```

`f` applied to the value of `#some`, or the result of calling `fallback` for any other tag. `fallback` runs only
when it is needed.

## `orElseSome`

```nanyx fragment
def orElseSome: [c] #some(a) | r, ([c] () -> #some(a) | s) -> #some(a) | s
```

The option as it is if it is `#some`, or the result of calling `fallback` for any other tag. `fallback` runs only
when it is needed.

## `someToList`

```nanyx fragment
def someToList: #some(a) | r -> list(a)
```

The value of `#some` as a one-element list, or the empty list for any other tag. Spread into a
list literal, it adds an element only when there is one: `["device", ...(identity \someToList)]`.

## `somes`

```nanyx fragment
def somes: list(#some(a) | r) -> list(a)
```

The values of the `#some`s in `options`, in order. The other tags are dropped.

<!-- End of the generated reference. -->
