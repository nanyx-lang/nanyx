---
title: "prelude: sequences"
description: Lazy sequences, and ranges
order: 7.5
---

`seq(a)` is a lazy sequence: a function that, when pulled, gives back its first element and the rest of the sequence, or `#done`. Nothing is worked out until it is pulled, and pulling it changes nothing, so a sequence can be passed around, stored and pulled again. Ranges are sequences: `1..10` is a `seq(int)`, and `0..` is one that never ends. The functions here are [attached definitions](../advanced/attached-definitions) on `seq`, so `s \map(f)` resolves to `seq.map` when `s` is a sequence, and are in the [prelude](prelude), so no import is needed.

A function that gives back a sequence, such as `seq.map`, runs its callback when the sequence is pulled, not when it is called, so the sequence carries the callback's contexts: mapping with a function that prints gives a `[Console] seq(b)`, which can be pulled only where `Console` is (see [Lazy sequences](../work-in-progress/lazy-sequences)). A function that pulls the whole sequence now, such as `seq.toList`, needs them where it is called.

```nanyx
println((1..10) \seq.map { * 2 } \seq.toList)
println(0.. \seq.filter { x -> x % 3 == 0 } \seq.take(4) \seq.toList)
println((1..4) \seq.fold(0) { + })
-- [2, 4, 6, 8, 10, 12, 14, 16, 18, 20]
-- [0, 3, 6, 9]
-- 10
```

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

## `Seq`

```nanyx fragment
type Seq(a) = [k] protected (next: ([k] () -> #next(a, seq(a)) | #done))
```

A lazy sequence of `a`s: a function that, when called, gives back the first element and the rest
of the sequence, `#next(x, rest)`, or `#done`. Calling it changes nothing, so a sequence can be
pulled again, and does its work again. A sequence whose steps need contexts says so in its type,
`[Console] seq(int)`, and the rest of it needs them too. Ranges are sequences: `1..10` is
`seq.range(1, 10)` and `0..` is `seq.from(0)`.

## `seq.empty`

```nanyx fragment
def seq.empty: () -> seq(a)
```

The sequence of no elements.

## `seq.delay`

```nanyx fragment
def seq.delay: ([c] () -> [d] seq(a)) -> [c, d] seq(a)
```

The sequence `f` gives, worked out when it is pulled, not now: `f` runs each time the sequence is
pulled.

## `seq.cons`

```nanyx fragment
def seq.cons: a, ([c] () -> [d] seq(a)) -> [c, d] seq(a)
```

`x`, then the elements of the sequence `rest` gives, which runs only when the sequence is pulled
past `x`.

## `seq`

```nanyx fragment
def seq: ([c] () -> [d] seq(a)) -> [c, d] seq(a)
```

The sequence `block` gives, worked out when it is pulled. A block with `yield` in it is a sequence:
`seq { yield 1  yield 2 }` gives `1` and `2`, and the code after a `yield` runs only when the
sequence is pulled that far.

## `seq.range`

```nanyx fragment
def seq.range: int, int -> seq(int)
```

The `int`s from `first` up to `last`, both included: `1..10`. Empty when `last` is less than
`first`.

## `seq.from`

```nanyx fragment
def seq.from: int -> seq(int)
```

The `int`s from `first` up, without end: `0..`.

## `seq.unfold`

```nanyx fragment
def seq.unfold: b, ([c] b -> #some(a, b) | #done) -> [c] seq(a)
```

The elements `step` gives, starting from `state`: `#some(x, next)` gives `x` and carries on from
`next`, and `#done` ends the sequence. `step` runs when the sequence is pulled.

## `list.toSeq`

```nanyx fragment
def list.toSeq: list(a) -> seq(a)
```

The elements of `xs`, in order.

## `seq.map`

```nanyx fragment
def seq.map: ([c] seq(a)), ([d] a -> b) -> [c, d] seq(b)
```

`f` applied to each element of `s`, when it is pulled: `(1..3) \seq.map { * 2 }` gives `2`, `4`
and `6`.

## `seq.append`

```nanyx fragment
def seq.append: ([c] seq(a)), ([d] seq(a)) -> [c, d] seq(a)
```

The elements of `s`, then those of `t`.

## `seq.concatMap`

```nanyx fragment
def seq.concatMap: ([c] seq(a)), ([d] a -> [d] seq(b)) -> [c, d] seq(b)
```

The elements of the sequences `f` gives for each element of `s`, one after another, when it is
pulled: `(1..3) \seq.concatMap { n -> seq.range(1, n) }` gives `1`, `1`, `2`, `1`, `2` and `3`.

## `seq.filter`

```nanyx fragment
def seq.filter: ([c] seq(a)), ([d] a -> bool) -> [c, d] seq(a)
```

The elements of `s` for which `predicate` is true. Pulling it pulls `s` until one is, in constant
stack.

## `seq.take`

```nanyx fragment
def seq.take: ([c] seq(a)), int -> [c] seq(a)
```

The first `count` elements of `s`, or all of them if it has fewer. It pulls no more of `s` than
that, so it ends an endless sequence: `(0..) \seq.take(3)` is `0`, `1` and `2`.

## `seq.skip`

```nanyx fragment
def seq.skip: ([c] seq(a)), int -> [c] seq(a)
```

`s` without its first `count` elements, which are pulled and thrown away when it is pulled.

## `seq.takeWhile`

```nanyx fragment
def seq.takeWhile: ([c] seq(a)), ([d] a -> bool) -> [c, d] seq(a)
```

The elements of `s` up to the first for which `predicate` is false.

## `seq.fold`

```nanyx fragment
def seq.fold: [c, d] ([c] seq(a)), b, ([d] (b, a) -> b) -> b
```

`step` applied to `initial` and the first element, then to that result and the second, and so
on, pulling the whole of `s` now. Stack-safe, but an endless sequence never ends.

## `seq.reduce`

```nanyx fragment
def seq.reduce: [Default(a), c, d] ([c] seq(a)), ([d] (a, a) -> a) -> a
```

The elements of `s`, pulled now, combined with `f`, in order, starting from the element type's
`Default`: `(1..4) \seq.reduce { + }` is `10`. An empty one gives the default, so `reduce` never
fails, unlike a `reduce` that starts from the first element. To start from another value, use
[`seq.fold`](#seqfold).

## `seq.toList`

```nanyx fragment
def seq.toList: [c] ([c] seq(a)) -> list(a)
```

The elements of `s`, pulled now, as a list: `(1..3) \seq.toList` is `[1, 2, 3]`. Stack-safe, but
an endless sequence never ends.

<!-- End of the generated reference. -->
