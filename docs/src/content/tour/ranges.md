---
title: "Ranges"
description: "Working with ranges and sequences"
order: 11
---

Ranges use the `x..y` syntax and are [lazy sequences](../stdlib/prelude-sequences): `1..10` is a `seq(int)` of the numbers from 1 to 10, and `0..`, with no end, is one that counts up forever.

```nanyx
for 1..5 | i -> println("Hello times {i}")

def naturalNumbers = 0..

def oneToTen = 1..10

println(oneToTen \seq.map { * 2 } \seq.toList)
println(naturalNumbers \seq.take(3) \seq.toList)
```

A range is a value like any other: it can be passed around, stored, and pulled with the `seq` functions. A `for` loop over a range of `int`s written in place counts through it without making the sequence.

A `for` loop walks any other sequence too, pulling one element each time round, just before the body runs with it:

```nanyx
def evens = (1..10) \seq.filter { x -> x % 2 == 0 }

for evens | n -> println(n)
```

An unbounded range such as `0..` counts up forever, so a `for` loop can only walk it [in step with something that ends](./loops#enumerating-with-indexes):

```nanyx
for ["a", "b", "c"], 1.. | letter, n -> println("{n}. {letter}")
```

# Types and ranges

A range can be of any type whose values come one after another: one with an `Enumerable`, the prelude's context of `succ`, which gives the value after `x`, and an `Ord`, which says when the range has reached its end. `int` has both. A type of your own gets an `Enumerable` by attaching `succ`, and an `Ord` by attaching `compare`:

```nanyx
type Point = protected (x: int, y: int)

def Point.succ: Point -> Point = { p ->
  Point(x = p.x + 1, y = p.y + 1)
}

def Point.compare: Point, Point -> #less | #equal | #greater = { p1, p2 ->
  int.compare(p1.x + p1.y, p2.x + p2.y)
}

def points = Point(x = 1, y = 1)..Point(x = 8, y = 8)

for points | p -> println("({p.x}, {p.y})")
```

A range of a type with no `succ` is an error that says so: `Enumerable` is missing, as any context a call needs is.

A range of characters, `'a'..'z'`, waits on a character type, which Nanyx doesn't have yet:

```nanyx skip=#2196
def aToZ = 'a'..'z'
```

# Implementing ranges

You can implement your own range generator from the same two contexts. A block with `yield` in it is a sequence, and the code after a `yield` runs only when the sequence is pulled that far, so a range can be made of its first element and the range after it:

```nanyx
def range: [Enumerable(a), Ord(a)] a, a -> seq(a) = { start, end ->
  seq {
    if start <= end then
      yield start
      for range(succ(start), end) | x -> yield x
  }
}

println(range(1, 5) \seq.toList)
```
