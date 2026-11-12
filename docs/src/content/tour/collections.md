---
title: Collections
description: Lists, maps, sets, and mutability
order: 8
---

Collections are typically immutable in Nanyx, although [it is possible to use and create mutable versions](../advanced/memory). Mutable versions live in `nanyx/collections.mutable` and, by convention, have an `M` prefix in their name (for example, `MList` vs `list`).

# Immutable vs mutable

Immutable collections are persistent data structures, while mutable collections are optimized for in-place updates.

```nanyx skip=#870
-- Immutable list

def numbers = [1, 2, 3]

-- Mutable list (requires $Memory context)

def addItems = {
  memory {
    def items: MList(int) = mut []
    items += 1
    items += 2
    MList.toList(items)
  }
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
| `Collection(a)` | - | Any in-memory collection |
| `Sequence(a)` | `seq(a)` | Finite, deterministic sequences |
| `Generator(a)` | `gen(a)` | Potentially infinite or non-deterministic sequences |

```nanyx
def lookup = ["a" => 1, "b" => 2]

def unique = set.fromList([1, 2, 2, 3])
```

The functions on them are on the [collections](../stdlib/collections), [map](../stdlib/map) and [set](../stdlib/set) pages.

## Literal types

Today a square-bracket literal of plain items, `[1, 2, 3]`, is always a `list(a)`, and one of `key => value` entries, `["a" => 1]`, is always a `map(k, v)`. There is no set literal: a set is made from a list with `set.fromList`. Literals that take their type from an annotation, such as `def unique: set(int) = [1, 2, 2, 3]`, are planned, with the builders described under [Syntax](#syntax).

Associated types for constrained collection capabilities are a work in progress; see [Type members and reflection](../work-in-progress/type-members-and-reflection#associated-types).

# Syntax

> **Planned.** Builders are not implemented yet: a literal is a `list(a)` or a `map(k, v)`, as described above. See [Maps and sets](../work-in-progress/maps-and-sets#open-questions).

Nanyx is to support a generalisable literal syntax for all collection types; the square bracket form will be available to any target type that implements the correct builder context:

```nanyx fragment
context ListBuilder(a) = (
  yield: a -> ()
)

def MyList.apply: ([ListBuilder(a)] () -> ()) -> MyList(a) = { f ->
  memory {
    def listRef = mut MyList()

    use (
      yield = { element -> listRef := listRef \add(element) }
    ) in f()

    listRef
  }
}

-- The square bracket literal syntax is now available for type MyList
def myList: MyList(int) = [1, 2, 3]
```

```nanyx fragment
context MapBuilder(k, v) = (
  `=>`: (k, v) -> ()
)

def MyMap.apply: ([MapBuilder(k, v)] () -> ()) -> MyMap(k, v) = { f ->
  memory {
    def mapRef = mut MyMap()

    use (
      `=>` = { k, v -> mapRef := mapRef \add(k, v) }
    ) in f()

    mapRef
  }
}

-- The square bracket literal syntax is now available for type MyMap
def myMap: MyMap(string, int) = ["a" => 1, "b" => 2]
```

This works because the square bracket syntax desugars to a call to the `apply` function on the target type, which provides the builder context that defines how the literal should be constructed:

```nanyx fragment
def myList: MyList(int) = [1, 2, 3]
-- Desugars to:
def myList: MyList(int) = MyList.apply {
  yield(1)
  yield(2)
  yield(3)
}
```

For iteration constructs, see [Loops](./loops). For query-style collection programming, see [Sequences and queries](../advanced/sequences-and-queries).