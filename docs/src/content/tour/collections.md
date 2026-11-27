---
title: Collections
description: Lists, maps, sets, and mutability
order: 8
---

Collections are typically immutable in Nanyx, although [it is possible to use and create mutable versions](../advanced/memory). Mutable versions have an `M` prefix in their name (for example, `MList` vs `list`), and each is a module of its own: [`nanyx/mlist`](../stdlib/mlist), [`nanyx/mstack`](../stdlib/mstack), [`nanyx/marray`](../stdlib/marray), [`nanyx/mslice`](../stdlib/mslice) and [`nanyx/mref`](../stdlib/mref).

# Immutable vs mutable

Immutable collections are persistent data structures, while mutable collections are optimized for in-place updates.

```nanyx
import nanyx/mlist

-- Immutable list

def numbers = [1, 2, 3]

-- Mutable list (requires Memory context)

def addItems = memory {
  def items: MList(int) = MList.empty()
  items += 1
  items += 2
  MList.toList(items)
}
```

Use immutable collections by default. Reach for mutable variants when profiling shows a hot path that benefits from in-place updates.

An immutable list can be updated in place too, when nothing else can see the change: transforming a list you hold the only reference to, with `list.map` or a function of your own that takes it apart and rebuilds it, reuses its cells. See [Reference counting](../deep-dives/reference-counting#reuse-in-place).

# Arrays and slices

An `Array(a)` holds its elements in one block, so reading one by its index takes the same time wherever it is, where a list walks to it. It never changes, so it needs no `Memory`. Calling an array reads an element, and gives `#notFound` past the end rather than stopping the program:

```nanyx
import nanyx/slice

def xs = Array[10, 20, 30, 40, 50]

println(xs(1))
println(xs(9))
println(array.length(xs))

def middle = xs \slice(1, 4)

println(Slice.toArray(middle))
```

This prints `#some(20)`, `#notFound`, `5` and `[20, 30, 40]`. To work on part of an array, or of a string, without copying it, take a slice: `xs \slice(1, 4)` is a [`Slice`](../stdlib/slice) that views the elements from index 1 up to but not including 4, and shares the array's storage. Bounds clamp, so a slice past the end is shorter, never an error. `Slice.toArray` copies the run into an array of its own. A slice has the functions a list has: those that make new elements, such as `Slice.map`, `Slice.filter` and `Slice.sort`, give an `Array`, and `Slice.take` and `Slice.skip` give a slice of the same array. An [`MSlice`](../stdlib/mslice) does the same for a mutable array, so `ys \slice(0, 3) \sort` sorts part of it in place, and a [`StringSlice`](../stdlib/text#stringslice) views part of a string.

The functions on arrays are on the [array](../stdlib/array) page. For an array whose elements change, see [Memory](../advanced/memory#mutable-collections).

# Common collection abstractions

Most of the time, Nanyx programs don't care very much about the specific implementation of a collection, just that it supports certain operations. For this reason, the standard library provides a number of common collection abstractions that can be implemented by any type:

| Collection type | Special alias | Characteristics |
|-----------------|-------------------|---------|
| `list(a)` | - | Indexable collections |
| `set(a)` | - | Membership checks |
| `map(k, v)` | - | Key/value lookup (a map is callable: `m(k)` gives `#some(v) \| #notFound`) |
| `Array(a)` | - | Fixed length, read by index in constant time (`import nanyx/array`; an array is callable: `xs(i)` gives `#some(a) \| #notFound`) |
| `Stack(a)` | - | Last in, first out (`import nanyx/stack`) |
| `Queue(a)` | - | First in, first out (`import nanyx/queue`) |
| `Trie(k, v)`, `StringTrie(v)` | - | Lookup by a sequence of keys, and every key that starts with a prefix (`import nanyx/trie`) |
| `Sequence(a)` | `seq(a)` | Lazy sequences, which may never end, such as the range `0..` ([sequences](../stdlib/prelude-sequences)) |

```nanyx
def lookup = ["a" => 1, "b" => 2]

def unique: set(int) = [1, 2, 2, 3]
```

A map gives its entries, and a set its elements, in an order of its own: `map.toList`, `for` and printing all walk them in the order of their hashes. It is the same for two maps with the same keys, however they were built, but it is unspecified, so sort for an order you can rely on: `map.toList(lookup) \list.sortBy { entry -> entry.0 }`.

The functions on them are on the standard library's [lists](../stdlib/prelude-lists), [maps and sets](../stdlib/prelude-maps), [array](../stdlib/array), [stack](../stdlib/stack), [queue](../stdlib/queue) and [trie](../stdlib/trie) pages. A stack, a queue and a trie are immutable, as a list is: each operation that adds or takes away gives back a new one, and the old one still holds what it held.

Each collection combines its elements into one value with `fold`, from a starting value, or with `reduce`, from its first element. An empty collection has no first element, so `reduce` gives `#some` of the result, or `#none` for an empty one. For a total, a list has `sum` and `product`, which give `0` and `1` for an empty list:

```nanyx
println([1, 2, 3, 4] \fold(0) { + })
println([1, 2, 3, 4, 5] \reduce { * })
def none: list(int) = []
println(none \reduce { * })
println([1, 2, 3, 4] \list.product)
```

This prints `10`, `#some(120)`, `#none` and `24`.

Each collection is made from a [sequence](../stdlib/prelude-sequences) with `fromSeq`, which pulls the whole sequence, and from a block that `emit`s its elements with `build`, which adds each element as it is emitted ([Building a collection](../advanced/sequences-and-queries#building-a-collection)):

```nanyx
def squares = (1..4) \seq.map { n -> n * n } \Array.fromSeq
def withZero = true

def numbers = Array.build {
  emit(1)
  emit(2)
  if withZero then emit(0)
}

println(squares)
println(numbers)
println(seq.take(0.., 3) \set.fromSeq)
```

This prints `[1, 4, 9, 16]`, `[1, 2, 0]` and `[0, 1, 2]`. An endless sequence such as `0..` must be cut with `seq.take` first. When the sequence's steps need a context, so does making the collection: `fromSeq` of a `[Console] seq(int)` requires `Console`.

```nanyx
import (
  nanyx/queue
  nanyx/trie
)

def jobs = Queue["build", "test"] \enqueue("deploy")
def commands = StringTrie["git" => 1, "git push" => 2, "go" => 3]

println(Queue.toList(jobs))
println(StringTrie.keys(commands))
println("{StringTrie.withPrefix(commands, "gi")}")
```

## Literal types

A square-bracket literal of plain items, `[1, 2, 3]`, is a `list(a)`, and one of `key => value` entries, `["a" => 1]`, is a `map(k, v)`, unless something says it's another type: the type expected where it's written, or the type's name written before the brackets. See [Syntax](#syntax).

Every collection can be iterated: it has the prelude's `Iterable` context, which a type gets by attaching `toSeq`, so a `for` loop walks it and a function can take any collection by requiring `Iterable(c)`. The context's associated type `elem` is the element type, and a function fixes it where it needs to, `[Iterable(c, elem = string)]` (see [Loops](./loops#looping-collections)).

# Syntax

The square-bracket literal works for any collection type that says how to be built from one. There are two ways to say which type a literal is:

```nanyx
def a: Array(int) = [1, 2, 3] -- from the expected type
def b = Array[1, 2, 3] -- from the type's name before the brackets
def s = set[1, 2, 2, 3] -- a set of 1, 2 and 3
def e: set(int) = [] -- an empty set
def m: map(string, int) = [] -- an empty map

println(set.length(s))
```

Writing the type's name before the brackets is like writing a record's type before its fields, `Point(x = 1, y = 2)`. The expected type can be a def's written type, the type a function takes, the result a lambda's type gives it, or a record field's type: `total([1, 2])` passes an array to `total: Array(int) -> int`, and `{ -> [1, 2] }` checked against `() -> Array(int)` gives back an array. With neither, plain items make a `list(a)` and `key => value` entries a `map(k, v)`; `[]` is the empty list.

A type has literals when it has an attached `fromArray`. The compiler collects the items into an `Array`, which knows its length, and passes it in; a map-like type is given an `Array` of `(key, value)` tuples:

```nanyx
type Bag(a) = protected (items: list(a))

def Bag.fromArray: Array(a) -> Bag(a) = { xs ->
  Bag(items = list.fromArray(xs))
}

def bag: Bag(int) = [1, 2, 2]
def other = Bag[3, 4]

println(list.length(bag.items))
```

The standard library's `list`, `set`, `map`, `Array`, `Slice`, `seq` (`seq[1, 2, 3]` is a sequence), `Stack`, `Queue`, `Trie` and mutable collections get their literals the same way, with nothing special about them. A `fromArray` that requires a context makes the literal require it: `MStack[1, 2]` needs `Memory`, so it is written inside a `memory` block. A literal of a type with no `fromArray` is an error (NYX0257).

Anything that can be iterated can be spread into a literal: a list, an array, a set, a map, a slice, a sequence, or a type of your own that attaches `toSeq`. A sequence is pulled when the literal is built, and spreading one whose steps need a context makes the literal need it:

```nanyx
import nanyx/slice

def s = set[1, 2]

println([...s, 3])
println(Array[0, ...1..3, ...Array.slice(Array[7, 8, 9], 1, 3)])
```

This prints `[1, 2, 3]` and `[0, 1, 2, 3, 8, 9]`. Spreading something that can't be iterated, such as a record, is an error (NYX0258).

For iteration constructs, see [Loops](./loops). For query-style collection programming, see [Sequences and queries](../advanced/sequences-and-queries).