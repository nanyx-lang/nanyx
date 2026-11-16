---
title: Collections
description: Lists, maps, sets, and mutability
order: 8
---

Collections are typically immutable in Nanyx, although [it is possible to use and create mutable versions](../advanced/memory). Mutable versions have an `M` prefix in their name (for example, `MList` vs `list`), and each is a module of its own: [`nanyx/mlist`](../stdlib/mlist), [`nanyx/mstack`](../stdlib/mstack) and [`nanyx/mref`](../stdlib/mref).

# Immutable vs mutable

Immutable collections are persistent data structures, while mutable collections are optimized for in-place updates.

```nanyx
import nanyx/mlist

-- Immutable list

def numbers = [1, 2, 3]

-- Mutable list (requires Memory context)

def addItems = memory {
  def items: MList(int) = MList.empty()
  items \add(1)
  items \add(2)
  MList.toList(items)
}
```

Use immutable collections by default. Reach for mutable variants when profiling shows a hot path that benefits from in-place updates.

An immutable list can be updated in place too, when nothing else can see the change: transforming a list you hold the only reference to, with `list.map` or a function of your own that takes it apart and rebuilds it, reuses its cells. See [Reference counting](../foundations/reference-counting#reuse-in-place).

# Common collection abstractions

Most of the time, Nanyx programs don't care very much about the specific implementation of a collection, just that it supports certain operations. For this reason, the standard library provides a number of common collection abstractions that can be implemented by any type:

| Collection type | Special alias | Characteristics |
|-----------------|-------------------|---------|
| `list(a)` | - | Indexable collections |
| `set(a)` | - | Membership checks |
| `map(k, v)` | - | Key/value lookup (a map is callable: `m(k)` gives `#some(v) \| #notFound`) |
| `Array(a)` | - | Fixed length, read by index in constant time (`import nanyx/array`; an array is callable: `xs(i)` gives `#some(a) \| #none`) |
| `Collection(a)` | - | Any in-memory collection |
| `Sequence(a)` | `seq(a)` | Finite, deterministic sequences |
| `Generator(a)` | `gen(a)` | Potentially infinite or non-deterministic sequences |

```nanyx
def lookup = ["a" => 1, "b" => 2]

def unique = set.fromList([1, 2, 2, 3])
```

The functions on them are on the standard library's [lists](../stdlib/prelude-lists), [maps and sets](../stdlib/prelude-maps) and [array](../stdlib/array) pages.

## Literal types

Today a square-bracket literal of plain items, `[1, 2, 3]`, is always a `list(a)`, and one of `key => value` entries, `["a" => 1]`, is always a `map(k, v)`. There is no set literal or array literal: a set is made from a list with `set.fromList`, and an array with `array.fromList`. Literals of any collection type, such as `def unique: set(int) = [1, 2, 2, 3]` or `Array[1, 2, 3]`, are planned, as described under [Syntax](#syntax).

Associated types for constrained collection capabilities are a work in progress; see [Type members and reflection](../work-in-progress/type-members-and-reflection#associated-types).

# Syntax

> **Planned** ([#1543](https://github.com/nanyx-lang/nanyx/issues/1543)). Today a literal is a `list(a)` or a `map(k, v)`, as described above.

The square-bracket literal is to work for any collection type that says how to be built from one. There are two ways to say which type a literal is:

```nanyx fragment
def a: Array(int) = [1, 2, 3]     -- from the expected type
def b = Array[1, 2, 3]            -- from the type's name before the brackets
def s = set[1, 2, 2, 3]           -- a set of 1, 2 and 3
def e: set(int) = []              -- an empty set
```

Writing the type's name before the brackets is like writing a record's type before its fields, `Point(x = 1, y = 2)`. With neither, plain items make a `list(a)` and `key => value` entries a `map(k, v)`, as today.

A type has literals when it has an attached `fromArray`. The compiler collects the items into an `Array`, which knows its length, and passes it in; a map-like type is given an `Array` of `(key, value)` tuples:

```nanyx fragment
type Bag(a) = protected (items: list(a))

def Bag.fromArray: Array(a) -> Bag(a) = { xs -> Bag(items = array.toList(xs)) }

def bag: Bag(int) = [1, 2, 2]
def other = Bag[3, 4]
```

The standard library's `set`, `map`, `Array` and mutable collections get their literals the same way, with nothing special about them. A `fromArray` that requires a context makes the literal require it: `MStack[1, 2]` needs `Memory`, so it is written inside a `memory` block. A list or an array can be spread into a literal, `Array[...xs, 4]`.

For iteration constructs, see [Loops](./loops). For query-style collection programming, see [Sequences and queries](../advanced/sequences-and-queries).