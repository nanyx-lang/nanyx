---
title: "Lazy sequences"
description: "How seq(a) is represented, why there is one sequence type, how Iterable works, and how for loops and yield compile"
order: 19
---

What sequences do is described in [Sequences and queries](../advanced/sequences-and-queries),
[Ranges](../tour/ranges), [Workflows](../advanced/workflows#yield-and-sequences) and
[prelude: sequences](../stdlib/prelude-sequences). This page gives the design behind them and says how the compiler
builds them. What is still open is in issues: inferring `Iterable` for a def that loops over its parameter in a
sequence's block ([#3093](https://github.com/nanyx-lang/nanyx/issues/3093)), a shorter way to write such a parameter
([#2775](https://github.com/nanyx-lang/nanyx/issues/2775)), `seq.cache`
([#2705](https://github.com/nanyx-lang/nanyx/issues/2705)), a range of chars
([#2731](https://github.com/nanyx-lang/nanyx/issues/2731)), a `for` over only unbounded ranges
([#2777](https://github.com/nanyx-lang/nanyx/issues/2777)) and fusing chains of `map` and `filter`
([#2773](https://github.com/nanyx-lang/nanyx/issues/2773)). Compiling `seq { }` on async's state machines is on
[Effect handlers](../work-in-progress/effect-handlers).

# One type

There is one lazy sequence type, `seq(a)`, attached to as `seq` or `Sequence`, as lists are `list` or `List`; the prelude
writes `seq.map`. The prelude declares it as `Sequence`, a type as a user's is, and `seq` is its alias (`Seqs`,
`BuiltinTypeNames`); it is shown as `seq(int)`.

There is no separate `gen(a)` for sequences that may be infinite. The type checker can't prove a sequence ends:
`takeWhile` and `unfold` may or may not stop, and a `while` loop inside `seq { ... }` could claim to be finite with
nothing to check it. A split would also mix finiteness with purity, and purity is what contexts track. So `0..` is a
`seq(int)`, like `1..10`, and what a `gen` type would have caught, a `for` over only `0..`, is an error of its own
(NYX0217, see [`for` loops](#for-loops)).

# Representation

A sequence is a function that, when called, gives back the first element and the rest of the sequence, or `#done`:

```nanyx fragment
export type Sequence(a) = [k] protected (next: [k] () -> #next(a, Sequence(a)) | #done)
```

`Sequence` is a nominal type, not a structural alias: attached definitions such as `seq.map` need a type of their own to
attach to, the recursion goes through a name, and since the library keeps the representation to itself it can change
it later (chunking, fusion) without breaking anyone. It is protected, so a sequence is made with the library's
functions, and read with `s.next()`. It is generic in contexts ([Types generic in
contexts](../advanced/contexts#types-generic-in-contexts)), and its tail, `Sequence(a)` in its own body, is a `[k] seq(a)`
too, since a protected type can name itself.

It is a singly linked list whose tails are functions rather than cells. The closest prior art is OCaml's `Seq`
(`type 'a t = unit -> 'a node`, in the standard library since 4.07), whose functions were the list to start from:
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
  a new closure.
- **`filter` must not grow the stack.** Its `next` keeps pulling until an element passes, so it does that with a tail
  call. [Tail calls](./tail-calls) are guaranteed for closures, so a tail-recursive `filter` runs in constant stack.
- **Walking a sequence twice does its work twice.** Tails are not stored once worked out. Storing them would need a
  mutable cell in every node, and a sequence whose start is still held would keep every element worked out so far
  alive (the "holding onto the head" leak in Haskell and Clojure).
- **There is no cleanup hook.** A sequence over an open file that is abandoned halfway has no `Dispose`. Contexts help:
  a `[FileSystem] seq(string)` can't be pulled outside the scope that supplies `FileSystem`, so whatever opened the
  file is still there to close it.

# Effects

A sequence whose steps use a context says so in its type: reading lines gives a `[FileSystem] seq(string)`, which can
be pulled only where `FileSystem` is available, and a sequence whose steps wait would be an `[Async] seq(a)`, which is
what Kotlin calls a flow. No second sequence type is needed for impure sequences.

Pulling such a sequence again runs its steps, and their effects, again. Since its type says so, the official linter's
`repeatedPulls` rule warns when a def pulls one more than once on some path, and its fix pulls it once into a list
([Sequence with effects pulled more than once](../standard-tools/official-linter#sequence-with-effects-pulled-more-than-once)).
The rule is `RepeatedPulls`, in `LinterRepeatedPulls.fs`.

Because a lazy function runs its callback when the result is pulled, not when it is called, it moves the callback's
contexts into the type of its result, where an eager function requires them at the call:

```nanyx fragment
list.map: [c] list(a), ([c] a -> b) -> list(b)          -- runs f now: needs c now
seq.map:  ([c] seq(a)), ([d] a -> b) -> [c, d] seq(b)   -- runs f when pulled: the result needs c and d
seq.fold: [c, d] ([c] seq(a)), b, ([d] b, a -> b) -> b  -- runs now again
```

So a lambda given for `d`, a context variable that only the result carries, is checked as a constructor's field is:
outside the def or top-level value it is written in, which needn't have its contexts. A pure function, or a top-level
value, can make a `[Console] seq(int)` with `seq.map`, and whatever pulls it needs `Console`. Where a
`[Console] seq(int)` is expected, either of `[c, d]` may be the one that requires `Console`.

A sequence whose steps read a `mut` cell requires `Memory`, so it can't leave its `memory` block
([Memory](../advanced/memory#values-cannot-escape-memory-scope)).

# Taking anything that can be iterated

Nanyx has no subtyping, so a `list(a)` is not a `seq(a)`. It doesn't need to be one: it needs to give one. The
prelude's `Iterable` context says that:

```nanyx fragment
export context Iterable(c) = (elem: type, toSeq: c -> seq(elem))
```

The prelude attaches a `toSeq` to `list`, `Array`, `set`, `map` and `seq`, and the standard library's `Slice`, `Stack`,
`Queue` and mutable collections attach one too. A mutable collection's requires `Memory` and gives the elements it
holds when called.

- **A type gets the context by attaching `toSeq`.** The compiler builds an instance from a type's attached definitions
  by member name, as it builds `ToString(a)` from an attached `toString`
  ([Context synthesis](../advanced/contexts#context-synthesis)).
- **The element type is an [associated type](../advanced/contexts#associated-types),** `elem`. An instance's `toSeq`
  fixes it by unification: `List.toSeq: list(x) -> seq(x)` makes `Iterable(list(int)).elem` an `int`. A signature
  constrains only the container, and fixes the element type where it needs to, as in `[Iterable(c, elem = string)]`.
- **The `seq` functions take a sequence, not any iterable.** They keep their `([c] seq(a))` first parameter, so
  `file.lines` and every sequence whose steps need contexts work with them, and a list or a set goes into one through
  its `toSeq`: `[1, 2, 3] \list.toSeq \seq.map { * 2 }`. One function can't take both, since Nanyx has no overloading,
  and `Iterable` can't stand for a `[k] seq(a)`: `toSeq` gives a sequence that needs no contexts, so one read through
  it would be pulled without the members of `k`. `Iterable` takes only values whose sequence needs none.
- **What takes `Iterable`** is a `for` loop, a spread in a collection literal, and any function written to:
  `[Iterable(c)] c -> ...` reads its argument's elements with `toSeq(xs)`. Another of a function's contexts can name
  `elem`, so `[Iterable(c), ToString(elem)] c -> ()` prints each element.
- **A def that writes no type is inferred to require it.** In such a def's body, a `for` loop over a type nothing has
  settled reads it through `Iterable`, as `__toSeq(xs)` (`Loops.inferFor`), so
  `def walk = { items -> for items | item -> println("{item}") }` is
  `[Iterable(α, elem = β), ToString(β), Console] α -> ()`. Its type doesn't name `elem`, so generalising it quantifies
  the associated types of the contexts it is found to require along with its own variables, and requires `ToString` of
  `elem` too (`ContextInference.resolvePending`). An associated type the body fixes is written in the context:
  `{ xs -> for xs | x -> x + 1 }` requires `Iterable(c, elem = int)`. The backend finds an associated type in a def's
  body by the name its signature gives it (`TypedModule.AssociatedArguments`), so an inferred one is named after the
  context's own name for it, `elem`, as `[Iterable(c), ToString(elem)]` writes it. Where nothing can require
  `Iterable`, at the top level or in a sequence's step, which is checked outside the def it is written in, a type
  nothing has settled is still a list, or in a sequence's block the sequence itself (`Loops.readsThroughIterable`).
  A loop over a list, a sequence, a map or a set whose type is known where the loop is written still walks it directly;
  one whose type is settled only after the loop goes through `__toSeq`.

The compiler doesn't insert `toSeq` wherever a `seq(a)` is expected and a `list(a)` is given: unification has no
direction, so whether a conversion was inserted would depend on the order types become known; it would turn an eager
computation lazy without saying so; and it is subtyping by another name, which comes back as soon as someone wants a
`list(list(a))` to be a `seq(seq(a))`.

# Ranges

`a..b` is read as the prelude's `seq.range(a, b)`, and `a..` as `seq.from(a)`, of any type with an `Enumerable` (its
`succ`) and an `Ord` (`InferCalls`, `Seqs`). A range of `int`s is read as the prelude's `__intRange` or `__intFrom`
instead, which count and are passed no instances. Both are `seq` values that can be passed around and stored. The
bounds are inferred once, before either call, which is checked with each bound by a name of its own
(`RangeSyntax.bound`), so a range in a range's bound is not inferred again for each call
([#2307](https://github.com/nanyx-lang/nanyx/issues/2307)).

# `for` loops

A `for` loop walks a `list`, `seq`, `map` or `set` as it is, and reads anything else as the prelude's `__toSeq(xs)`,
`[Iterable(c)] c -> seq(elem)`, its sequence through `Iterable` (`Loops.inferFor`, `ForSyntax.toSeq`), so a user's
type can be walked by attaching a `toSeq`. A loop rejects a range with no end written in place unless something that
ends is walked beside it (NYX0217).

The backend walks a list and a range of `int`s written in place with the core IR's `ForEach`, counting with the
range's bounds rather than making the sequence (`Loops.inferFor` keeps the range as it is written;
`CoreIrLowerLoops`). A loop over a sequence value is a `while` loop instead (`CoreIrLowerLoops.forEachPulling`): each
time round it pulls the sequence, calling its function with the members of the contexts it carries, binds the element
and holds the rest for the next time round, so the body runs between pulls. A list or a range walked beside it is
walked in the same loop, and the loop ends when the first of them does.

# `yield`

A block with `yield` in it is a sequence, and `seq { ... }` makes one: the code after a `yield` doesn't run until the
next element is pulled. An ordinary workflow couldn't do it: under the one desugaring rule `yield x` is
`yield(x, rest)`, and `rest` needs the builder's context, which is gone by the time the sequence is pulled. So the
prelude's `Yield` context declares `yield` as a keyword of kind `#yield`, and any lambda with one in it, a block after
`seq` included, is rewritten into the sequence itself (`WorkflowDesugaring`): `yield x` with the rest `k` is
`seq.cons(x, { k })`, the end of the block `seq.empty()`, and a `for` loop whose body yields
`seq.append(seq.flatMap(__toSeq(items), body), seq.delay({ k }))`. A `yield` or a loop the sequence ends after makes no
rest: the last `yield x` is `__seqLast(x)`, the sequence of `x` alone, and the loop is the `seq.flatMap` alone. The loop's items are anything `Iterable`, as a
plain `for` loop's are: the checker reads `__toSeq(items)` as `__seqItself(items)`, the sequence as it is with whatever
its steps need, when the items are a sequence or a type nothing has settled (`Loops.walksAsSequence`), and through
their `Iterable` otherwise, so a list, an array, a set or a map is walked by its `toSeq`. A step is checked outside the
def it is written in, so it can't add `Iterable` to what an unannotated def requires: a def that loops over its
parameter in a sequence's block takes a sequence, unless its signature requires `Iterable` of it
([#3093](https://github.com/nanyx-lang/nanyx/issues/3093)). `seq` is an ordinary prelude
function, `seq.delay` by another name. Where the branches of an `if` join, a pure `seq.empty()` joins a branch whose sequence prints: the whole
carries what any branch's sequence does. There is no `yield` in a `while` loop: a `while` needs a cell, and a sequence
that reads one can't leave its `memory` block anyway.

# Pushing with `Emit`

A builder that takes every value at once has no use for laziness, so it doesn't take a sequence. `list.build`,
`list.select`, `Array.build`, `MArray.build`, `MList.build`, `set.build`, `set.select`, `map.build`, `Queue.build`,
`Stack.build` and `Slice.select` take a function that requires the prelude's `Emit(a)`, whose one member is
`emit: a -> ()`, and run it under a handler of their own:

```nanyx fragment
export def list.build: [c] ([c, Emit(a)] () -> ()) -> list(a) = { block ->
  memory {
    mut first = []
    mut last = []

    use Emit(
      emit = { y ->
        def cell = [y]

        match last
          | [] -> (first := cell)
          | previous -> __nanyx_list_set_tail(previous, cell)

        last := cell
      }
    ) in block()

    first
  }
}
```

`emit(x)` is an ordinary context call: the handler runs and returns, and the block carries on. So a `for` loop around
an `emit` stays a loop, and a value costs what the handler makes for it, here one list cell. `list.select` is
`list.build { for xs | x -> f(x) }`, and the other builders are `list.build` and the collection's `fromList`, but for
`MList.build`, whose handler adds to the `MList` itself, and `string.build`, whose handler copies each piece it is
given into one buffer that doubles when it is full, and makes the string of the bytes written at the end
([#2597](https://github.com/nanyx-lang/nanyx/issues/2597)). The handler's cells are captured inside the builder's own
`memory` block, which is allowed, and can't leave it ([Memory](../advanced/memory#values-cannot-escape-memory-scope)).

`Emit` is the pushing counterpart of `Yield`. Where `yield` makes the lambda it is in a sequence, and the code after it
waits to be pulled, `emit` hands the value over and runs on. `seq { ... }` and the `selectOne` functions, which stop
at the first value, keep taking a sequence. Nothing else about `Emit` is special: a function that requires it can be
passed around and called through other functions, and a user's own `use Emit(int)(emit = ...) in ...` handles it. A
lambda that yields given where a builder's function is expected would be a mismatch between a sequence's record and
`()`; the checker reports it at the lambda's first `yield` instead, saying to write `emit(x)` (`EmitLambdas`, called
from `InferCalls` before the lambda is inferred against its parameter, #2571).

# What a step costs

A sequence is persistent: pulling a step gives the element and a new sequence for the rest, and the step that was pulled
can be pulled again. So each step is made on the heap, and the allocator counts it (`nanyx run --memory`). Pulling one
element of `1..n` makes four blocks: the `Sequence` record, the closure its `next` holds (capturing where the range is
and where it ends), and the `#next(x, rest)` it gives, a tag box and its payload tuple. Each stage of `seq.map` or
`seq.filter` makes two more per element, its own `Sequence` record and closure for the rest. The `#next` it gives costs
nothing: the stage's `match` arm takes the `#next` it pulled apart and builds its own of the same size, so it writes
into the same blocks ([Reuse in place](reference-counting#reuse-in-place)). So `seq.range(1, n) \seq.toList` makes five
blocks per element, the list's cell included, and a `seq.filter` and a `seq.map` folded with `seq.fold` seven.

A `yield` block costs what its calls do. `seq { for 1..n | i -> yield i }` makes ten blocks per element: four for the
range, two for the step of `seq.flatMap` that gives the element, and four for `__seqLast(i)`, though the `#next` it gives
is reused by `seq.flatMap`'s. `seq.flatMap` walks each inner sequence itself rather than through `seq.append`, and
`seq.empty()` is one sequence that every call shares.

What isn't reused, and why:

- **A step's `Sequence` record and closure.** Reuse in place works inside one `match` arm: the arm takes a block apart
  and makes one of the same size, a list cell, a tag or a record (`CoreIrReuse`). A step's new record and closure are
  made by another function, `seq.map(rest, f)`, while the old ones belong to the consumer, which calls the closure,
  lending it its environment, and frees both once it has the next step. A closure is never made in a reused block
  either. Reusing them would take closures that own their environment when they are called at a last use, and the
  next step's record and closure made where that environment is let go.
- **The `#next` a consumer takes apart.** `seq.fold`, `seq.toList` and a `for` loop take each `#next` apart and make
  nothing of its size: their next `#next` is made by the producer's closure, a function of its own.
- **The `#next` the first stage gets.** A step at the source, a range's or `__seqLast`'s, makes a new `#next`; only a
  stage that takes one apart, such as `seq.map`, `seq.filter` or `seq.flatMap`, reuses it.
