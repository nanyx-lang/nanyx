---
title: "prelude: sequences"
description: Lazy sequences, and ranges
order: 7.5
---

`seq(a)` is a lazy sequence: a function that, when pulled, gives back its first element and the rest of the sequence, or `#done`. Nothing is worked out until it is pulled, and pulling it changes nothing, so a sequence can be passed around, stored and pulled again. Ranges are sequences: `1..10` is a `seq(int)`, and `0..` is one that never ends. The functions here are [attached definitions](../advanced/attached-definitions) on `seq`, so `s \map(f)` resolves to `seq.map` when `s` is a sequence, and are in the [prelude](prelude), so no import is needed.

A function that gives back a sequence, such as `seq.map`, runs its callback when the sequence is pulled, not when it is called, so the sequence carries the callback's contexts: mapping with a function that prints gives a `[Console] seq(b)`, which can be pulled only where `Console` is (see [Lazy sequences](../deep-dives/lazy-sequences#effects)). A function that pulls the whole sequence now, such as `seq.toList`, needs them where it is called.

```nanyx
println((1..10) \seq.map { * 2 } \seq.toList)
println(0.. \seq.filter { x -> x % 3 == 0 } \seq.take(4) \seq.toList)
println((1..4) \seq.fold(0) { + })
-- [2, 4, 6, 8, 10, 12, 14, 16, 18, 20]
-- [0, 3, 6, 9]
-- 10
```

`seq` has `list`'s functions, with `list`'s names, argument order and results, where a sequence can give them: the ones that give back a sequence are lazy, the ones that find something, such as `seq.find`, `seq.any` and `seq.head`, stop pulling as soon as they have their answer, so they work on an endless sequence that has one, and the ones that read every element, such as `seq.length`, `seq.max` and `seq.partition`, never return on an endless sequence. Each function's comment says which it is. `reverse`, the `sort` family, `foldBack` and `dropLast` are left out on purpose: each would have to read and keep the whole sequence before giving anything, which is a list's job, so write `xs \seq.toList \list.sort`.

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

## `Sequence`

