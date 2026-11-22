---
title: "Sequences and queries"
description: "Lazy sequences, and walking them"
order: 6
---

Nanyx has one lazy sequence type, `seq(a)`. A sequence is worked out only as far as it is pulled, so it may never end:
the range `0..` counts up forever. Pulling one changes nothing, so it can be passed around, stored and pulled again.

```nanyx
def squares = 1.. \seq.map { x -> x * x }

println(squares \seq.take(4) \seq.toList) -- [1, 4, 9, 16]
```

# Iteration safety

A `for` loop over a range with no end alone would never end, so it is an error:

```nanyx
for 0.. | i -> println("Hello") -- error NYX0217

for 1..10 | i -> println("Hello") -- OK
```

# Enumerating with indexes

A range with no end may be walked in step with something that ends, which bounds the loop:

```nanyx
def items = ["a", "b", "c"]

for items, 0.. | item, i -> println("Item {i} is {item}")
```

# Pulling a sequence

A sequence's `next` gives its first element and the rest of the sequence, `#next(x, rest)`, or `#done`. A function that
walks one matches on it and recurses on the rest, as it would on a list:

```nanyx
def total: seq(int) -> int = { s ->
  match s.next()
    | #next(x, rest) -> x + total(rest)
    | #done -> 0
}
```

A sequence whose steps need contexts says so in its type: mapping with a function that prints gives a
`[Console] seq(b)`, which can be pulled only where `Console` is.
