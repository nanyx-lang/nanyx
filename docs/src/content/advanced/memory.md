---
title: "Memory"
description: "Scoped mutation with the memory context"
order: 5
---

Mutation in Nanyx is available but bounded. Mutable values require the `$Memory` context, and `memory { ... }` is the helper that creates and scopes that context. This allows internal mutation while keeping external interfaces pure.

`$Memory` is a [nominal context](./nominal-and-protected-types) owned by the standard library, so only the standard library can construct an instance of it, and `memory { ... }` is the only way to get one. As with any context, `use` loads an instance you already have; it can't make a new one.

This page is about mutating values. For how Nanyx frees memory, and updates immutable values in place when nothing else refers to them, see [Reference counting](../foundations/reference-counting).

# Local mutation

`mut x = e` declares a cell, `x`, holding the value of `e`, and `:=` replaces what it holds. Reading a cell is implicit: using the cell's name anywhere but on the left of `:=` reads what it holds now.

```nanyx
def sum: list(int) -> int = { xs ->
  memory {
    mut total = 0
    for xs | x -> total := total + x
    total
  }
}
```

`memory` runs its block and gives back the block's result. A cell holds values of one type, which an annotation on its declaration can write: `mut names: list(string) = []`. The value given to `:=` must have that type.

A `mut` declares one name: it takes no pattern, so `mut (a, b) = pair` is an error. To make cells from the parts of a value, take it apart with `def` first, then declare a cell for each part. `mut` is not an expression either, so a cell is never made without a name.

A cell is not a value of its own. `def y = x` copies what `x` holds into `y`, which a later `x := ...` leaves as it is, and passing `x` to a function passes what it holds. Nothing can hold a cell, only what is in it.

# Mutability requires context

A cell can be declared only inside a `memory { ... }` block, where `$Memory` is:

```nanyx
def main = {
  mut x = 0 -- Error NYX0221: `mut` needs the context '$Memory'
}

def main = {
  memory {
    mut x = 0 -- OK
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
    mut count = 0
    def note = { word -> count := count + 1 }
    for words | word -> note(word)
    count
  }
}
```

A lambda that uses a cell needs `$Memory` too, and its type says so: `note` is `[$Memory] string -> ()`. It can be called inside the block but cannot leave it (see below).

# Values cannot escape memory scope

`$Memory` is a _scoped_ context (`scoped context $Memory = ()`): each `memory` call supplies an instance of its own, and everything that requires it belongs to that call. A cell's `$Memory` is that of the block it is declared in, and so is that of every lambda that uses the cell. What the call gives back must not require it: giving back a lambda that uses one of the block's cells, or a structure that holds one, is an error (NYX0233), which names the cell:

```nanyx
def bad = {
  memory {
    mut count = 0
    { count := count + 1 } -- Error NYX0233: the result requires the block's '$Memory' through the cell 'count'
  }
}
```

To return data, return what the cells hold:

```nanyx
def good = {
  memory {
    mut count = 0
    count := count + 1
    count -- OK: an int
  }
}
```

A block inside another may give back a lambda over the outer block's cells, since that requires the outer block's `$Memory`, not its own:

```nanyx
def total = memory {
  mut sum = 0
  def add = memory { { n -> sum := sum + n } }
  add(1)
  add(2)
  sum -- 3
}
```

The rule follows `$Memory` through other functions. A function that passes its parameter a block's `$Memory`, such as `{ make -> memory { make() } }`, supplies it in turn, so its result must not require it either. A function whose type says it requires `$Memory` itself can't give its parameter a block's: `def run: [$Memory] ([$Memory] () -> b) -> b = { make -> memory { make() } }` is an error at `make()`, since the block's `$Memory` would reach `make`, which is bound outside the block. Only `$Memory` is restricted this way: a function that supplies any other context, such as one that handles `Log` with a `use`, may give back a lambda that requires it.

The block is given its instance of `$Memory`, which it can name: `memory { m -> ... }`. A block that doesn't name it, `memory { ... }`, ignores it. The instance is a value of the type `$Memory`, and it belongs to its block as everything that requires the block's `$Memory` does, so `memory { m -> m }` is an error (NYX0233).

`mut` makes a cell of the innermost `$Memory` in scope. `use m` makes `m` the innermost one, so an inner block can make cells that belong to an outer one, and a lambda over them may leave the inner block:

```nanyx
def total = memory { outer ->
  def get = memory {
    use outer
    mut z = 0 -- belongs to the outer block
    z := 5
    { z }
  }
  get() + get() -- 10
}
```

`use m` starts no scope of its own: it only says whose cells a `mut` after it makes.

This is how Nanyx performs escape analysis. Instead of a separate escape-analysis pass, the compiler tracks which values require which `memory` call's `$Memory`. A value that still needs it can only be used inside that call, so it cannot leak into pure code.

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
