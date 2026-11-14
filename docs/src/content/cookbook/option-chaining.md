---
title: Option chaining
description: Implement a short-circuiting workflow for option-like values
order: 4
---

Let's build our understanding of workflows by implementing a simple option-chaining workflow. We want a workflow that allows us to chain together computations that may return `#some(a) | #none`, short-circuiting if any step returns `#none`.

# Setup

```bash
nanyx new option_chaining
cd option_chaining
```

# The code

A workflow builder is a function that runs its block under a context, whose members are the workflow's keywords. We start by declaring that context:

```nanyx fragment
context OptionChain = (
  try: [c] ((#some(a) | #none), ([c] a -> #some(b) | #none)) -> #some(b) | #none
  return: a -> #some(a) | #none
  zero: #some(()) | #none
)
```

`try`'s continuation is the rest of the block, which calls the context's members too, so its type takes a context variable, `[c]`, that stands for whatever the block requires.

Then we write the builder: a function that loads an instance of the context with `use` and runs the block under it. The block's type, `[OptionChain & c] () -> ...`, is what gives the block the keywords:

```nanyx fragment
def maybe: [c] ([OptionChain & c] () -> #some(a) | #none) -> #some(a) | #none = { block ->
  use OptionChain(
    -- This is how we get the short-circuiting behavior for options.
    -- Notice how we don't use the continuation for the `#none` case, so
    -- the workflow ignores the rest of the block
    try = {
      | #some(value), continue -> continue(value)
      | #none, _ -> #none
    }

    -- `return` wraps the block's final value
    return = { value -> #some(value) }

    -- `zero` is the value of a block that produces nothing: an `if` with no
    -- `else`, or a block that falls off the end without a `return`
    zero = #none
  ) in block()
}
```

The workflow is used like this:

```nanyx fragment
maybe {
  def a = try someComputation1()
  def b = try someComputation2()

  return a + b
}
```

`maybe { ... }` is the call `maybe({ ... })`. If `someComputation1()` returns `#none`, the `try` keyword never calls its continuation, so `someComputation2()` is never run and the whole block evaluates to `#none`. Otherwise both values are bound and `return` produces `#some(a + b)`.

For the general model behind this, see [Workflows](../advanced/workflows).