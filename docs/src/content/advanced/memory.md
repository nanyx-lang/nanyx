---
title: "Memory"
description: "Scoped mutation with the memory context"
order: 5
---

Mutation in Nanyx is available but bounded. Mutable values require the `$Memory` context, and `memory { ... }` is the helper that creates and scopes that context. This allows internal mutation while keeping external interfaces pure.

`$Memory` is a [nominal context](./nominal-and-protected-types) owned by the standard library, so `memory { ... }` is the only way to obtain an instance of it; user code cannot construct one with `use`.

This page is about mutating values. For how Nanyx frees memory, and updates immutable values in place when nothing else refers to them, see [Reference counting](../foundations/reference-counting).

# Local mutation

`mut e` makes a cell holding the value of `e`, and `:=` replaces what it holds. Reading a cell is implicit: using the cell's name anywhere but on the left of `:=` reads what it holds now.

```nanyx
def sum: list(int) -> int = { xs ->
  memory {
    def total = mut 0
    for xs | x -> total := total + x
    total
  }
}
```

`memory` runs its block and gives back the block's result. A cell holds values of one type, which an annotation on its `def` can write: `def names: list(string) = mut []`. The value given to `:=` must have that type.

A cell is not a value of its own. `def y = x` copies what `x` holds into `y`, which a later `x := ...` leaves as it is, and passing `x` to a function passes what it holds. Nothing can hold a cell, only what is in it.

# Mutability requires context

`mut` makes a cell only inside a `memory { ... }` block, where `$Memory` is:

```nanyx
def main = {
  def x = mut 0 -- Error NYX0221: `mut` needs the context '$Memory'
}

def main = {
  memory {
    def x = mut 0 -- OK
    x := x + 1
    println("{x}") -- prints 1
  }
}
```

`:=` is the only assignment operator in the language, and it changes only a cell: `:=` on any other name is an error (NYX0222), and so is assigning a value of another type (NYX0223).

# Lambdas that use cells

A lambda in the block may read and change the block's cells. A `for` loop's body is part of the block it is written in:

```nanyx
def tally = { words ->
  memory {
    def count = mut 0
    def note = { word -> count := count + 1 }
    for words | word -> note(word)
    count
  }
}
```

A lambda that uses a cell needs `$Memory` too, so it can be called inside the block but cannot leave it.

# Values cannot escape memory scope

Because a cell is not a value, what a `memory` block gives back is always plain data, unless it is a lambda that uses one of the block's cells. Giving one back, or a structure that holds one, is an error (NYX0224):

```nanyx
def bad = {
  memory {
    def count = mut 0
    { count := count + 1 } -- Error NYX0224: the lambda uses `count`, so it cannot leave the block
  }
}
```

To return data, return what the cells hold:

```nanyx
def good = {
  memory {
    def count = mut 0
    count := count + 1
    count -- OK: an int
  }
}
```

This is how Nanyx performs escape analysis. Instead of a separate escape-analysis pass, the compiler tracks which values require `$Memory`. If a value still needs `$Memory`, it can only be used where `$Memory` is available, so it cannot leak into pure code.

# No global mutable state

Because `memory` is scoped, global mutable state is effectively impossible. Top-level `mut` values fail to compile, because a top-level definition cannot be inside a `memory` block.

# How cells are compiled

A cell that no lambda captures is a WebAssembly local, and `:=` sets it. A cell a lambda captures is a box on the heap, shared by the block and every lambda that captures it, and reference counted like any other value, so it is freed when the last of them is done with it.

# Planned

Mutable collections, cells as values of their own, and `mut` record fields are planned; see [Work in progress: Mutation](../work-in-progress/mutation). For example, a mutable list will be an ordinary value with attached operators that update it in place, where `+=` is an attached definition on `MList`, not a second kind of assignment:

```nanyx
memory {
  def items: MList(int) = []
  items += 1
  items += 2
  MList.toList(items) -- [1, 2]
}
```
