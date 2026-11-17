---
title: "Memory"
description: "Scoped mutation with the memory context"
order: 5
---

Mutation in Nanyx is available but bounded. Mutable values require the `Memory` context, and `memory { ... }` is the helper that creates and scopes that context. This allows internal mutation while keeping external interfaces pure.

`Memory` is a [nominal context](./nominal-and-protected-types) owned by the standard library, so only the standard library can construct an instance of it, and `memory { ... }` is the only way to get one. As with any context, `use` loads an instance you already have; it can't make a new one.

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

A cell is not a value of its own. `def y = x` copies what `x` holds into `y`, which a later `x := ...` leaves as it is, and passing `x` to a function passes what it holds (`mut x` passes the cell itself; see below). Nothing can hold a cell, only what is in it.

# Mutability requires context

A cell can be declared only inside a `memory { ... }` block, where `Memory` is:

```nanyx
def main = {
  mut x = 0 -- Error NYX0221: `mut` needs the context 'Memory'
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

A lambda that uses a cell needs `Memory` too, and its type says so: `note` is `[Memory] string -> ()`. It can be called inside the block but cannot leave it (see below).

# Passing a cell to a function

A function can take a cell and change it, with `mut` at both ends: `mut cell` in its parameters, and `mut count` where it is called.

```nanyx
def inc = { mut cell ->
  cell := cell + 1
}

def count = memory {
  mut count = 0

  for 1..10 ->
    inc(mut count)

  count -- 10
}
```

In the body, a cell parameter behaves as a cell declared there: its name reads it and `:=` changes it. Its type is `mut T`, which an annotation can write: `def inc: [Memory] mut int -> () = { mut cell -> ... }`. A function can pass a cell it is given on, `other(mut cell)`, and one cell can be passed for two parameters, `swap(mut a, mut a)`, which are then the same cell.

A function that takes a cell can only be called where the cell's block is, so a `mut` parameter implies `Memory`, whether or not the body uses the cell. `inc` above is inferred as `[Memory] mut int -> ()`, and so is a function that only passes its cell on. A function type that takes a cell and writes no contexts, such as a parameter's `change: mut int -> ()`, requires `Memory` too. A def that writes contexts must write `Memory` among them: `def inc: [Console] mut int -> ()` is an error (NYX0211) that says to write `[Console, Memory]`.

`mut x` passes only a cell, a name declared with `mut` or a `mut` parameter: anything else is an error (NYX0234). A plain value where `mut T` is expected, or `mut x` where a plain `T` is, is a type error. A `mut` parameter takes one name, never a pattern.

A cell can be passed to a function, but nothing else can hold one: `mut T` anywhere but a function's parameter, as in `list(mut int)`, is an error (NYX0235). To hold one, use a reference (below). A lambda over a cell parameter needs the `Memory` of the block the cell is in, so it cannot leave that block, as a lambda over the cell itself cannot.

# References

A reference, `MRef(a)` from [`nanyx/mref`](../stdlib/mref), is a cell that is a value. It can be held in a record, a list or another reference, passed without `mut`, and returned from a function, and every holder sees what `MRef.set` changes:

```nanyx
import nanyx/mref

def bump: [Memory] MRef(int) -> () = { r ->
  MRef.update(r, { n -> n + 1 })
}

