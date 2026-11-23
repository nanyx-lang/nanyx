---
title: "Lazy sequences"
description: "What is still open about seq(a) and Iterable: inferring Iterable, and a shorter signature"
order: 25
---

Sequences, ranges, `seq { }` and `yield` are described for users in
[Sequences and queries](../advanced/sequences-and-queries), [Ranges](../tour/ranges),
[Workflows](../advanced/workflows#yield-and-sequences) and [prelude: sequences](../stdlib/prelude-sequences), and how
they are built, with the design behind them, in [Lazy sequences](../deep-dives/lazy-sequences) in the Deep dives
section. This page keeps what is still open.

# Plan

1. **Inferring `Iterable` for a def that writes no type**
   ([#2003](https://github.com/nanyx-lang/nanyx/issues/2003)). `def walk = { items -> for items | item -> ... }` should
   be inferred as `[Iterable(s)] s -> ()`. It isn't, since generalising the def would have to quantify `elem`, which its
   type doesn't name; a type nothing settles is walked as a list, unless a signature around the loop requires
   `Iterable` of it. A written signature pays for it, and how to make that shorter is an
   [open question](#open-questions).
2. **A range of characters** waits on a character type ([#2196](https://github.com/nanyx-lang/nanyx/issues/2196)).
3. **`seq.cache`,** not written yet, would give the sequences that want it stored tails, so that walking one twice
   does its work once.
4. **Later:** fusing chains of `map` and `filter` in the compiler, so that a chain doesn't allocate a closure and a
   `#next` per element at each step.

# Open questions

- **A shorter signature.** `[Iterable(s)] s -> ()` says the right thing, but `seq(a) -> ()` was easier to read. Two
  sources of inspiration:
  - Rust's `impl Trait` in argument position: a context named where a type is expected, with every parameter but its
    first, stands for a new type variable that the context constrains. `def walk: Iterable -> ()` would mean
    `[Iterable(s)] s -> ()`. Each occurrence is its own variable, it is allowed only for parameters, and it works
    for any context (`def describe: ToString -> string`). A context's name where a type goes is NYX0247
    ([#1447](https://github.com/nanyx-lang/nanyx/issues/1447)), so this would give that position a meaning rather
    than need a rule to tell the names apart.
  - F#'s flexible types, `#seq<'a>`, which mark a parameter as taking the named type or anything that can stand in for
    it.

  Making a plain `seq(a)` parameter quietly mean "anything iterable" is ruled out: the annotation would no longer mean
  what it says.
- **Walking an unbounded range.** The compiler rejects a `for` whose only inputs are ranges with no end (NYX0217). But
  an infinite `for` that ends with a `return` is as legitimate as a `while` loop that ends the same way, so this could
  be a default-linter rule instead, as for `while true`.
