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

A range is a value like any other: it can be passed around, stored, and pulled with the `seq` functions. A `for` loop over a range written in place counts through it without making the sequence.

An unbounded range such as `0..` counts up forever, so a `for` loop can only walk it [in step with something that ends](./loops#enumerating-with-indexes):

```nanyx
for ["a", "b", "c"], 1.. | letter, n -> println("{n}. {letter}")
```

# Types and ranges

Any type can be used in a range if it supports `succ` and comparison:

```nanyx skip=#1700
def aToZ = 'a'..'z'

type Point = (x: int, y: int)

def Point.succ: Point -> Point = { p -> Point(x = p.x + 1, y = p.y + 1) }

def Point.`<=`: (Point, Point) -> bool = { p1, p2 -> p1.x + p1.y <= p2.x + p2.y }

def points = Point(x = 1, y = 1)..Point(x = 8, y = 8)
```

# Implementing ranges

You can implement your own range generator when you have `succ` and a comparison:

```nanyx skip=#1699
def range
  : [(succ: a -> a, `<=`: (a, a) -> bool)] (start: a, end: a) -> seq(a)
  = { start, end ->
      seq {
        memory {
          mut current = start

          while current <= end ->
            yield current
            current := succ(current)
        }
      }
    }
```
