---
title: "Ranges"
description: "Working with ranges and sequences"
order: 11
---

Ranges use the `x..y` syntax and are a form of sequence. Bounded ranges are finite (`seq`), while unbounded ranges are potentially infinite (`gen`).

```nanyx
for 1..5 | i -> println("Hello times {i}")

def naturalNumbers = 0..

def oneToTen = 1..10
```

An unbounded range such as `0..` counts up forever, so a `for` loop can only walk it [in step with something that ends](./loops#enumerating-with-indexes):

```nanyx
for ["a", "b", "c"], 1.. | letter, n -> println("{n}. {letter}")
```

# Types and ranges

Any type can be used in a range if it supports `succ` and comparison:

```nanyx
def aToZ = 'a'..'z'

type Point = (x: int, y: int)

def Point.succ(p: Point) -> Point(p.x + 1, p.y + 1)

def Point.`<=`(p1: Point, p2: Point) -> magnitude(p1) <= magnitude(p2)

def points = Point(1, 1)..Point(8, 8)
```

# Implementing ranges

You can implement your own range generator when you have `succ` and a comparison:

```nanyx
def range
  : [(succ: a -> a, `<=`: (a, a) -> bool)] (start: a, end: a) -> seq(a)
  = { start, end ->
      seq {
        memory {
          def current = mut start

          while current <= end ->
            yield current
            current := succ(current)
        }
      }
    }
```
