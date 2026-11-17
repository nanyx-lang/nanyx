---
title: stack
description: Immutable stacks, last in, first out
order: 10.81
---

Import it as `nanyx/stack`. A `Stack` is a list read from one end: `push` puts a value on top and `pop` takes it off, each in constant time, and each gives back a new stack, leaving the one it was given as it was. For a stack that changes in place inside a `memory` block, see [`MStack`](mstack).

```nanyx
import nanyx/stack

def stack = Stack[2, 1] \push(3)

match Stack.pop(stack)
  | #some(top, rest) -> println("{top} {Stack.toList(rest)}") -- 3 [2, 1]
  | #none -> println("empty")
```
# Reference

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

Immutable stacks: last in, first out.

A `Stack(a)` is a list read from one end: [`Stack.push`](#stackpush) puts a value on top and [`Stack.pop`](#stackpop)
takes it off, each in constant time. Nothing about a stack changes once it is made: each of them
gives back a new stack, which shares the rest of its values with the one it was made from, so the
old one still holds what it held. For a stack that changes in place, see [`MStack`](mstack).

Two stacks are `==` when they hold equal values in the same order, and a stack can be a map's key
when its values can. [`Stack.toString`](#stacktostring) writes one as `Stack[3, 2, 1]`, the top first.

Each function is attached to `Stack`, so with `import nanyx/stack` it is called as
`Stack.push(s, 5)`, or as `s \push(5)`. A literal makes one, the first value on top:
`Stack[3, 2, 1]`, or `[3, 2, 1]` where a stack is expected.

## `Stack`

```nanyx fragment
type Stack(a) = protected list(a)
```

## `Stack.empty`

```nanyx fragment
def Stack.empty: Stack(a)
```

The stack with nothing on it.

## `Stack.fromList`

```nanyx fragment
def Stack.fromList: list(a) -> Stack(a)
```

A stack holding `xs`, the first of them on top.

## `Stack.fromArray`

```nanyx fragment
def Stack.fromArray: array(a) -> Stack(a)
```

A stack holding `xs`, the first of them on top. What a literal builds a stack with,
`Stack[3, 2, 1]`, or `[3, 2, 1]` where a stack is expected.

## `Stack.push`

```nanyx fragment
def Stack.push: Stack(a), a -> Stack(a)
```

`stack` with `value` on top of it.

## `Stack.pop`

```nanyx fragment
def Stack.pop: Stack(a) -> #some(a, Stack(a)) | #none
```

The value on top of `stack` and the stack below it, or `#none` when it is empty.

## `Stack.peek`

```nanyx fragment
def Stack.peek: Stack(a) -> #some(a) | #none
```

The value on top of `stack`, or `#none` when it is empty.

## `Stack.isEmpty`

```nanyx fragment
def Stack.isEmpty: Stack(a) -> bool
```

Whether `stack` has nothing on it.

## `Stack.length`

```nanyx fragment
def Stack.length: Stack(a) -> int
```

How many values are on `stack`. It counts them, so it takes time in proportion to how many
there are.

## `Stack.toList`

```nanyx fragment
def Stack.toList: Stack(a) -> list(a)
```

The values on `stack`, the top one first.

## `Stack.toString`

```nanyx fragment
def Stack.toString: [ToString(a)] Stack(a) -> string
```

`stack` as its literal is written, the top first: `Stack[3, 2, 1]`, and `Stack[]` when it is
empty.

## `Stack.reduce`

```nanyx fragment
def Stack.reduce: [Default(a), c] Stack(a), ([c] (a, a) -> a) -> a
```

The elements of `s`, from the top, combined with `f`, in order, starting from the element type's
`Default`: a stack of `1`, `2` and `3` reduced with `{ + }` is `6`. An empty one gives the
default, so `reduce` never fails, unlike a `reduce` that starts from the first element. To start
from another value, use [`list.fold`](prelude-lists#listfold).

<!-- End of the generated reference. -->