def total = memory {
  def count = MRef.new(0)
  def holder = (counter = count, name = "clicks")
  bump(holder.counter)
  bump(count)
  MRef.get(count) -- 2
}
```

A reference belongs to the `memory` block it was made in, as a cell does. `MRef` is declared `type MRef(a) = [Memory] private box(a)`, a type in `Memory`'s scope ([Types in a scope](./contexts#types-in-a-scope)), so making, reading or changing one requires the block's `Memory`, and neither it nor anything holding it can leave the block (NYX0233). A reference holds values of one type, so a def of one isn't generic, as a def of a function is: after `def r = MRef.new([])`, `MRef.set(r, [5])` makes `r` an `MRef(list(int))`, and reading it as a list of strings is an error. A reference has no `==`, order or hash, since what it holds changes, and no `toString`, since writing what it holds would read it without `Memory`: compare or write what `MRef.get` gives instead.

# Values cannot escape memory scope

`Memory` is a _scoped_ context (`scoped context Memory = protected ()`): each `memory` call supplies an instance of its own, and everything that requires it belongs to that call. A cell's `Memory` is that of the block it is declared in, and so is that of every lambda that uses the cell. What the call gives back must not require it: giving back a lambda that uses one of the block's cells, or a structure that holds one, is an error (NYX0233), which names the cell:

```nanyx
def bad = {
  memory {
    mut count = 0
    { count := count + 1 } -- Error NYX0233: the result requires the block's 'Memory' through the cell 'count'
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

A block inside another may give back a lambda over the outer block's cells, since that requires the outer block's `Memory`, not its own:

```nanyx
def total = memory {
  mut sum = 0
  def add = memory { { n -> sum := sum + n } }
  add(1)
  add(2)
  sum -- 3
}
```

The rule follows `Memory` through other functions. A function that passes its parameter a block's `Memory`, such as `{ make -> memory { make() } }`, supplies it in turn, so its result must not require it either. A function whose type says it requires `Memory` itself can't give its parameter a block's: `def run: [Memory] ([Memory] () -> b) -> b = { make -> memory { make() } }` is an error at `make()`, since the block's `Memory` would reach `make`, which is bound outside the block. Only `Memory` is restricted this way: a function that supplies any other context, such as one that handles `Log` with a `use`, may give back a lambda that requires it.

The block can name the `Memory` it is given with `context`, as any lambda can name a context it is given ([Contexts](./contexts#naming-the-instance-a-lambda-is-given)): `memory { context m -> ... }`. It isn't a parameter, and the block's type is still `[Memory] () -> a`. A block that doesn't name it, `memory { ... }`, is the same block. The name isn't a value: the only thing it can do is be loaded with `use`, so `memory { context m -> m }` is an error (NYX0245).

`mut` makes a cell of the innermost `Memory` in scope. `use m` makes `m` the innermost one, so an inner block can make cells that belong to an outer one, and a lambda over them may leave the inner block:

```nanyx
def total = memory { context outer ->
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

Which block's `Memory` a type requires is never written. Where a type requires the `Memory` of two different blocks, as a function's type can, hovers and error messages number them in the order they first appear, `([Memory₁] () -> a), ([Memory₂] () -> b) -> b`; with one block's, it is plain `[Memory]`.

This is how Nanyx performs escape analysis. Instead of a separate escape-analysis pass, the compiler tracks which values require which `memory` call's `Memory`. A value that still needs it can only be used inside that call, so it cannot leak into pure code.

# No global mutable state

Because `memory` is scoped, global mutable state is effectively impossible. Top-level `mut` values fail to compile, because a top-level definition cannot be inside a `memory` block.

# How cells are compiled

A cell that no lambda captures is a WebAssembly local, and `:=` sets it. A cell a lambda captures, or one passed with `mut`, is a box on the heap, shared by the block, every lambda that captures it and every function it is passed to, and reference counted like any other value, so it is freed when the last of them is done with it. A reference is the same box: `MRef.set` gives up the value it held, and the box gives up the value it holds when it is freed.

# Mutable collections

A stack or a list that changes in place is a value, as a reference is, and belongs to its block in the same way. To keep a stack in a block, use an `MStack` from [`nanyx/mstack`](../stdlib/mstack): `push` and `pop` change it in place, each in constant time.

```nanyx
import nanyx/mstack

def top = memory {
  def stack = MStack.empty()
  stack \push(5.0)
  stack \push(6.0)
  def popped = stack \pop -- #some(6.0)
  stack \peek -- #some(5.0)
}
```

An `MList` from [`nanyx/mlist`](../stdlib/mlist) grows at the end with `add`, or `+=`, and reads and sets its elements by index. Both are written in Nanyx over `MRef`, so every operation requires the block's `Memory`, and neither can leave the block. Each says so in its declaration, `type MStack(a) = [Memory] private MRef(list(a))`, as a type of your own that holds an `MRef` or an `MArray` must ([Types in a scope](./contexts#types-in-a-scope)).

```nanyx
import nanyx/mlist

def names = memory {
  def items: MList(string) = MList.empty()
  items += "ada"
  items += "bob"
  MList.toList(items) -- [ada, bob]
}
```

`+=` is a [spare operator](../tour/operators#the-operators) that `MList` attaches, so `items += x` is `MList.add(items, x)`, and gives `()`. It doesn't assign: a `mut` cell changes with `:=`, so `count += 1` on one is an error that says to write `count := count + ...`.

An `MArray` from [`nanyx/marray`](../stdlib/marray) is a fixed-length array whose elements `set` changes in place, laid out in one block as an [`Array`](../stdlib/array) is. It belongs to its block too. Converting between an `Array` and an `MArray` copies, so an `Array` never changes. An `MSlice` from [`nanyx/mslice`](../stdlib/mslice) is a view of part of an `MArray`: setting, sorting, filling or reversing it changes that part of the array, and nothing else.

```nanyx
import (
  nanyx/marray
  nanyx/mslice
)

def scores = Array[3, 1, 4, 1, 5]

def sorted = memory {
  def xs = MArray.fromArray(scores)
  def changed = xs \set(4, 9) -- #ok
  xs \slice(0, 3) \sort
  MArray.toArray(xs) -- [1, 3, 4, 1, 9]
}
```

An index out of range never traps: `set` gives `#outOfRange` and leaves the array as it was, and `at` gives `#none`. A mutable array or slice has no `==`, order, hash or `toString`, as a reference has none: compare or write the `Array` that `MArray.toArray` gives back.

# Which one to use

| You want | Use |
|---|---|
| A local variable that changes, in one function | a `mut` cell |
| A function that changes its caller's variable | a `mut` parameter, passed with `mut x` |
| A cell held in a record or a list, or shared | `MRef` |
| A stack, or a list that grows at the end | `MStack`, `MList` |
| Elements changed by index | `MArray`, and `MSlice` for part of one |

Each belongs to the `memory` block it was made in, so a function that uses any of them inside a `memory` block can still be pure from the outside: give back an immutable value, a list, an [`Array`](../stdlib/array) or what a cell holds.

# Planned

`mut` record fields are planned ([#1873](https://github.com/nanyx-lang/nanyx/issues/1873)).
