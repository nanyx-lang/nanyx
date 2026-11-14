---
title: "Lazy sequences"
description: "One lazy sequence type, how it is represented, and how a function takes anything that can be iterated"
order: 25
---

> **Status: design in progress.** Nothing on this page is implemented. It replaces the `seq`/`gen` split that
> [Sequences and queries](../advanced/sequences-and-queries) and [Ranges](../tour/ranges) still describe. The runtime work is tracked in
> [#192](https://github.com/nanyx-lang/nanyx/issues/192).

# Summary

- There is one lazy sequence type, `seq(a)`, attached to as `Seq`. There is no separate `gen(a)` for sequences that
  may be infinite.
- A sequence is a value that, when asked, gives back either `#done` or its first element and the rest of the sequence.
  Asking does not change anything, so a sequence is an ordinary immutable value.
- A sequence whose steps need contexts says so in its type, `[k] seq(a)`, as any value does under
  [context-qualified types](./context-qualified-types).
- A function that takes "anything that can be iterated" requires a context, `Iterable(c, a)`, which a type gets by
  attaching a `toSeq`. A list is never a sequence by subtyping or by implicit conversion.
- Sequences are built with `Seq.unfold` or with a `seq { ... }` workflow whose `yield` keyword produces the elements.

# Where we are today

The docs describe several overlapping ideas, and only the type names exist in the compiler:

| Idea | Described in | In the compiler |
|------|--------------|-----------------|
| `seq(a)`, finite and with a pure iterator | [Sequences and queries](../advanced/sequences-and-queries), [Ranges](../tour/ranges) | The type of a bounded range, `1..10`, which only a `for` loop can walk ([Infer.fs:310](https://github.com/nanyx-lang/nanyx/blob/main/compiler/Nanyx.Compiler/Infer.fs#L310)) |
| `gen(a)`, possibly infinite or impure | the same pages | The type of an unbounded range, `0..` |
| `Sequence.map`, `Gen.take` | The standard library's `sequence` page, since deleted, as the library has no such module | Nothing. Attached definitions on `seq` are looked up as `Seq` ([BuiltinTypeNames.fs:18](https://github.com/nanyx-lang/nanyx/blob/main/compiler/Nanyx.Compiler/BuiltinTypeNames.fs#L18)), not `Sequence` |
| `Iter(a)` with `next` and an associated `elem` type | The standard library's `iter` page, since deleted | Nothing; it waits on [associated types](./type-members-and-reflection#associated-types) |
| `getIterator` | [Sequences and queries](../advanced/sequences-and-queries) | Nothing, and nothing defines it |
| `seq { yield ... }` | [Workflows](../advanced/workflows), [Ranges](../tour/ranges) | No `seq` builder |
| `Yield(b)` for `list.select` | The standard library's `collections` page, since replaced by [lists](../stdlib/prelude-lists), which has no `select` | Nothing ([#478](https://github.com/nanyx-lang/nanyx/issues/478)) |

A `for` loop accepts a `list`, `seq`, `gen`, `map` or `set` by name
([Infer.fs:325](https://github.com/nanyx-lang/nanyx/blob/main/compiler/Nanyx.Compiler/Infer.fs#L325)), and rejects a
`gen` unless something finite is walked beside it.

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

```nanyx
type Seq(a) = (moveNext: () -> #done | #some(a, Seq(a)))
```

This is the shape, not the declaration. `Seq` must be a nominal type, not a structural alias: attached definitions
such as `Seq.map` need a type of their own to attach to, the recursion goes through a name, and if the library keeps
the representation to itself it can change it later (chunking, fusion) without breaking anyone. `Seq` is already a
built-in type name, so the compiler can declare it.

It is a singly linked list whose tails are functions rather than cells. The closest prior art is OCaml's `Seq`
(`type 'a t = unit -> 'a node`, in the standard library since 4.07), whose functions are a good list to start from:
`unfold`, `map`, `filter`, `take`, `zip`, `fold_left`, `uncons`. Haskell lists, Scala's `LazyList`, Clojure's
`lazy-seq` and Scheme streams have the same shape but store each tail once it has been worked out. Rust, C#, Java and
Python iterators are the other approach: a `next()` that changes the iterator.

What this representation gives:

- **It is pure.** There is no hidden position. Calling `moveNext` on the same sequence twice gives the same answer,
  when its steps are pure.
- **Looking ahead and going back cost nothing.** Calling `moveNext` moves nothing; you move on by keeping the rest.
  A parser can keep a position and come back to it, where a stateful iterator needs a separate peekable wrapper.
- **It reads like a list.** A function matches on `#some(x, rest)` and recurses; `zip` pulls from both.

What it costs:

- **An allocation per element**: the closure for the rest and the `#some` it is returned in, where a stateful iterator
  changes one object in place. Reference counting can't reuse these the way it reuses a list held once, since each is
  a new closure. Fusing chains of `map` and `filter` in the compiler is the longer-term answer; it isn't needed to
  start.
- **`filter` must not grow the stack.** Its `moveNext` keeps pulling until an element passes, so it must do that with
  a tail call. [Tail calls](./tail-calls) are promised for closures, so a tail-recursive `filter` runs in constant
  stack.
- **Walking a sequence twice does its work twice.** Tails are not stored once worked out. Storing them would need a
  mutable cell in every node, and a sequence whose start is still held would keep every element worked out so far
  alive (the "holding onto the head" leak in Haskell and Clojure). `Seq.cache` gives the stored behaviour to the
  sequences that want it.
- **There is no cleanup hook.** A sequence over an open file that is abandoned halfway has no `Dispose`. Contexts help:
  a `[FileSystem] seq(string)` can't be pulled outside the scope that supplies `FileSystem`, so whatever opened the
  file is still there to close it.

## Effects

A sequence whose steps use a context has a [context-qualified type](./context-qualified-types): reading lines gives a
`[FileSystem] seq(string)`, and it can be pulled only where `FileSystem` is available. No second sequence type is
needed for impure sequences.

Because a lazy function runs its callback when the result is pulled, not when it is called, it moves the callback's
contexts into the type of its result, where an eager function requires them at the call:

```nanyx
list.map: [c] list(a), ([c] a -> b) -> list(b)          -- runs f now: needs c now
Seq.map:  ([c] seq(a)), ([d] a -> b) -> [c & d] seq(b)   -- runs f when pulled: the result needs c and d
Seq.fold: [c & d] ([c] seq(a)), b, ([d] b, a -> b) -> b  -- runs now again
```

A sequence whose steps read a `mut` cell requires `Memory`, so it can't leave its `memory` block
([Context escape](./context-escape)). The `range` example on the Ranges page, which builds a sequence from a `while`
loop over a cell, can never return; it becomes a `Seq.unfold`.

## Taking anything that can be iterated

Nanyx has no subtyping, so a `list(a)` is not a `seq(a)`. It doesn't need to be one: it needs to give one. A context
says that:

```nanyx
export context Iterable(c, a) = (toSeq: c -> seq(a))

def List.toSeq: list(a) -> seq(a) = ...
def Set.toSeq: set(a) -> seq(a) = ...
def Map.toSeq: map(k, v) -> seq((k, v)) = ...
def Seq.toSeq: seq(a) -> seq(a) = { s -> s }
```

- **A type gets the context by attaching `toSeq`.** The compiler builds an instance from a type's attached definitions
  by member name, as it builds `ToString(a)` from an attached `toString`
  ([Equality and well-known contexts](./equality-and-well-known-contexts)).
- **The element type needs no associated type.** It is the context's second parameter, and unification fixes it:
  resolving `Iterable(list(int), a)` against `List.toSeq: list(x) -> seq(x)` makes `a` an `int`. `FromString(a, e)`
  shows contexts with two parameters already work.
- **Functions that consume a sequence take any iterable; functions that make one return `seq(b)`**:

  ```nanyx
  Seq.fold: [Iterable(c, a) & d] c, b, ([d] b, a -> b) -> b
  Seq.map: [Iterable(c, a)] c, ([d] a -> b) -> [d] seq(b)
  ```

  So `[1, 2, 3] \Seq.map { * 2 }` needs no conversion, and gives back a lazy `seq(int)`.
- **Laziness is chosen by name.** `xs \map(f)` on a list still finds `List.map`, which is eager and gives back a list;
  `xs \Seq.map(f)` is lazy. This is the `List.map` and `Seq.map` split F# has.
- **A `for` loop walks anything `Iterable`.** The list of types in `Infer.fs` becomes the context, so a user's type can
  be walked by a `for` loop by attaching a `toSeq`.
- **`xs \List.toSeq` is always there** for code that wants to say it.

A function whose type is inferred gets this for free: `def walk = { items -> for items | item -> ... }` is inferred as
`[Iterable(s, a)] s -> ()`. Only a written signature pays for it, and how to make that shorter is an
[open question](#open-questions).

The compiler could instead insert `toSeq` wherever a `seq(a)` is expected and a `list(a)` is given. It won't:
unification has no direction, so whether a conversion was inserted would depend on the order types become known; it
would turn an eager computation lazy without saying so; and it is subtyping by another name, which comes back as soon
as someone wants a `list(list(a))` to be a `seq(seq(a))`.

## Making sequences

- **`Seq.unfold`** takes a state and a step that gives `#some(element, nextState)` or `#done`, the same step as
  `list.unfold`. `list.unfold` becomes `Seq.unfold(...) \Seq.toList`.
- **`seq { ... }`** is a [workflow](../advanced/workflows) whose `yield` keyword produces an element. A keyword
  receives the rest of the block as its continuation, and that continuation becomes the rest of the sequence, so the
  code after a `yield` doesn't run until the next element is pulled. Laziness comes from the workflow rewrite with no
  extra machinery. A loop inside the block whose body yields is run by the builder's `for` member (see
  [Keywords in loops](../advanced/workflows#keywords-in-loops)), so `seq` needs one.
- **Ranges** give sequences: `1..10` and `0..` are both `seq(int)` values that can be passed around and stored, not
  only walked by a `for` loop.

# Open questions

- **A shorter signature.** `[Iterable(s, a)] s -> ()` says the right thing, but `seq(a) -> ()` was easier to read. Two
  sources of inspiration:
  - Rust's `impl Trait` in argument position: a context named where a type is expected, with every parameter but its
    first, stands for a new type variable that the context constrains. `def walk: Iterable(a) -> ()` would mean
    `[Iterable(s, a)] s -> ()`. Each occurrence is its own variable, it is allowed only for parameters, and it works
    for any context (`def describe: ToString -> string`). It needs a rule that keeps a context's name and a type's
    name apart, since both are capitalised.
  - F#'s flexible types, `#seq<'a>`, which mark a parameter as taking the named type or anything that can stand in for
    it.

  Making a plain `seq(a)` parameter quietly mean "anything iterable" is ruled out: the annotation would no longer mean
  what it says.
- **Iterable values that need contexts.** If `c` is a `[k] seq(a)`, `toSeq` has to keep `k` in its result. Either the
  context's member carries `c`'s own requirements, or, to start with, `Iterable` takes only pure values and the
  `Seq` functions also take `[k] seq(a)` directly.
- **How a value's contexts reach its members.** A `[k] seq(a)` is a value whose `moveNext` needs `k`. How a value's
  requirements show on the functions inside it is for
  [context-qualified types](./context-qualified-types) and [#768](https://github.com/nanyx-lang/nanyx/issues/768) to
  settle.
- **The context's name.** The compiler matches the member, `toSeq`, by name; the context's own name is free.
  `Iterable` reads well in signatures; `ToSeq` would sit beside `ToString` and `FromString`.
- **An instance's second parameter.** Building an instance from attached definitions has only been used for contexts
  whose parameters are all fixed by the type. Check that it fills in `Iterable`'s `a` by unification as described.
- **Walking an unbounded range.** A default-linter rule for a `for` over only an unbounded range literal, or nothing,
  as for `while true`.
- **One `yield`.** The docs have three: the `seq { }` keyword, the `Yield(b)` context of `list.select`
  ([#478](https://github.com/nanyx-lang/nanyx/issues/478)), and the `ListBuilder` member behind
  [literal builders](../tour/collections#syntax). The last two are handler-style: they need a mutable cell or
  resumable handlers. With sequences, `select` is a `flatMap` whose body is a `seq { }`, and a literal builder could
  take a `seq(a)`, leaving one `yield`.

# Plan

1. Settle the open questions above, and bring the
   [Sequences and queries](../advanced/sequences-and-queries), [Ranges](../tour/ranges) and
   [Collections](../tour/collections) pages into line: drop `gen`. The standard library's pages follow its modules,
   so `Seq` and `Iterable` get pages when the library has them.
2. Type `a..` as a `seq(int)`, and remove `gen` from the type checker.
3. Give `seq(a)` its runtime representation, with ranges as values and `for` loops over them
   ([#192](https://github.com/nanyx-lang/nanyx/issues/192)).
4. The core functions: `Seq.unfold`, `map`, `filter`, `take`, `skip`, `takeWhile`, `fold`, `zip`, `toList`, and
   `List.toSeq`, `Set.toSeq`, `Map.toSeq`.
5. `Iterable(c, a)`, with `for` loops and the `Seq` functions that consume a sequence taking it.
6. The `seq { }` builder.
