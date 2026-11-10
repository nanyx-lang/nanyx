---
title: "Reference counting (Perceus)"
description: "How Nanyx frees memory without a garbage collector, and reuses it in place"
order: 12
---

Nanyx has no garbage collector. A compiled program counts the references to each value it keeps on the heap (a
string, a list cell, a record, a closure, a tag with a payload), and frees the value as soon as the last reference
goes. You never write any of this: the compiler works out where each reference is taken and given up. It follows
[Perceus](https://www.microsoft.com/en-us/research/publication/perceus-garbage-free-reference-counting-with-reuse/),
the scheme the Koka language uses.

# Why counting is enough

Reference counting on its own cannot free a cycle: two values that refer to each other keep each other's count
above zero. Nanyx values are immutable, and a value can only refer to values that existed before it, so the heap
never holds a cycle. Counting frees everything.

That keeps [performance predictable](../foundations/principles#performance-you-can-reason-about). Memory comes back
at a point the program determines, not when a collector decides to run, and there are no collector pauses.

# What the compiler does

- **Each name holds a reference.** When a value is used again after it has been passed on (stored in a record, say,
  or given to a function that keeps it), the compiler takes another reference first. After a value's last use, it
  gives its reference up, on every branch of an `if` or `match`.
- **A function that only reads a parameter borrows it.** The caller keeps its reference, and the call costs no
  counting at all.
- **Taking a value apart hands its fields over.** When a `match` arm takes apart a value nothing else refers to, the
  names it binds take over the value's references to its fields, and the value itself is freed.
- **Tail calls still use no stack.** A value that dies at a [tail call](../tour/functions#tail-recursion) is given up
  before the call, or passed into it, never after it.

```nanyx
def total: list(int) -> int = {
  | [] -> 0
  | [x, ...rest] -> x + total(rest)
}
```

When `total` is handed a list nothing else refers to, each cell is freed as soon as the arm has taken `x` and `rest`
out of it, so the list is gone by the time the sum is.

Values that are not on the heap (numbers, booleans, and tags without a payload such as `#none`) are never counted.
String literals are never freed.

# Reuse in place

When a `match` arm takes apart a value it holds the only reference to, and then builds a new value of the same size,
the new value is written into the old one's memory instead of new memory. Mapping over a list that nothing else
refers to rewrites its cells in place:

```nanyx
def double: list(int) -> list(int) = {
  | [] -> []
  | [x, ...rest] -> [x * 2, ...double(rest)]
}
```

Handed a list nothing else refers to, `double` allocates nothing: each cell of the list becomes a cell of the doubled
list. The same goes for `List.map` from the standard library.

Reuse never changes what a program means. When something else still refers to the value, the arm finds that out
when it runs, leaves the value as it was, and the new value gets memory of its own:

```nanyx
def both: list(int) -> int = { xs ->
  def ys = double(xs)
  total(xs) + total(ys)
}
```

Here `xs` is used again after `double`, so `double` copies its cells rather than rewriting them.

An arm reuses a value's memory when:

- it takes apart one value: the first cell of a list (`[x, ...rest]`), a tag with its payload (`#some(v)`), or a
  tuple or record whose fields it binds;
- it does not use that value itself afterwards; and
- it always builds a list cell, a tag with a payload, or a tuple or record of the same size, not only in one branch
  of an `if` or `match`.

So to get reuse, hand a function the only reference to what it transforms, and build the new value directly in the
arm that took the old one apart. Strings are not reused.

# What it costs

Each value on the heap carries a count in a header before it, and taking or giving up a reference updates that count.
The compiler avoids most of that work: a borrowed parameter costs nothing, the last use of a name passes its
reference on rather than taking a new one, and taking apart a value nothing else refers to frees it without
touching its fields' counts.

Generic functions are compiled once for each set of types they are used at, so the compiler always knows which
fields of a value are on the heap, and an integer keeps its full width.

# Status

Reference counting and reuse are in the WebAssembly backend, the one `nanyx run` uses. `nanyx run --check-memory`
runs a program with an allocator that checks its counts. It stops the program at an object freed twice or used after
it was freed, and fails the run if anything is left unfreed at the end. A way to ask the compiler to
check that a function runs entirely in place, as Koka's `fip` functions do, is an open question. The design, with
how values are laid out in memory, is in
[memory.md](https://github.com/nanyx-lang/nanyx/blob/main/compiler/design/memory.md) in the compiler's design notes.
