---
title: "prelude: results and options"
description: The proc workflow, and helpers for option-like unions
order: 9
---

The `proc` workflow, and the helpers for option-like unions. They are in the [prelude](prelude), so no import is needed. How to handle errors with them is in the tour's [Error handling](../tour/error-handling).

# Results

A function that can fail gives `#some` with its value, or `#ok` when it has none, on success, and a tag of its own for each way it fails, with what there is to say about it as the tag's payload. There is no `Result` type: the option helpers below work on any such union, and keep its failures as they are.

```nanyx
def parse: string -> #some(int) | #notANumber(string) = { s ->
  int.fromString(s) except #invalidInt -> #notANumber(s)
}

def half: int -> #some(int) | #odd(int) = { n ->
  if n % 2 == 0 then #some(n // 2) else #odd(n)
}

println(parse("42") \bindSome(half))
println(parse("7") \bindSome(half))
-- #some(21)
-- #odd(7)
```

# Options

There is no `Option` type. Any union that includes `#some(a)` is [option-like](../advanced/option-like-types): `#some(a) | #none` is the most common, and `int.fromString` gives a `#some(int) | #invalidInt`. These helpers keep every tag but `#some` as it is, whatever it is. They are plain functions rather than definitions attached to a type, because there is no `Option` type for them to be attached to, and they are named for the `#some` tag they act on, as `mapSome`, `someOr` and `somes` are.

```nanyx
println(["1", "x", "3"] \list.map(int.fromString) \somes)
println(int.fromString("x") \mapSome { * 2 } \someOr(0))
-- [1, 3]
-- 0
```

The [`??` operator](../tour/error-handling#falling-back-with-) is `someOrElse`: `option ?? fallback` is `someOrElse(option, { fallback })`, and the fallback is worked out only when it is needed.

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

## `Proc`

```nanyx fragment
context Proc = (
  try: [c] #some(a) | r, ([c] a -> #some(b) | r) -> #some(b) | r
  return: a -> #some(a) | r
  zero: #some() | r
  for: [d] list(x), ([d] x -> #__nextItem | #some(y) | s), ([d] () -> #some(y) | s) -> #some(y) | s
)
```

The context of the result workflow, which `proc` runs its block under: a `try`, `return` or
`for` in a `proc { ... }` block calls the member of that name, and a block that ends without
`return` has `zero` as its value.

- `try`: Continues with the payload of `#some`, and gives back any other tag as is without running
  the rest of the block.
- `return`: Wraps the value in `#some`.
- `zero`: The value of a block that ends without `return`.
- `for`: Runs a loop whose body uses a keyword (see `procFor`): the body gives back `#__nextItem` to go
  on to the next item.

## `proc`

```nanyx fragment
def proc: [c] ([Proc, c] () -> #some(b) | r) -> #some(b) | r
```

The result workflow: inside `proc { ... }`, `try x` continues with the payload of `#some`, and
gives back any other tag, a failure, as is without running the rest of the block, and `return x`
wraps the value in `#some`.

```nanyx
def total = proc {
  def customer = try getCustomer(5)
  def order = try getLastOrder(customer)
  return order.total
}
```

A workflow builder is a function that runs its block under a context. `proc { ... }` is the
call `proc({ ... })`, and in the block each `try x` (and each `try x` nested inside an
expression) is a call to the context's `try` member, with the rest of the block as its
continuation, and each `return x` a call to its `return` member. A block that ends without
`return` has `zero` as its value, `#some()`, so a `proc` that is only run for what it does
needs no `return`.

Every tag other than `#some` is a failure, and the block collects them: a `try` on a
`#some(string) | #nil` and another on a `#some(int) | #notFound` make the block a
`#some(...) | #nil | #notFound`. Two failures with the same tag whose payloads are tags combine
too: `#invalid(#empty)` and `#invalid(#tooLong)` make `#invalid(#empty | #tooLong)`. Payloads of
other types must agree.

A `for` loop over a list, a map or a set whose body uses `try` or `return` is run by `for`, item
by item, a map's or a set's items its entries or elements in the order `toList` gives. A
`try` that fails in the body ends the whole block with that failure, and a `return` ends it
with its value, as they do outside the loop: the items after it, and what follows the loop,
don't run.

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
`#some(string) | #notFound` is a `#some(int) | #notFound | #invalidInt`. A union has one rest, so a
function generic in both steps shares one `r` too: `#some(b) | r | s` is an error (NYX0259).

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

## `allSome`

```nanyx fragment
def allSome: list(#some(a) | r) -> #some(list(a)) | r
```

The values of `options`, in order, if every element is `#some`; otherwise the first element that
isn't, as it is: the first failure. It turns what parses a list of inputs into one result:
`["1", "2", "x"] \list.map(int.fromString) \allSome` is `#invalidInt`. Stack-safe: it works on a
list of any length.

<!-- End of the generated reference. -->
