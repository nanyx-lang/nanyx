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

# Building a collection

A sequence is lazy, which a collection built all at once doesn't need. So `list.build`, `list.select` and the other
`build` and `select` functions take a block that calls `emit` for each value instead, through the prelude's `Emit`
context. The block runs once, now, and each value goes into the collection as it is emitted:

```nanyx
def evens = list.build {
  for 1..10 | n ->
    if n % 2 == 0 then emit(n)
}

def doubled = [1, 2, 3] \list.select { x ->
  emit(x)
  emit(x * 10)
}

println(evens) -- [2, 4, 6, 8, 10]
println(doubled) -- [1, 10, 2, 20, 3, 30]
```

`string.build` builds a string the same way, from the pieces its block emits. Each piece is copied once, so a string
of many pieces takes time in proportion to its length, where adding them one at a time with `++` copies the string so
far for each:

```nanyx
def row = string.build {
  for [1, 2, 3] | n ->
    emit(int.toString(n))
    emit(";")
}

println(row) -- 1;2;3;
```

A lambda given to `list.select` emits as many values as it likes for each element, none included. One that gives a
whole list for each element is `list.flatMap`'s, and one that gives a sequence `seq.flatMap`'s.

`emit` is an ordinary context member, so a function that emits can call another that does, and you can handle it
yourself with `use`, here to print each value a walk emits:

```nanyx
type Tree = #leaf(int) | #node(Tree, Tree)

def walk: [Emit(int)] Tree -> () = {
  | #leaf(n) -> emit(n)
  | #node(left, right) ->
    walk(left)
    walk(right)
}

use Emit(int)(emit = { n -> println(n) }) in walk(#node(#leaf(1), #node(#leaf(2), #leaf(3))))
```

A block with `yield` in it is different: it is a lazy sequence, made with `seq { ... }`
([Workflows](./workflows#yield-and-sequences)).
