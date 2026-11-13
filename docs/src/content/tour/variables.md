---
title: "Variables"
description: "Def bindings and immutability in Nanyx"
order: 1
---

In Nanyx, variables are immutable. You declare them using the `def` keyword.

# Def bindings

```nanyx
def name = "Nanyx"
def version = 4
def pi = 3.14159
```

Once a value is bound, it cannot be reassigned:

```nanyx
def x = 10
x = 20  -- ✗ error NYX0001: cannot reassign a value
```

> **Mutation:** In Nanyx mutation is considered an advanced topic and makes use of a context called `Memory`. Inside a `memory { ... }` block, `mut total = 0` makes a cell, and `total := total + 1` changes what it holds; a `def` without `mut` still cannot be reassigned. See [Memory](../advanced/memory) for more on how mutation works and how to use it safely.

# Type annotations

Nanyx can infer types, but you can add annotations for clarity:

```nanyx
def name: string = "Nanyx"
def count: int = 42
def ratio: float = 0.75
def active: bool = true
```

# Blocks

A block expression returns the value of its last expression:

```nanyx
def result =
  def a = 10
  def b = 20
  a + b

-- result is 30
```

So a block has to end with an expression. One that ends with a `def` has no value to give back, and is an error (NYX0220): write the name you defined on the last line, or `()` if the block gives back nothing. An empty block, `{ }`, gives back `()`.

The defs in a block are only in scope inside it.

# Several statements on one line

A `;` separates statements written on one line: `a; b` means the same as `a` and `b` on lines of their own, at the block's indent.

```nanyx
def result =
  def a = 10; def b = 20; a + b

def double = { n -> def twice = n * 2; twice }
```

It works in every block: a function's body, a lambda's, a match arm's and an `if`'s branches. The `;` belongs to the innermost of them. In `if ready then log(n); n`, both statements are the `then` branch's, as they would be on lines of their own under it, and in `| 0 -> log(n); n` both are the arm's. To run something after the whole `if` or `match`, start a new line.

A `;` only goes between two statements, so one at the end of a line, or two in a row, is an error. Top-level definitions can't be separated by `;`: each goes on a line of its own.

The formatter keeps statements separated by `;` on one line while they fit, with one space after each `;`, and otherwise puts each statement on a line of its own. The official linter's [`semicolons`](../standard-tools/official-linter#statements-separated-by-) rule warns about every `;`; turn it off if you like short sequences on one line.

# `do` blocks

To start a block of your own in the middle of another, write `do` and put the block's statements on the lines after it, indented. The defs in it are gone once the block ends, so a `do` block keeps names that are only needed for a moment out of the code after it. A def in it can also reuse a name from outside, without changing what that name means after the block:

```nanyx
@entry
def main = {
  def total = 10

  do
    def total = 20
    println("inside: {total}")

  println("outside: {total}")
}

-- inside: 20
-- outside: 10
```

Like any block, a `do` block's value is the value of its last statement, so it can be a def's value too. `do` is not a loop: the block runs once, where it is written. It is the same as `do` in F#, and has nothing to do with `do ... while` in C or Java.
