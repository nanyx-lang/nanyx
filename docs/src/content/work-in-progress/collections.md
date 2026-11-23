---
title: "Collections"
description: "The same core functions on every collection type, and arrays that update in place"
order: 10
---

Someone who knows `list.select` should be able to expect `array.select`, with the same name, argument order and
meaning. `list` and `seq` have about fifty functions each and the other collections far fewer, with different names
for the same thing in places. This page is the plan to even them out. The maintainer decided its shape in
[#1672](https://github.com/nanyx-lang/nanyx/issues/1672): one name for a collection's size, one core set of functions
and which types get which, and one indexed read. The [table](#which-type-has-which-function) shows what each type has
and what it is missing, and the gaps are [filed as issues](#the-work). A few names are [open](#open-questions).

# The decisions

## `length` is the size

`length` is the name for how many elements a collection holds, on every collection: `list`, `Array`, `Slice`, `set`,
`map` (its entries), `MList`, `MArray`, `MStack`, `Stack`, `Queue`, the tries, and `seq` where it ends. There is no
`count` alias. `list.count` and `seq.count` count the elements a test holds for, which is a different function; whether
they stay is an [open question](#open-questions).

## The core set, and which types get which

- **Every collection gets the core set:** `map`, `filter`, `filterMap`, `fold`, `select`, `selectOne`, `find`,
  `findIndex`, `any`, `all`, `contains`, `length`, `isEmpty`, `toList`, `fromList` and `fromArray`.
- **The ordered types,** `list`, `Array`, `Slice` and `seq`, also get `reverse`, `sort`, `sortBy`, `sortWith`, `take`,
  `skip`, `zip` and `scan`. `seq` leaves out `reverse` and the sorts, which would have to read and keep the whole
  sequence before giving anything ([#2020](https://github.com/nanyx-lang/nanyx/issues/2020)); `list` has them.
- **`set` and `map`** get only the ones that don't depend on order. A map's functions take the key and the value as two
  arguments, as `map.filter` and `map.fold` do.
- **The mutable types,** `MList`, `MArray`, `MStack` and `MSlice`, get the ones that only read, plus in-place `sort`,
  `reverse` and `fill`. "Only read" is taken to mean the core functions that give a value, not a new collection:
  `fold`, `find`, `findIndex`, `selectOne`, `any`, `all`, `contains`, `length`, `isEmpty` and `toList`, with `fromList`
  and `fromArray` to make one. For `map`, `filter` and the rest, take a snapshot with `toList` or `toArray` first.

Every function has the same name, argument order and meaning on each type that has it, and a function that makes a
new collection gives one of the same type, except that a `Slice`'s give an `Array`, as `Slice.map` does. Each is
written out per type, which is simpler and faster today, rather than once over the [`Iterable`](./lazy-sequences)
context.

The decision named "the queue" among the mutable types, but `Queue` is immutable, as `Stack` and the tries are. Where
those three fit is [open](#open-questions).

## `at` is the indexed read, and `#notFound` its missing case

The indexed read is `at` on every collection, giving `#some(a) | #notFound`, the missing tag `map.get` uses, for a
negative index and one past the end. Calling an array, `xs(i)`, is `array.at`, so it gives `#notFound` too.

# Which type has which function

On `main` at 4f63f796, from the attached definitions in `stdlib/nanyx/*.nyx` (`export def list.map` and the like),
updated as [the work](#the-work) lands:

- ✓ it has it;
- **+** it should have it, and doesn't yet: [filed](#the-work);
- – it shouldn't have it;
- ? not decided: see [open questions](#open-questions).

| | `list` | `Array` | `Slice` | `seq` | `set` | `map` | `MList` | `MArray` | `MSlice` | `MStack` | `Stack` | `Queue` | `Trie` |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| `map` | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | – | – | – | – | ? | ? | ? |
| `filter` | ✓ | **+** | ✓ | ✓ | ✓ | ✓ | – | – | – | – | ? | ? | ? |
| `filterMap` | ✓ | **+** | ✓ | ✓ | ✓ | ✓ | – | – | – | – | ? | ? | ? |
| `fold` | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | **+** | **+** | **+** | **+** | ? | ? | ? |
| `select` | ✓ | **+** | ✓ | ✓ | ✓ | ? | – | – | – | – | ? | ? | ? |
| `selectOne` | ✓ | **+** | ✓ | ✓ | ? | ? | **+** | **+** | **+** | **+** | ? | ? | ? |
| `find` | ✓ | **+** | ✓ | ✓ | ? | ? | **+** | **+** | **+** | **+** | ? | ? | ? |
| `findIndex` | ✓ | **+** | ✓ | ✓ | – | – | **+** | **+** | **+** | **+** | ? | ? | ? |
| `any` | ✓ | **+** | ✓ | ✓ | ✓ | ✓ | **+** | **+** | **+** | **+** | ? | ? | ? |
| `all` | ✓ | **+** | ✓ | ✓ | ✓ | ✓ | **+** | **+** | **+** | **+** | ? | ? | ? |
| `contains` | ✓ | **+** | ✓ | ✓ | ✓ | ? | **+** | **+** | **+** | **+** | ? | ? | ? |
| `length` | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| `isEmpty` | ✓ | **+** | ✓ | ✓ | ✓ | ✓ | **+** | **+** | **+** | ✓ | ✓ | ✓ | ✓ |
| `toList` | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| `fromList` | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | **+** | ✓ | ✓ | ✓ | ✓ |
| `fromArray` | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | **+** | ✓ | ✓ | ✓ | ✓ |
| `at` | ✓ | ✓ | ✓ | ✓ | – | ? | ✓ | ✓ | ✓ | ? | ? | ? | ? |
| `reverse` | ✓ | **+** | ✓ | – | – | – | **+** | **+** | ✓ | **+** | ? | ? | ? |
| `sort` | ✓ | **+** | ✓ | – | – | – | **+** | ✓ | ✓ | **+** | ? | ? | ? |
| `sortBy` | ✓ | **+** | ✓ | – | – | – | **+** | ✓ | ✓ | **+** | ? | ? | ? |
| `sortWith` | ✓ | **+** | ✓ | – | – | – | **+** | ✓ | ✓ | **+** | ? | ? | ? |
| `take` | ✓ | **+** | ✓ | ✓ | – | – | – | – | – | – | ? | ? | ? |
| `skip` | ✓ | **+** | ✓ | ✓ | – | – | – | – | – | – | ? | ? | ? |
| `zip` | ✓ | **+** | ✓ | ✓ | – | – | – | – | – | – | ? | ? | ? |
| `scan` | ✓ | **+** | ✓ | ✓ | – | – | – | – | – | – | ? | ? | ? |
| `fill` | – | – | – | – | – | – | **+** | **+** | ✓ | **+** | – | – | – |

The `Trie` column stands for `StringTrie` too, which has the same functions. The table leaves out what isn't in the core set, such as `list`'s
`groupBy`, `windows` and `sum`, `set`'s `union`, `MArray`'s `swap`, or `reduce`.

# The work

One issue per type, or per group of functions:

| Issue | What | Stage |
|---|---|---|
| [#2187](https://github.com/nanyx-lang/nanyx/issues/2187) | `Array`: the core and ordered functions it is missing | Ready |
| [#2188](https://github.com/nanyx-lang/nanyx/issues/2188) | `Array` functional but in place: `map`, `filter`, `reverse` and the sorts reuse an exclusive array's block | Ready, after #2187 |
| [#2189](https://github.com/nanyx-lang/nanyx/issues/2189) | `Slice`: the core and ordered functions it is missing, and `ofArray` renamed `fromArray` | Done |
| [#2192](https://github.com/nanyx-lang/nanyx/issues/2192) | `MList`, `MArray`, `MStack` and `MSlice`: the read-only core functions, and in-place `sort`, `reverse` and `fill` | Ready |
| [#2193](https://github.com/nanyx-lang/nanyx/issues/2193) | Which core functions `Stack`, `Queue` and the tries get | Planning |
| [#2194](https://github.com/nanyx-lang/nanyx/issues/2194) | `map`'s `contains`, `at` and `select`, `find` on a set or map, and `list.count` | Planning |

## Functional but in place for `Array`

An `Array` is one block, and every element sits in a slot of the same size, so a function that makes a new array from
an old one can write into the old one's block when nothing else refers to it, and copy it otherwise, as Lean 4's
`Array` does. That is what [Principles](../meta/principles#functional-but-in-place) promises. The standard library
checks this at run time with the `__nanyx_is_exclusive` intrinsic, for `MList.toList`
([#1671](https://github.com/nanyx-lang/nanyx/issues/1671)); [#2188](https://github.com/nanyx-lang/nanyx/issues/2188)
uses it for `array.map`, `filter`, `reverse` and the sorts. It never changes what a program means, only what it
allocates.

# Open questions

- **`Stack`, `Queue` and the tries** ([#2193](https://github.com/nanyx-lang/nanyx/issues/2193)). They are immutable, so
  the mutable group doesn't fit them. Are `Stack` and `Queue` ordered collections that get the core set, and `at`? Are
  the tries keyed collections that get what `map` gets? Should `Trie.get` give `#notFound`, as `map.get` does? Does
  `MStack` have `at`, when a stack is read from its top?
- **`map` and `set`'s remaining names** ([#2194](https://github.com/nanyx-lang/nanyx/issues/2194)). Is `map.contains`
  a key test (renaming `containsKey`), an entry test, or left out? Is `map.get` renamed `map.at`? What does
  `map.select` give? Do a set and a map have `find` and `selectOne`, whose answer depends on the order when several
  elements pass?
- **`list.count` and `seq.count`** ([#2194](https://github.com/nanyx-lang/nanyx/issues/2194)). They count the elements
  a test holds for. `count` is not a size anywhere; do these go too, for `xs \filter(test) \length`?
- **Is `MArray` still needed?** Once arrays update in place when they are exclusive, much of what `MArray` is for can
  be written with `Array`. Lean has no mutable array at all. This isn't decided here.