```nanyx fragment
type Sequence(a) = [k] protected (next: ([k] () -> #next(a, seq(a)) | #done))
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

## `Enumerable`

```nanyx fragment
context Enumerable(a) = (
  succ: a -> a
)
```

The values that come one after another, so that a range of them can be counted through: `succ`
gives the value after `x`. A range, `first..last`, takes the values from `first` up to `last` in
steps of `succ`, and stops by their `Ord`. A type gets it by attaching `succ`, as `int` does.

## `int.succ`

```nanyx fragment
def int.succ: int -> int
```

The value after `n`: `n + 1`. `int`'s [`Enumerable`](#enumerable), which counts a range of `int`s.

## `seq.range`

```nanyx fragment
def seq.range: [Enumerable(a), Ord(a)] a, a -> seq(a)
```

The values from `first` up to `last`, both included, each the `succ` of the one before: `1..10`.
Empty when `last` is less than `first`.

## `seq.from`

```nanyx fragment
def seq.from: [Enumerable(a)] a -> seq(a)
```

The values from `first` up, each the `succ` of the one before, without end: `0..`.

## `seq.unfold`

```nanyx fragment
def seq.unfold: b, ([c] b -> #some(a, b) | #done) -> [c] seq(a)
```

The elements `step` gives, starting from `state`: `#some(x, next)` gives `x` and carries on from
`next`, and `#done` ends the sequence. `step` runs when the sequence is pulled.

## `Iterable`

```nanyx fragment
context Iterable(c) = (
  elem: type
  toSeq: c -> seq(elem)
)
```

What can be iterated: a value of type `c` that gives a sequence of `elem`s. A `for` loop walks
anything `Iterable`, and the `seq` functions that read a sequence take one. A type gets it by
attaching `toSeq`, whose type fixes `elem`: `list.toSeq: list(a) -> seq(a)` makes the `elem` of a
`list(int)` an `int`. A signature that needs the element type fixes it by name,
`[Iterable(c, elem = string)]`. The sequence `toSeq` gives needs no contexts.

## `seq.toSeq`

```nanyx fragment
def seq.toSeq: seq(a) -> seq(a)
```

`s` itself: a sequence is [`Iterable`](#iterable).

## `list.toSeq`

```nanyx fragment
def list.toSeq: list(a) -> seq(a)
```

The elements of `xs`, in order.

## `seq.fromList`

```nanyx fragment
def seq.fromList: list(a) -> seq(a)
```

The elements of `xs`, in order: `seq.fromList([1, 2, 3]) \seq.toList` is `[1, 2, 3]`. The same
as [`list.toSeq`](#listtoseq).

## `seq.fromArray`

```nanyx fragment
def seq.fromArray: array(a) -> seq(a)
```

The elements of `xs`, in order, each read when the sequence is pulled. What a literal builds a
sequence with, `seq[1, 2, 3]` or `Sequence[1, 2, 3]`. The same as [`array.toSeq`](prelude-arrays#arraytoseq).

## `seq.map`

```nanyx fragment
def seq.map: ([c] seq(a)), ([d] a -> b) -> [c, d] seq(b)
```

`f` applied to each element of `s`, when it is pulled: `(1..3) \seq.map { * 2 }` gives `2`, `4`
and `6`.

## `seq.zip`

```nanyx fragment
def seq.zip: ([c] seq(a)), ([d] seq(b)) -> [c, d] seq((a, b))
```

Pairs the elements of `s` and `t` in order, ending when either ends, as `list.zip` stops at the
shorter list: `seq.zip(0.., list.toSeq(["a", "b"]))` gives `(0, "a")` and `(1, "b")`. Each pair
pulls one element from each side, and nothing more.

## `seq.append`

```nanyx fragment
def seq.append: ([c] seq(a)), ([d] seq(a)) -> [c, d] seq(a)
```

The elements of `s`, then those of `t`.

## `seq.flatMap`

```nanyx fragment
def seq.flatMap: ([c] seq(a)), ([d] a -> [d] seq(b)) -> [c, d] seq(b)
```

The elements of the sequences `f` gives for each element of `s`, one after another, when it is
pulled: `(1..3) \seq.flatMap { n -> seq.range(1, n) }` gives `1`, `1`, `2`, `1`, `2` and `3`. Lazy:
`f` runs on an element only when the sequence is pulled that far.

## `seq.flatten`

```nanyx fragment
def seq.flatten: ([c] seq(seq(a))) -> [c] seq(a)
```

The elements of each sequence of `s`, one after another: `[[1, 2], [3]] \list.map(list.toSeq)
\list.toSeq \seq.flatten` gives `1`, `2` and `3`. Lazy: it pulls `s` one sequence at a time.

## `seq.select`

```nanyx fragment
def seq.select: ([c] seq(a)), ([d] a -> [d] seq(b)) -> [c, d] seq(b)
```

The values `f` yields for each element in turn, in the order it yields them: [`seq.flatMap`](#sequenceflatmap) for
a lambda with `yield` in it, which is a lazy sequence (see `Yield`). Lazy: the code after a
`yield` runs only when the sequence is pulled that far.

## `seq.filterMap`

```nanyx fragment
def seq.filterMap: ([c] seq(a)), ([d] a -> #some(b) | #skip) -> [c, d] seq(b)
```

The payload of each `#some` that `f` gives for the elements of `s`, skipping each `#skip`:
`(1..6) \seq.filterMap { n -> if n % 2 == 0 then #some(n * 10) else #skip }` gives `20`, `40` and
`60`. Lazy: pulling it pulls `s` until `f` gives a `#some`, in constant stack.

## `seq.mapIndexed`

```nanyx fragment
def seq.mapIndexed: ([c] seq(a)), ([d] (int, a) -> b) -> [c, d] seq(b)
```

`f` applied to the position of each element of `s`, counting from 0, and the element:
`["a", "b"] \list.toSeq \seq.mapIndexed { i, x -> "{i}{x}" }` gives `"0a"` and `"1b"`. Lazy.

## `seq.skipWhile`

```nanyx fragment
def seq.skipWhile: ([c] seq(a)), ([d] a -> bool) -> [c, d] seq(a)
```

`s` from the first element for which `predicate` is false: `(1..5) \seq.skipWhile { < 3 }` gives
`3`, `4` and `5`. Lazy: the elements it skips are pulled when the sequence is first pulled.

## `seq.scan`

```nanyx fragment
def seq.scan: ([c] seq(a)), b, ([d] (b, a) -> b) -> [c, d] seq(b)
```

The running totals of a fold: `initial`, then each value [`seq.fold`](#sequencefold) passes along:
`(1..3) \seq.scan(0) { + }` gives `0`, `1`, `3` and `6`. Lazy: each total is worked out when it is
pulled, so it works on an endless sequence.

## `seq.zipWith`

```nanyx fragment
def seq.zipWith: ([c] seq(a)), ([d] seq(b)), ([e] (a, b) -> f) -> [c, d, e] seq(f)
```

`f` applied to the elements of `s` and `t` in pairs, ending when either ends, as [`seq.zip`](#sequencezip) does:
`seq.zipWith(1.., 10..12, { + })` gives `11`, `13` and `15`. Lazy.

## `seq.intersperse`

```nanyx fragment
def seq.intersperse: ([c] seq(a)), a -> [c] seq(a)
```

`s` with `separator` between each pair of neighbouring elements: `(1..3) \seq.intersperse(0)`
gives `1`, `0`, `2`, `0` and `3`. Lazy.

## `seq.chunks`

```nanyx fragment
def seq.chunks: ([c] seq(a)), int -> [c] seq(list(a))
```

`s` cut into lists of `size` elements, in order; the last is shorter when the sequence ends part
way through one: `(1..5) \seq.chunks(2)` gives `[1, 2]`, `[3, 4]` and `[5]`. Lazy: each list is
pulled when it is. A `size` below 1 crashes.

## `seq.windows`

```nanyx fragment
def seq.windows: ([c] seq(a)), int -> [c] seq(list(a))
```

Every run of `size` neighbouring elements, in order: `(1..4) \seq.windows(2)` gives `[1, 2]`,
`[2, 3]` and `[3, 4]`, and a sequence shorter than `size` has none. Lazy: each window pulls one
more element. A `size` below 1 crashes.

## `seq.distinct`

```nanyx fragment
def seq.distinct: [Eq(a)] seq(a) -> seq(a)
```

The elements of `s` without repeats, each where it first appears, compared with `==`. Lazy, but it
remembers each element it has given, so its memory grows with the distinct elements, and finding
the next one compares with each of them.

## `seq.distinctBy`

```nanyx fragment
def seq.distinctBy: [Eq(b), Hash(b)] seq(a), (a -> b) -> seq(a)
```

The elements of `s` without two of the same `key`, each where the first with its key appears. Lazy,
but it remembers each key it has seen, so its memory grows with the distinct keys.

## `seq.init`

```nanyx fragment
def seq.init: int, ([c] int -> a) -> [c] seq(a)
```

The sequence of `count` elements whose element at each position is `f` applied to the position:
`seq.init(3, { i -> i * 2 })` gives `0`, `2` and `4`. Lazy: `f` runs on a position when the
sequence is pulled that far. A `count` of zero or less gives no elements.

## `seq.replicate`

```nanyx fragment
def seq.replicate: (a, count: int) -> seq(a)
```

`count` copies of `x`: `seq.replicate(0, count = 3)` gives `0`, `0` and `0`. A `count` of zero or
less gives none.

## `seq.unzip`

```nanyx fragment
def seq.unzip: [c] ([c] seq((a, b))) -> ([c] seq(a), ([c] seq(b)))
```

The first parts of the pairs of `pairs` and their second parts, each a sequence: the inverse of
[`seq.zip`](#sequencezip). Each side pulls `pairs` on its own, so a sequence whose steps have effects runs them
once for each side pulled.

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

## `seq.foldWhile`

```nanyx fragment
def seq.foldWhile: [c, d] ([c] seq(a)), b, ([d] (b, a) -> #some(b) | r) -> #some(b) | r
```

[`seq.fold`](#sequencefold) that can stop part-way: `step` gives `#some(acc)` to go on to the next element, and
anything else it gives ends the fold there and is its result, as it is. A fold that runs to the end
gives `#some(acc)`. It pulls no element after it stops, so it ends on an endless sequence that
stops: `(1..) \seq.foldWhile(0, { total, n -> if total + n > 10 then #full(total) else
#some(total + n) })` is `#full(10)`.

## `seq.reduce`

```nanyx fragment
def seq.reduce: [c, d] ([c] seq(a)), ([d] (a, a) -> a) -> #some(a) | #none
```

The elements of `s`, pulled now, combined with `f`, in order, starting from the first: `(1..5)
\seq.reduce { * }` is `#some(120)`, and an empty sequence, having no first element, gives `#none`.
To start from another value, use [`seq.fold`](#sequencefold).

## `seq.toList`

```nanyx fragment
def seq.toList: [c] ([c] seq(a)) -> list(a)
```

The elements of `s`, pulled now, as a list: `(1..3) \seq.toList` is `[1, 2, 3]`. Stack-safe, but
an endless sequence never ends.

## `list.fromSeq`

```nanyx fragment
def list.fromSeq: [c] ([c] seq(a)) -> list(a)
```

The elements of `s`, pulled now, as a list: `(1..3) \list.fromSeq` is `[1, 2, 3]`. It requires
the contexts the sequence's steps do. An endless sequence never ends: cut it with [`seq.take`](#sequencetake).

## `seq.find`

```nanyx fragment
def seq.find: [c, d] ([c] seq(a)), ([d] a -> bool) -> #some(a) | #notFound
```

The first element of `s` for which `matches` is true, or `#notFound`: `(1..) \seq.find { n -> n * n
> 50 }` is `#some(8)`. It pulls `s` up to that element.

## `seq.findIndex`

```nanyx fragment
def seq.findIndex: [c, d] ([c] seq(a)), ([d] a -> bool) -> #some(int) | #notFound
```

The position, counting from 0, of the first element of `s` for which `matches` is true, or
`#notFound`. It pulls `s` up to that element.

## `seq.indexOf`

```nanyx fragment
def seq.indexOf: [Eq(a), c] ([c] seq(a)), a -> #some(int) | #notFound
```

The position, counting from 0, of the first element of `s` that is `== x`, or `#notFound`. It pulls
`s` up to that element.

## `seq.contains`

```nanyx fragment
def seq.contains: [Eq(a), c] ([c] seq(a)), a -> bool
```

Whether `s` has an element `== x`. It pulls `s` until it finds one.

## `seq.any`

```nanyx fragment
def seq.any: [c, d] ([c] seq(a)), ([d] a -> bool) -> bool
```

Whether `test` holds for any element of `s` (so `false` for an empty one). It pulls `s` until it
holds.

## `seq.all`

```nanyx fragment
def seq.all: [c, d] ([c] seq(a)), ([d] a -> bool) -> bool
```

Whether `test` holds for every element of `s` (so `true` for an empty one). It pulls `s` until it
doesn't.

## `seq.head`

```nanyx fragment
def seq.head: [c] ([c] seq(a)) -> #some(a) | #emptyList
```

The first element of `s`, or `#emptyList` when it has none, as [`list.head`](prelude-lists#listhead) says. It pulls one
element.

## `seq.at`

```nanyx fragment
def seq.at: [c] ([c] seq(a)), int -> #some(a) | #notFound
```

The element of `s` at `index`, counting from 0, or `#notFound` when `index` is negative or `s`
ends before it. It pulls `s` up to that element.

## `seq.isEmpty`

```nanyx fragment
def seq.isEmpty: [c] ([c] seq(a)) -> bool
```

Whether `s` has no elements. It pulls one element.

## `seq.selectOne`

```nanyx fragment
def seq.selectOne: [c, d] ([c] seq(a)), ([d] a -> [d] seq(b)) -> #some(b) | #notFound
```

The first value `f` yields, running it on the elements in turn as [`seq.select`](#sequenceselect) does, or
`#notFound` when it yields none. It stops at the first yield.

## `seq.count`

```nanyx fragment
def seq.count: [c, d] ([c] seq(a)), ([d] a -> bool) -> int
```

How many elements of `s` `test` holds for. It pulls the whole of `s`.

## `seq.length`

```nanyx fragment
def seq.length: [c] ([c] seq(a)) -> int
```

How many elements `s` has. It pulls the whole of `s`.

## `seq.last`

```nanyx fragment
def seq.last: [c] ([c] seq(a)) -> #some(a) | #emptyList
```

The last element of `s`, or `#emptyList` when it has none. It pulls the whole of `s`.

## `seq.max`

```nanyx fragment
def seq.max: [Ord(a), c] ([c] seq(a)) -> #some(a) | #emptyList
```

The largest element of `s`, or `#emptyList` when it has none; of equal ones, the first. It pulls the
whole of `s`.

## `seq.min`

```nanyx fragment
def seq.min: [Ord(a), c] ([c] seq(a)) -> #some(a) | #emptyList
```

The smallest element of `s`, or `#emptyList` when it has none; of equal ones, the first. It pulls
the whole of `s`.

## `seq.maxBy`

```nanyx fragment
def seq.maxBy: [Ord(b), c, d] ([c] seq(a)), ([d] a -> b) -> #some(a) | #emptyList
```

The element of `s` whose `key` is largest, or `#emptyList` when it has none; of equal keys, the
first. It pulls the whole of `s`.

## `seq.minBy`

```nanyx fragment
def seq.minBy: [Ord(b), c, d] ([c] seq(a)), ([d] a -> b) -> #some(a) | #emptyList
```

The element of `s` whose `key` is smallest, or `#emptyList` when it has none; of equal keys, the
first. It pulls the whole of `s`.

## `seq.sum`

```nanyx fragment
def seq.sum: [Add(a), c] ([c] seq(a)) -> a
```

The elements of `s` added up, from the element type's zero: `(1..4) \seq.sum` is `10`. It pulls
the whole of `s`.

## `seq.partition`

```nanyx fragment
def seq.partition: [c, d] ([c] seq(a)), ([d] a -> bool) -> (list(a), list(a))
```

The elements of `s` for which `test` holds, and those for which it doesn't, each a list in order. It
pulls the whole of `s`.

<!-- End of the generated reference. -->
