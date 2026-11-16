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

<!-- End of the generated reference. -->
