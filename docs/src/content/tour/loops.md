---
title: "Loops"
description: "Iterating with for"
order: 10
---

Nanyx uses the `for` keyword to iterate. The iterable comes first, which keeps loops readable and supports pattern matching on the loop variable.

# Basic loops

```nanyx
for 1..5 -> print("Hello")

for 1..5 | i -> print("Hello times {i}")
```

# Pattern matching in loops

```nanyx
for 1..5 | { < 5 } -> println("Ready...")
         | _       -> println("Go!")
```

# Looping collections

```nanyx
def data = [1, 2, 3]

for data | item -> println(item)
```

# Looping over several collections at once

A `for` loop can walk several collections in step. List them separated by commas, and each arm takes one pattern per collection, in the same order:

```nanyx
def names = ["Alex", "Sam", "Jo"]
def ages = [34, 27, 41]

for names, ages | name, age -> println("{name} is {age}")
```

The loop stops as soon as the shortest collection runs out, so nothing is left over and nothing is read past the end. Arms match the elements together, as a `match` on several values does:

```nanyx fragment
for names, ages | "Sam", _   -> println("Found Sam")
                | name, age -> println("{name}, {age}")
```

# Enumerating with indexes

There is no `enumerate` function, because zipping already does the job. Walk a collection in step with a range that has no end to keep a counter:

```nanyx
def data = ["one", "two", "three"]

for data, 0.. | item, i ->
  println("Item number {i} is {item}")
```

This prints `Item number 0 is one`, `Item number 1 is two` and `Item number 2 is three`. Two things are clear from the loop head: `i` is a counter that starts wherever you choose (`1..` counts from one), not the item's position in some collection, and which name is the counter and which the item, even when looping over a list of ints.

A range with no end never runs out on its own, so a loop over one alone is an error (NYX0217). It needs at least one collection or bounded range beside it to say when to stop.

# Dictionaries

When iterating a map or dictionary, the loop variable is a record of the key and value:

```nanyx
def dict = ["one" => "uno", "two" => "dos"]

for dict | key, value -> print("{key} => {value}")
```

# Ignoring loop variables

Use `_` to ignore unused variables:

```nanyx fragment
for dict | _, value ->
  print("Only care about {value}")
```

# While loops

`for` iterates over a collection that already exists. When the number of iterations depends on state that changes as the loop runs, use `while`, which repeats its body for as long as the condition is `true`:

```nanyx
memory {
  def remaining = mut 3

  while remaining > 0 ->
    println("{remaining}...")
    remaining := remaining - 1
}
```

Because the condition has to change for the loop to end, `while` is almost always used together with [the `$Memory` context](../advanced/memory). It is the one low-level loop in the language, and exists so that locally mutable algorithms can be written inside an otherwise immutable interface.
