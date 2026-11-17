---
title: "Compiler optimisations"
description: "How Nanyx improves generated code"
order: 11
---

Nanyx applies compiler optimisations automatically during compilation. These optimisations are intended to make programs faster and smaller without changing program meaning.

# Optimisation model

Nanyx optimisations are implementation details of the compiler pipeline. You should treat them as performance improvements, not language-level guarantees.

In practice this means:

- Your code should remain correct even if a specific optimisation does not apply.
- The exact set of optimisations can evolve between compiler versions.
- Optimisations must preserve observable behaviour.

Tail calls are the exception: a call in tail position never uses up stack. That is a guarantee of the language, not an optimisation, and every compiler version keeps it. See [Tail recursion](../tour/functions#tail-recursion).

It is a guarantee because recursion is how Nanyx loops: `for` needs a collection that already exists, and `while` needs `Memory`, so any other loop is a recursive function, and a loop that may run out of stack depending on its input is no use. It holds for every kind of call, not only a function calling itself: functions calling each other (a state machine, a parser), a closure, a context member, and the continuations a [workflow](../advanced/workflows) passes. And a rule you can check ("a call in tail position uses no stack") is predictable where an optimisation that applies to some shapes of code is not. The cost is that every backend needs real tail calls. WebAssembly has them; a backend that had to fake them would be slow.

Freeing memory is not an optimisation either: a value is freed as soon as nothing refers to it, by [reference counting](../foundations/reference-counting), not left for a garbage collector.

# Typical optimisation areas

Depending on code shape and target backend, the compiler may perform optimisations such as:

- constant folding and simplification,
- [inlining](#inlining) of lambdas called once, handlers and small functions,
- dead-code elimination: a function, closure or host member that nothing reaches is not in the program,
- copy elision and reduced temporary allocations,
- reuse in place: a value a `match` takes apart and holds the only reference to is rewritten into a new value of the same size, so mapping over a list nothing else refers to allocates nothing (see [Reference counting](../foundations/reference-counting#reuse-in-place)),
- improved passing strategy for values (for example by-value vs by-reference decisions).

For allocation-specific behavior, see [Stack vs heap allocation](../advanced/stack-vs-heap-allocation).

# Inlining

The compiler replaces a call with the body of the function it calls when that makes the program smaller or lets it simplify what is left: a lambda written where it is called and called once, a small function, and a handler, a function that runs a lambda it is given under a `use`. A function that calls itself, or calls a function that calls it back, is never inlined into that cycle, though its callers may be.

Inlining is what makes a [workflow](../advanced/workflows) cost nothing. This `proc` block:

```nanyx fragment
proc {
  def a = try getA()
  def b = try getB()

  return a + b
}
```

is a call of `proc` with the block as a lambda, and each keyword is a call of a member of the `Proc` context with the rest of the block as another lambda. Once `proc`, the block, the members and the continuations are inlined, and the matches on what is now known are simplified, what is left is the same code as this `match`, written by hand:

```nanyx fragment
match getA()
  | #some(a) ->
    match getB()
      | #some(b) -> #some(a + b)
      | other -> other
  | other -> other
```

It makes no closures and allocates nothing of its own. So use a workflow wherever it reads better: it is not slower than the code it stands for.

Inlining changes no behaviour. A call in tail position is still one after inlining, and a crash reports the line the code was written on, not the line of the call it was inlined into. Debug and release builds inline alike, so what a debug run costs is what a release build costs.

# Writing optimisation-friendly Nanyx

You usually get the best results by writing clear, idiomatic code first:

- Prefer straightforward expressions over clever micro-optimised patterns.
- Keep data flow explicit (especially around large records).
- Profile real workloads before changing code for performance.

# Future direction

Nanyx is expected to expose more user control over selected performance behaviors over time (for example allocation and passing strategy controls), while keeping automatic optimisation as the default experience.
