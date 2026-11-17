---
title: "Lazy sequences"
description: "One lazy sequence type, how it is represented, and how a function takes anything that can be iterated"
order: 25
---

> **Status: being built.** `seq(a)` is in the prelude with its core functions, and ranges are values of it
> ([prelude: sequences](../stdlib/prelude-sequences), [#192](https://github.com/nanyx-lang/nanyx/issues/192), done).
> A `for` loop walks any sequence ([#1698](https://github.com/nanyx-lang/nanyx/issues/1698), done).
> A block with `yield` in it is a sequence, and `seq { }` makes one ([#1699](https://github.com/nanyx-lang/nanyx/issues/1699), done).
> `Iterable(c)` is in the prelude, and a `for` loop walks anything `Iterable`
> ([#1802](https://github.com/nanyx-lang/nanyx/issues/1802), done).
> Still open: the `seq` functions taking any `Iterable` ([#2005](https://github.com/nanyx-lang/nanyx/issues/2005)), and
> inferring `[Iterable(s)]` for a def that writes no type ([#2003](https://github.com/nanyx-lang/nanyx/issues/2003)).

# Summary

- There is one lazy sequence type, `seq(a)`, attached to as `seq` or `Seq`, as lists are `list` or `List`; the
  prelude writes `seq.map`. The prelude declares it as `Seq`,
  a type as a user's is, and `seq` is its alias (`Seqs`, `BuiltinTypeNames`); it is shown as `seq(int)`. Its runtime
  form is the pull function below ([#192](https://github.com/nanyx-lang/nanyx/issues/192), done). There is no
  separate `gen(a)` for sequences that may be infinite.
- A sequence is a value that, when asked, gives back either `#done` or its first element and the rest of the sequence.
  Asking does not change anything, so a sequence is an ordinary immutable value.
- A sequence whose steps need contexts says so in its type, `[k] seq(a)`: `seq` is declared generic in contexts,
  `type Seq(a) = [k] protected (...)`, as any user's type can be
  ([Types generic in contexts](../advanced/contexts#types-generic-in-contexts),
  [#1525](https://github.com/nanyx-lang/nanyx/issues/1525), done). Its tail, `seq(a)` in its own body, is a
  `[k] seq(a)` too: a protected type can name itself ([#1663](https://github.com/nanyx-lang/nanyx/issues/1663), done).
  So a sequence whose steps wait would be an `[Async] seq(a)`, which is what Kotlin calls a flow
  ([Concurrency, "Streams"](./concurrency-and-crash-boundaries#streams-async-sequences), proposed).
- A function that takes "anything that can be iterated" requires a context, `Iterable(c)`, which a type gets by
  attaching a `toSeq` ([#1802](https://github.com/nanyx-lang/nanyx/issues/1802), done). A list is never a sequence by
  subtyping or by implicit conversion.
- Sequences are built with `seq.unfold`, from a list with `list.toSeq`, or with a `seq { ... }` block whose `yield`
  keyword produces the elements ([#1699](https://github.com/nanyx-lang/nanyx/issues/1699), done).
- A range is a sequence: `a..b` is read as the prelude's `seq.range(a, b)`, and `a..` as `seq.from(a)` (`Infer`,
  `Seqs`). A `for` loop over a range written in place still counts with its bounds, and doesn't make the sequence
  (`Loops.inferFor` keeps the range as it is written; `CoreIrLowerLoops`).

# Where we are today

Where each idea the docs described stands:

| Idea | Described in | In the compiler |
|------|--------------|-----------------|
| `seq(a)` | [Sequences and queries](../advanced/sequences-and-queries), [Ranges](../tour/ranges), [prelude: sequences](../stdlib/prelude-sequences) | The prelude's `Seq(a)`, of which ranges are values |
| `gen(a)` | | Gone: `0..` is a `seq(int)` |
| `Sequence.map`, `Gen.take` | | `seq.map`, `seq.take` and the rest, in the prelude |
| `Iter(a)` with `next` and an associated `elem` type | The standard library's `iter` page, since deleted | The prelude's `Iterable(c)`, with `toSeq` and an associated `elem` ([#1802](https://github.com/nanyx-lang/nanyx/issues/1802)) |
| `getIterator` | [Sequences and queries](../advanced/sequences-and-queries) | Nothing, and nothing defines it |
| `seq { yield ... }` | [Workflows](../advanced/workflows#yield-and-sequences), [Ranges](../tour/ranges) | The `#yield` keyword kind (`WorkflowDesugaring`), into the prelude's `seq.cons`, `seq.empty`, `seq.concatMap`, `seq.append` and `seq.delay` ([#1699](https://github.com/nanyx-lang/nanyx/issues/1699)) |
| `yield` in `list.select` | [Lists](../stdlib/prelude-lists), [Lambdas](../tour/lambdas-and-higher-order-functions#contextual-lambdas) | A lambda that yields is a sequence, which `list.select` flattens ([#1699](https://github.com/nanyx-lang/nanyx/issues/1699)) |

A `for` loop walks a `list`, `seq`, `map` or `set` as it is, and reads anything else as the prelude's `__toSeq(xs)`,
its sequence through `Iterable` (`Loops.inferFor`, `ForSyntax.toSeq`). A type nothing has settled is still a list,
unless a signature around the loop requires `Iterable` of it
([#2003](https://github.com/nanyx-lang/nanyx/issues/2003)). A loop rejects a range with no end written in place unless
something that ends is walked beside it (NYX0217). The backend walks a list and a range written
in place with the core IR's `ForEach`. A loop over a sequence value is a `while` loop instead
(`CoreIrLowerLoops.forEachPulling`): each time round it pulls the sequence, calling its function with the members of
the contexts it carries, binds the element and holds the rest for the next time round, so the body runs between pulls.
A list or a range walked beside it is walked in the same loop, and the loop ends when the first of them does
([#1698](https://github.com/nanyx-lang/nanyx/issues/1698), done).

# Design

## One type: no `gen`

The split between a finite `seq` and a possibly infinite `gen` can't be kept. The type checker can't prove a sequence
ends: `takeWhile` and `unfold` may or may not stop, and the `range` example on the Ranges page, a `while` loop inside
`seq { ... }`, claims to be finite with nothing to check it. The split also mixed finiteness with purity ("a `gen`
iterator can be impure"), and purity is already what contexts track.

So `0..` is a `seq(int)`, like `1..10`. What the split bought was an error on `for 0.. | i -> ...`; a rule in the
default linter can flag a `for` whose only input is an unbounded range literal (see
[open questions](#open-questions)). An infinite `for` that ends with a `return` is as legitimate as a `while` loop
that ends the same way.

## Representation

A sequence is a function that, when called, gives back the first element and the rest of the sequence, or `#done`:

```nanyx fragment
export type Seq(a) = [k] protected (next: [k] () -> #next(a, Seq(a)) | #done)
```

`Seq` is a nominal type, not a structural alias: attached definitions such as `seq.map` need a type of their own to
attach to, the recursion goes through a name, and if the library keeps the representation to itself it can change it
later (chunking, fusion) without breaking anyone. It is protected, so a sequence is made with the library's functions,
and read with `s.next()`.

It is a singly linked list whose tails are functions rather than cells. The closest prior art is OCaml's `Seq`
(`type 'a t = unit -> 'a node`, in the standard library since 4.07), whose functions are a good list to start from:
`unfold`, `map`, `filter`, `take`, `zip`, `fold_left`, `uncons`. Haskell lists, Scala's `LazyList`, Clojure's
`lazy-seq` and Scheme streams have the same shape but store each tail once it has been worked out. Rust, C#, Java and
Python iterators are the other approach: a `next()` that changes the iterator.

What this representation gives:

- **It is pure.** There is no hidden position. Calling `next` on the same sequence twice gives the same answer,
  when its steps are pure.
- **Looking ahead and going back cost nothing.** Calling `next` moves nothing; you move on by keeping the rest.
  A parser can keep a position and come back to it, where a stateful iterator needs a separate peekable wrapper.
- **It reads like a list.** A function matches on `#next(x, rest)` and recurses; `zip` pulls from both.

What it costs:

- **An allocation per element**: the closure for the rest and the `#next` it is returned in, where a stateful iterator
  changes one object in place. Reference counting can't reuse these the way it reuses a list held once, since each is
  a new closure. Fusing chains of `map` and `filter` in the compiler is the longer-term answer; it isn't needed to
  start.
- **`filter` must not grow the stack.** Its `next` keeps pulling until an element passes, so it must do that with
  a tail call. [Tail calls](./tail-calls) are promised for closures, so a tail-recursive `filter` runs in constant
  stack.
- **Walking a sequence twice does its work twice.** Tails are not stored once worked out. Storing them would need a
  mutable cell in every node, and a sequence whose start is still held would keep every element worked out so far
  alive (the "holding onto the head" leak in Haskell and Clojure). A `Seq.cache`, not written yet, would give the stored
  behaviour to the sequences that want it.
- **There is no cleanup hook.** A sequence over an open file that is abandoned halfway has no `Dispose`. Contexts help:
  a `[FileSystem] seq(string)` can't be pulled outside the scope that supplies `FileSystem`, so whatever opened the
  file is still there to close it.

## Effects

A sequence whose steps use a context has a [type generic in contexts](../advanced/contexts#types-generic-in-contexts): reading lines gives a
`[FileSystem] seq(string)`, and it can be pulled only where `FileSystem` is available. No second sequence type is
needed for impure sequences.

Because a lazy function runs its callback when the result is pulled, not when it is called, it moves the callback's
contexts into the type of its result, where an eager function requires them at the call:

```nanyx
list.map: [c] list(a), ([c] a -> b) -> list(b)          -- runs f now: needs c now
Seq.map:  ([c] seq(a)), ([d] a -> b) -> [c, d] seq(b)   -- runs f when pulled: the result needs c and d
Seq.fold: [c, d] ([c] seq(a)), b, ([d] b, a -> b) -> b  -- runs now again
```

So a lambda given for `d`, a context variable that only the result carries, is checked as a constructor's field is:
outside the def or top-level value it is written in, which needn't have its contexts. A pure function, or a top-level
value, can make a `[Console] seq(int)` with `seq.map`, and whatever pulls it needs `Console` (#1738). Where a
`[Console] seq(int)` is expected, either of `[c, d]` may be the one that requires `Console`.

A sequence whose steps read a `mut` cell requires `Memory`, so it can't leave its `memory` block
([Memory](../advanced/memory#values-cannot-escape-memory-scope)). So the `range` example on the Ranges page is a recursive `seq { }`, not a `while`
loop over a cell.

## Taking anything that can be iterated

Nanyx has no subtyping, so a `list(a)` is not a `seq(a)`. It doesn't need to be one: it needs to give one. A context
says that:

```nanyx
export context Iterable(c) = (elem: type, toSeq: c -> seq(elem))

def list.toSeq: list(a) -> seq(a) = ...
def set.toSeq: set(a) -> seq(a) = ...
def map.toSeq: map(k, v) -> seq((k, v)) = ...
def seq.toSeq: seq(a) -> seq(a) = { s -> s }
```

Built ([#1802](https://github.com/nanyx-lang/nanyx/issues/1802)): the prelude declares it, with a `toSeq` for `list`,
`Array`, `set`, `map` and `seq`, and the standard library's `Slice`, `Stack`, `Queue` and mutable collections attach
one too. A mutable collection's requires `Memory` and gives the elements it holds when called.

- **A type gets the context by attaching `toSeq`.** The compiler builds an instance from a type's attached definitions
  by member name, as it builds `ToString(a)` from an attached `toString`
  ([Context synthesis](../advanced/contexts#context-synthesis)).
- **The element type is an [associated type](./type-members-and-reflection#associated-types),** `elem`, decided on
  2026-11-17 ([#1802](https://github.com/nanyx-lang/nanyx/issues/1802)). An instance's `toSeq` fixes it by
  unification: `List.toSeq: list(x) -> seq(x)` makes `Iterable(list(int)).elem` an `int`. A signature constrains only
  the container, and fixes the element type where it needs to, as in `[Iterable(c, elem = string)]`.
- **The `seq` functions take a sequence, not any iterable** (decided on 2026-11-17,
  [#1802](https://github.com/nanyx-lang/nanyx/issues/1802)). They keep their `([c] seq(a))` first parameter:

  ```nanyx
  seq.fold: [c, d] ([c] seq(a)), b, ([d] b, a -> b) -> b
  seq.map: ([c] seq(a)), ([d] a -> b) -> [c, d] seq(b)
  ```

  So `file.lines` and every sequence whose steps need contexts work with them, and a list or a set goes into one
  through its `toSeq`: `[1, 2, 3] \list.toSeq \seq.map { * 2 }`. One function can't take both, since Nanyx has no
  overloading, and `Iterable` can't stand for a `[k] seq(a)`: `toSeq` gives a sequence that needs no contexts, so one
  read through it would be pulled without the members of `k`. `Iterable` takes only values whose sequence needs none.
  Carrying a sequence's contexts through `Iterable`, so the `seq` functions could take any `Iterable`, is
  [#2005](https://github.com/nanyx-lang/nanyx/issues/2005).
- **What takes `Iterable`** is a `for` loop, a spread in a collection literal
  ([#1869](https://github.com/nanyx-lang/nanyx/issues/1869)), and any function written to: `[Iterable(c)] c -> ...`
  reads its argument's elements with `toSeq(xs)`.
- **A `for` loop walks anything `Iterable`.** A list, a sequence, a map and a set are walked as they are, and anything
  else through the prelude's `__toSeq: [Iterable(c)] c -> seq(elem)` (`Loops.inferFor`), so a user's type can be walked
  by a `for` loop by attaching a `toSeq`. Another of a function's contexts can name `elem`, so
  `[Iterable(c), ToString(elem)] c -> ()` prints each element.

A function whose type is inferred should get this for free: `def walk = { items -> for items | item -> ... }` should
be inferred as `[Iterable(s)] s -> ()`. It isn't yet, since generalising the def would have to quantify `elem`, which
its type doesn't name ([#2003](https://github.com/nanyx-lang/nanyx/issues/2003)); until then a type nothing settles is
walked as a list. A written signature pays for it, and how to make that shorter is an
[open question](#open-questions).

The compiler could instead insert `toSeq` wherever a `seq(a)` is expected and a `list(a)` is given. It won't:
unification has no direction, so whether a conversion was inserted would depend on the order types become known; it
would turn an eager computation lazy without saying so; and it is subtyping by another name, which comes back as soon
as someone wants a `list(list(a))` to be a `seq(seq(a))`.

## Making sequences

- **`Seq.unfold`** takes a state and a step that gives `#some(element, nextState)` or `#done`, the same step as
  `list.unfold`. `list.unfold` becomes `Seq.unfold(...) \Seq.toList`.
- **`seq { ... }`** is a block whose `yield` produces an element, and the code after a `yield` doesn't run until the
  next element is pulled. Built ([#1699](https://github.com/nanyx-lang/nanyx/issues/1699)); the
  [Workflows](../advanced/workflows#yield-and-sequences) page describes it. An ordinary workflow couldn't do it: under
  the one desugaring rule `yield x` is `yield(x, rest)`, and `rest` needs the builder's context, which is gone by the
  time the sequence is pulled. So `yield` is a keyword of kind `#yield`, and any lambda with one in it, a block after
  `seq` included, is rewritten into the sequence itself (`WorkflowDesugaring`): `yield x` with the rest `k` is
  `seq.cons(x, { k })`, the end of the block `seq.empty()`, and a `for` loop whose body yields
  `seq.append(seq.concatMap(items, body), seq.delay({ k }))`. `seq` is an ordinary prelude function, `seq.delay` by
  another name. Where the branches of an `if` join, a pure `seq.empty()` joins a branch whose sequence prints: the
  whole carries what any branch's sequence does
  ([#1908](https://github.com/nanyx-lang/nanyx/issues/1908)). There is no `yield` in a `while` loop: a `while` needs
  a cell, and a sequence that reads one can't leave its `memory` block anyway.
- **Ranges** give sequences: `1..10` and `0..` are both `seq(int)` values that can be passed around and stored, not
  only walked by a `for` loop.

# Open questions

- **A shorter signature.** `[Iterable(s)] s -> ()` says the right thing, but `seq(a) -> ()` was easier to read. Two
  sources of inspiration:
  - Rust's `impl Trait` in argument position: a context named where a type is expected, with every parameter but its
    first, stands for a new type variable that the context constrains. `def walk: Iterable -> ()` would mean
    `[Iterable(s)] s -> ()`. Each occurrence is its own variable, it is allowed only for parameters, and it works
    for any context (`def describe: ToString -> string`). A context's name where a type goes is NYX0247 today
    ([#1447](https://github.com/nanyx-lang/nanyx/issues/1447)), so this would give that position a meaning rather
    than need a rule to tell the names apart.
  - F#'s flexible types, `#seq<'a>`, which mark a parameter as taking the named type or anything that can stand in for
    it.

  Making a plain `seq(a)` parameter quietly mean "anything iterable" is ruled out: the annotation would no longer mean
  what it says.
- **Iterable values that need contexts.** `Iterable` takes only values whose sequence needs no contexts, so the `seq`
  functions take a sequence ([#1802](https://github.com/nanyx-lang/nanyx/issues/1802)). Whether and how to carry a
  sequence's contexts through `toSeq` is [#2005](https://github.com/nanyx-lang/nanyx/issues/2005).
- **Walking an unbounded range.** A default-linter rule for a `for` over only an unbounded range literal, or nothing,
  as for `while true`.
- ~~**One `yield`.**~~ Decided and built ([#1699](https://github.com/nanyx-lang/nanyx/issues/1699)): the prelude's
  `Yield` context declares the `#yield` keyword, and `list.select` takes a lambda that yields, a sequence it flattens,
  so it's written `xs \list.select { x -> if x > 0 then yield x }`. The call form, `yield(x)`, is gone. There was no
  `ListBuilder` member left to move: [literal builders](../tour/collections#syntax) take an `Array` through an attached
  `fromArray`.
# Plan

1. Settle the open questions above. Done: the [Sequences and queries](../advanced/sequences-and-queries),
   [Ranges](../tour/ranges) and [Collections](../tour/collections) pages have no `gen`, and `seq` has its
   [stdlib page](../stdlib/prelude-sequences). The Ranges page's ranges of other types wait on
   [#1700](https://github.com/nanyx-lang/nanyx/issues/1700).
2. ~~Type `a..` as a `seq(int)`, and remove `gen` from the type checker.~~ Done
   ([#192](https://github.com/nanyx-lang/nanyx/issues/192)).
3. ~~Give `seq(a)` its runtime representation, with ranges as values, and `for` loops over any sequence.~~ Done
   ([#192](https://github.com/nanyx-lang/nanyx/issues/192), [#1698](https://github.com/nanyx-lang/nanyx/issues/1698)).
4. ~~The core functions.~~ Done: `seq.empty`, `range`, `from`, `unfold`, `map`, `filter`, `take`, `skip`, `takeWhile`,
   `fold`, `toList`, `cons`, `delay`, `append`, `concatMap`, `zip`, and `list.toSeq`, `set.toSeq` and `map.toSeq`
   ([#1795](https://github.com/nanyx-lang/nanyx/issues/1795)).
5. ~~`Iterable(c)`, with `for` loops taking it.~~ Done ([#1802](https://github.com/nanyx-lang/nanyx/issues/1802)).
   The `seq` functions taking it is [#2005](https://github.com/nanyx-lang/nanyx/issues/2005).
6. ~~The `seq { }` builder, by the `#yield` keyword kind, with `yield` written bare and `list.select` moved to it.~~
   Done ([#1699](https://github.com/nanyx-lang/nanyx/issues/1699)). Each collection type is made from a sequence
   with `fromSeq` and from a `yield` block with `build`, `Array.build { yield 1 }`
   ([#1906](https://github.com/nanyx-lang/nanyx/issues/1906)).
