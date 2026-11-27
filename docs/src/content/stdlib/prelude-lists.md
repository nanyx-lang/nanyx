---
title: "prelude: lists"
description: Functions on lists
order: 7
---

Functions on `list(a)`. They are [attached definitions](../advanced/attached-definitions) on `list`, so `xs \map(f)` resolves to `list.map` without qualification when `xs` is a list, and are in the [prelude](prelude), so no import is needed. `list.groupBy`, which gives a map, is on the [maps and sets](prelude-maps#listgroupby) page.

Since lists are immutable, every function that transforms a list gives back a new one. When nothing else refers to the list you transform, the new list may be built in the old one's memory: `list.map` rewrites its cells in place (see [Reference counting](../deep-dives/reference-counting#reuse-in-place)).

Each of them is stack-safe, and works on a list of any length without running out of stack: it walks the list with [tail calls](../tour/functions#tail-recursion), and one that builds a list builds it in reverse and reverses it at the end.

```nanyx
def scores = [3, 8, 1, 8, 5]

println(scores \list.filter { > 2 } \list.map { * 10 })
println(scores \list.distinct \list.sort)
println(scores \list.fold(0) { + })
-- [30, 80, 80, 50]
-- [1, 3, 5, 8]
-- 25
```

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

## `list.toList`

```nanyx fragment
def list.toList: list(a) -> list(a)
```

`xs` itself: a list is already one. With it, code written over any collection's `toList` works
for a list too.

## `list.fromList`

```nanyx fragment
def list.fromList: list(a) -> list(a)
```

`xs` itself: a list is already one. With it, code written over any collection's `fromList` works
for a list too.

## `list.fold`

```nanyx fragment
def list.fold: [c] list(a), b, ([c] (b, a) -> b) -> b
```

`step` applied to `initial` and the first element, then to that result and the second, and so on.

## `list.reduce`

```nanyx fragment
def list.reduce: [c] list(a), ([c] (a, a) -> a) -> #some(a) | #none
```

The elements of `xs` combined with `f`, in order, starting from the first: `[1, 2, 3, 4, 5]
\list.reduce { * }` is `#some(120)`, and an empty list, having no first element, gives `#none`.
To start from another value, use [`list.fold`](#listfold); for a total, [`list.sum`](#listsum) or [`list.product`](#listproduct).

## `list.reverse`

```nanyx fragment
def list.reverse: list(a) -> list(a)
```

The elements in reverse order.

## `list.length`

```nanyx fragment
def list.length: list(a) -> int
```

The number of elements.

## `list.sum`

```nanyx fragment
def list.sum: [Add(a)] list(a) -> a
```

The elements added together: `[1, 2, 3] \list.sum` is 6, and the sum of no elements is `zero`.

## `list.product`

```nanyx fragment
def list.product: [Num(a)] list(a) -> a
```

The elements multiplied together: `[1, 2, 3, 4] \list.product` is 24, and the product of no
elements is 1.

## `list.isEmpty`

```nanyx fragment
def list.isEmpty: list(a) -> bool
```

Whether there are no elements.

## `list.count`

```nanyx fragment
def list.count: [c] list(a), ([c] a -> bool) -> int
```

The number of elements `test` holds for.

## `list.head`

```nanyx fragment
def list.head: list(a) -> #some(a) | #emptyList
```

The first element, or `#emptyList`.

## `list.last`

```nanyx fragment
def list.last: list(a) -> #some(a) | #emptyList
```

The last element, or `#emptyList`.

## `list.at`

```nanyx fragment
def list.at: list(a), int -> #some(a) | #notFound
```

The element at position `index`, counting from 0, or `#notFound` when `index` is negative or past
the end. It walks to the element, in time proportional to `index`.

## `list.contains`

```nanyx fragment
def list.contains: [Eq(a)] list(a), a -> bool
```

Whether any element is equal to `x`. Elements are compared with `==`, by their structure.

## `list.indexOf`

```nanyx fragment
def list.indexOf: [Eq(a)] list(a), a -> #some(int) | #notFound
```

The position of the first element equal to `x`, counting from 0, or `#notFound`. Elements are
compared with `==`, by their structure.

## `list.findIndex`

```nanyx fragment
def list.findIndex: [c] list(a), ([c] a -> bool) -> #some(int) | #notFound
```

The position of the first element `matches` holds for, counting from 0, or `#notFound`.

## `list.map`

```nanyx fragment
def list.map: [c] list(a), ([c] a -> b) -> list(b)
```

`f` applied to each element, in order. Stack-safe: it works on a list of any length.

## `list.mapIndexed`

```nanyx fragment
def list.mapIndexed: [c] list(a), ([c] (int, a) -> b) -> list(b)
```

`f` applied to each element's position, counting from 0, and the element, in order. Stack-safe:
it works on a list of any length.

## `list.flatMap`

```nanyx fragment
def list.flatMap: [c] list(a), ([c] a -> list(b)) -> list(b)
```

The lists `f` gives for each element, joined end to end. Stack-safe: it works however long the
lists are.

## `list.filter`

```nanyx fragment
def list.filter: [c] list(a), ([c] a -> bool) -> list(a)
```

The elements `keep` holds for, in order. Stack-safe: it works on a list of any length.

## `list.filterMap`

```nanyx fragment
def list.filterMap: [c] list(a), ([c] a -> #some(b) | #skip) -> list(b)
```

The values `f` gives as `#some`, in order, leaving out the elements it gives `#skip` for: a `map`
and a `filter` in one walk. Stack-safe: it works on a list of any length.

## `list.partition`

```nanyx fragment
def list.partition: [c] list(a), ([c] a -> bool) -> (list(a), list(a))
```

The elements `test` holds for and the elements it fails for, each in order. Stack-safe: it works on
a list of any length.

## `list.find`

```nanyx fragment
def list.find: [c] list(a), ([c] a -> bool) -> #some(a) | #notFound
```

The first element `matches` holds for, or `#notFound`.

## `list.build`

```nanyx fragment
def list.build: [c] ([c, Emit(a)] () -> ()) -> list(a)
```

The list of what `block` emits, in the order it emits it: `list.build { emit(1); if more then
emit(2) }`. `block` runs once, now, under a handler of `Emit` that puts each value in a new cell at
the end of the list, so a `for` loop in it stays a loop and nothing else is made per value.

## `list.select`

```nanyx fragment
def list.select: [c] list(a), ([c, Emit(b)] a -> ()) -> list(b)
```

The values `f` emits for each element in turn, in the order it emits them: `items \list.select {
item -> if item is #other(v) then emit(v) }` gives the payload of each `#other`. `f` can emit any
number of values for an element, none included, through `Emit`. A lambda that gives a list
instead is [`list.flatMap`](#listflatmap)'s.

## `list.selectOne`

```nanyx fragment
def list.selectOne: [c, d] list(a), ([c] a -> [d] seq(b)) -> #some(b) | #notFound
```

The first value `f` yields, running it on the elements in turn, or `#notFound` when it yields
none. A lambda with `yield` in it is a lazy sequence (see `Yield`), so this stops at the first
yield: `f` runs on no element after the one it yields for, and the code after that `yield` doesn't
run.

## `list.all`

```nanyx fragment
def list.all: [c] list(a), ([c] a -> bool) -> bool
```

Whether `test` holds for every element (so `true` for an empty list).

## `list.any`

```nanyx fragment
def list.any: [c] list(a), ([c] a -> bool) -> bool
```

Whether `test` holds for any element (so `false` for an empty list).

## `list.foldBack`

```nanyx fragment
def list.foldBack: [c] list(a), b, ([c] (b, a) -> b) -> b
```

[`list.fold`](#listfold) from the last element to the first:
`list.foldBack(["a", "b", "c"], "", { acc, x -> acc ++ x })` is `"cba"`.

## `list.foldWhile`

```nanyx fragment
def list.foldWhile: [c] list(a), b, ([c] (b, a) -> #some(b) | r) -> #some(b) | r
```

[`list.fold`](#listfold) that can stop part-way: `step` gives `#some(acc)` to go on to the next element,
and anything else it gives ends the fold there and is its result, as it is. A fold that runs to the
end gives `#some(acc)`. `step` isn't called again after it stops: with `step` being
`{ total, x -> if x > 10 then #tooBig(x) else #some(total + x) }`, `list.foldWhile([1, 2, 3], 0,
step)` is `#some(6)` and `list.foldWhile([1, 20, 3], 0, step)` is `#tooBig(20)`.

## `list.append`

```nanyx fragment
def list.append: list(a), a -> list(a)
```

`xs` with `x` added at the end.

## `list.flatten`

```nanyx fragment
def list.flatten: list(list(a)) -> list(a)
```

The lists of `xss` joined end to end. Stack-safe: it works however long the lists are.

## `list.take`

```nanyx fragment
def list.take: list(a), int -> list(a)
```

The first `count` elements (all of them when there are fewer). Stack-safe: it works on a list of
any length.

## `list.takeWhile`

```nanyx fragment
def list.takeWhile: [c] list(a), ([c] a -> bool) -> list(a)
```

The elements before the first one `keep` fails for. Stack-safe: it works on a list of any length.

## `list.dropLast`

```nanyx fragment
def list.dropLast: list(a) -> list(a)
```

All the elements but the last (no elements for an empty list). Stack-safe: it works on a list of
any length.

## `list.skip`

```nanyx fragment
def list.skip: list(a), int -> list(a)
```

The elements after the first `count`.

## `list.skipWhile`

```nanyx fragment
def list.skipWhile: [c] list(a), ([c] a -> bool) -> list(a)
```

The elements from the first one `test` fails for, so `list.takeWhile(xs, f) ++ list.skipWhile(xs, f)`
is `xs`.

## `list.zip`

```nanyx fragment
def list.zip: list(a), list(b) -> list((a, b))
```

Pairs of the elements at the same position, as long as the shorter list. Stack-safe: it works on
lists of any length.

## `list.zipWith`

```nanyx fragment
def list.zipWith: [c] list(a), list(b), ([c] (a, b) -> d) -> list(d)
```

`f` applied to the elements of `xs` and `ys` in pairs, stopping at the end of the shorter, as
[`list.zip`](#listzip) does: `[1, 2, 3] \zipWith([10, 20]) { + }` is `[11, 22]`. Stack-safe: it works on
lists of any length.

## `list.intersperse`

```nanyx fragment
def list.intersperse: list(a), a -> list(a)
```

`xs` with `separator` between each pair of neighbouring elements: `[1, 2, 3] \intersperse(0)` is
`[1, 0, 2, 0, 3]`. Stack-safe: it works on a list of any length.

## `list.chunks`

```nanyx fragment
def list.chunks: list(a), int -> list(list(a))
```

`xs` cut into lists of `size` elements, in order; the last is shorter when `size` doesn't divide
the length: `[1, 2, 3, 4, 5] \chunks(2)` is `[[1, 2], [3, 4], [5]]`. A `size` below 1 crashes.
Stack-safe: it works on a list of any length.

## `list.windows`

```nanyx fragment
def list.windows: list(a), int -> list(list(a))
```

Every run of `size` neighbouring elements, in order: `[1, 2, 3, 4] \windows(2)` is
`[[1, 2], [2, 3], [3, 4]]`, and a list shorter than `size` has none. A `size` below 1 crashes.
Stack-safe: it works on a list of any length.

## `list.scan`

```nanyx fragment
def list.scan: [c] list(a), b, ([c] (b, a) -> b) -> list(b)
```

The running totals of a fold: `initial`, then each value [`list.fold`](#listfold) passes along, so the last
is what the fold gives back: `[1, 2, 3] \scan(0) { + }` is `[0, 1, 3, 6]`. Stack-safe: it works on
a list of any length.

## `list.unzip`

```nanyx fragment
def list.unzip: list((a, b)) -> (list(a), list(b))
```

The first parts of the pairs and their second parts, each in order: the inverse of [`list.zip`](#listzip) for
lists of equal length. Stack-safe: it works on a list of any length.

## `list.distinct`

```nanyx fragment
def list.distinct: [Eq(a)] list(a) -> list(a)
```

The elements without repeats, each kept where it first appears. Elements are compared with `==`,
by their structure, so this works for lists of anything but functions.

## `list.distinctBy`

```nanyx fragment
def list.distinctBy: [Eq(b), Hash(b), c] list(a), ([c] a -> b) -> list(a)
```

The elements without two of the same `key`, each kept where the first with its key appears:
`list.distinctBy(people, { p -> p.name })` keeps one person of each name. Stack-safe: it works on a
list of any length.

## `list.sortWith`

```nanyx fragment
def list.sortWith: [c] list(a), ([c] (a, a) -> #less | #equal | #greater) -> list(a)
```

The elements in the order `order` gives: `order(a, b)` says whether `a` comes before `b`
(`#less`), after it (`#greater`), or either way (`#equal`), as `compare` and [`int.compare`](prelude-contexts#intcompare) do.
It sorts by an order that isn't a type's own: `{ a, b -> b.age \int.compare(a.age) }` sorts by
age, oldest first, and one that compares a second key when the first is `#equal` sorts by both.
The sort is stable: elements `order` finds equal keep their order. Stack-safe: it works on a list
of any length.

## `list.sortBy`

```nanyx fragment
def list.sortBy: [Ord(b), c] list(a), ([c] a -> b) -> list(a)
```

The elements in ascending order of `key`. The sort is stable: elements with the same key keep
their order. A key can be of any type with an order: `list.sortBy(people, { p -> p.name })`.
Stack-safe: it works on a list of any length.

## `list.sortByDescending`

```nanyx fragment
def list.sortByDescending: [Ord(b), c] list(a), ([c] a -> b) -> list(a)
```

The elements in descending order of `key`: `list.sortByDescending(people, { p -> p.age })` puts
the oldest first. The sort is stable: elements with the same key keep their order, which reversing
[`list.sortBy`](#listsortby)'s result would not do. Stack-safe: it works on a list of any length.

## `list.sort`

```nanyx fragment
def list.sort: [Ord(a)] list(a) -> list(a)
```

The elements in ascending order of their `Ord`: numbers numerically, strings by their bytes,
tuples and lists position by position. The sort is stable. Records and tags have no order, so a
list of them is sorted with [`list.sortBy`](#listsortby) and a key. Stack-safe: it works on a list of any length.

## `list.sortDescending`

```nanyx fragment
def list.sortDescending: [Ord(a)] list(a) -> list(a)
```

The elements in descending order of their `Ord`, greatest first: `[3, 1, 2]` gives `[3, 2, 1]`.
The sort is stable. Stack-safe: it works on a list of any length.

## `list.min`

```nanyx fragment
def list.min: [Ord(a)] list(a) -> #some(a) | #emptyList
```

The least element, or `#emptyList`. Of several least elements, the first.

## `list.max`

```nanyx fragment
def list.max: [Ord(a)] list(a) -> #some(a) | #emptyList
```

The greatest element, or `#emptyList`. Of several greatest elements, the first. Elements are
ordered by their `Ord`, so a list of records has none: use [`list.maxBy`](#listmaxby) with a key.

## `list.minBy`

```nanyx fragment
def list.minBy: [Ord(b), c] list(a), ([c] a -> b) -> #some(a) | #emptyList
```

The element with the least `key`, or `#emptyList`. Of several elements with the least key, the
first. `key` is called once for each element.

## `list.maxBy`

```nanyx fragment
def list.maxBy: [Ord(b), c] list(a), ([c] a -> b) -> #some(a) | #emptyList
```

The element with the greatest `key`, or `#emptyList`. Of several elements with the greatest key,
the first. `key` is called once for each element.

## `list.unfold`

```nanyx fragment
def list.unfold: [c] b, ([c] b -> #some(a, b) | #done) -> list(a)
```

The elements `step` gives, starting from `state`: `#some(x, next)` gives `x` and carries on from
`next`, and `#done` ends the list. Stack-safe: it works however many elements `step` gives.

## `list.init`

```nanyx fragment
def list.init: [c] int, ([c] int -> a) -> list(a)
```

The list of `count` elements whose element at each position is `f` applied to the position:
`list.init(3, { i -> i * 2 })` is `[0, 2, 4]`. `f` is called on the positions in order, and a
`count` of zero or less gives `[]`. Stack-safe: it works for any `count`.

## `list.replicate`

```nanyx fragment
def list.replicate: (a, count: int) -> list(a)
```

`count` copies of `x`: `list.replicate(0, count = 3)` is `[0, 0, 0]`. A `count` of zero or less
gives `[]`. Stack-safe: it works for any `count`.

<!-- End of the generated reference. -->
