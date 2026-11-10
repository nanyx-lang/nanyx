---
title: "Memory"
description: "Scoped mutation with the memory context"
order: 5
---

Mutation in Nanyx is available but bounded. Mutable values require the `$Memory` context, and `memory { ... }` is the helper that creates and scopes that context. This allows internal mutation while keeping external interfaces pure.

`$Memory` is a [nominal context](./nominal-and-protected-types) owned by the standard library, so `memory { ... }` is the only way to obtain an instance of it; user code cannot construct one with `use`.

This page is about mutating values. For how Nanyx frees memory, and updates immutable values in place when nothing else refers to them, see [Reference counting](../foundations/reference-counting).

# Local mutation

```nanyx
def sort: [Ord(a)] list(a) -> list(a) = { l ->
  memory {
    def arr = MArray.of(l)
    MArray.sort(arr)
    MArray.toList(arr)
  }
}
```

# Mutability requires context

`mut e` creates a mutable cell holding the value of `e`, and `:=` replaces its contents. Reading a cell is implicit: using the cell's name where a plain value is expected reads the current contents.

```nanyx
def main = {
  def x = mut 0 -- Error: requires $Memory context
}

def main = {
  memory {
    def x = mut 0 -- OK
    x := x + 1
    println("{x}") -- prints 1
  }
}
```

The type of a cell records that it requires `$Memory`, which is what prevents it from leaking out of the scope (see below). `:=` is the only assignment operator in the language.

# Mutable collections

Mutable collections such as `MList` are ordinary values with attached operators that update in place. `+=` here is an attached definition on `MList`, not a second kind of assignment.

```nanyx
memory {
  def items: MList(int) = []
  items += 1
  items += 2
  MList.toList(items) -- [1, 2]
}
```

# Values cannot escape memory scope

Values that require the `$Memory` context cannot be returned out of a `memory { ... }` scope.

```nanyx
def bad = {
  memory {
    def arr = MArray.of([1, 2, 3])
    arr -- Error: value requires $Memory context and cannot escape this scope
  }
}
```

To return data, convert it to an immutable value before leaving the scope:

```nanyx
def good = {
  memory {
    def arr = MArray.of([1, 2, 3])
    MArray.toList(arr) -- OK
  }
}
```

This is how Nanyx performs escape analysis. Instead of a separate escape-analysis pass, the compiler tracks context requirements in types. If a value's type still needs `$Memory`, it can only be used where `$Memory` is available, so it cannot leak into pure code.

For the detailed proposal, see [Work in progress: Context-qualified types](../work-in-progress/context-qualified-types).

For the general model behind this, see [Contexts: Contexts Are Part of the Type](./contexts#contexts-are-part-of-the-type).

# No global mutable state

Because `memory` is scoped, global mutable state is effectively impossible. Top-level `mut` values will fail to compile because a top-level definition cannot be inside a context.
