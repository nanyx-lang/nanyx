---
title: "prelude: arrays"
description: Reading an element of an array, which calling the array does
order: 8.5
---

The part of an array's functions that is in the [prelude](prelude), so no import is needed: what calling an array reads, `xs(i)`. The rest of them are in [`nanyx/array`](array).

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

## `array.at`

```nanyx fragment
def array.at: array(a), int -> #some(a) | #none
```

The element of `xs` at `index`, counting from 0, or `#none` when `index` is negative or past the
end. Calling the array, `xs(index)`, is the same.

## `array.fromArray`

```nanyx fragment
def array.fromArray: array(a) -> array(a)
```

`xs` itself. What a literal builds an array with, `Array[1, 2]`, or `[1, 2]` where an array is
expected.

## `list.fromArray`

```nanyx fragment
def list.fromArray: array(a) -> list(a)
```

The elements of `xs`, in order. What a literal builds a list with where it is written
`List[...]`.

## `array.toSeq`

```nanyx fragment
def array.toSeq: array(a) -> seq(a)
```

The elements of `xs`, in order, each read when the sequence is pulled. An array is
`Iterable` by it, so a `for` loop walks one.

<!-- End of the generated reference. -->
