---
title: mstack
description: Mutable stacks, last in, first out
order: 10.7
---

Import it as `nanyx/mstack`. Reach for an `MStack` to keep a stack inside a `memory` block: `push` and `pop` change it in place, each in constant time. Like an [`MRef`](mref), it belongs to the block it was made in, and can't leave it.

```nanyx
import nanyx/mstack

def top = memory {
  def stack = MStack.empty()
  stack \push(5.0)
  stack \push(6.0)
  def popped = stack \pop -- #some(6.0)
  stack \peek -- #some(5.0)
}
```

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

Mutable stacks: last in, first out, changed in place.

An `MStack(a)` is a stack that [`MStack.push`](#mstackpush) and [`MStack.pop`](#mstackpop) change in place, each in
constant time. It holds its elements in an [`MRef`](mref), so it belongs to the `memory` block it
was made in: every operation requires that block's `Memory`, and the stack can't leave the block.

Each function is attached to `MStack`, so with `import nanyx/mstack` it is called as
`MStack.push(stack, 5)`, or as `stack \push(5)`. For an immutable stack, which needs no `memory`
block, see [`Stack`](stack).

## `MStack`

```nanyx fragment
type MStack(a) = [Memory] private MRef(list(a))
```

## `MStack.empty`

```nanyx fragment
def MStack.empty: [Memory] () -> MStack(a)
```

A new stack with nothing on it.

## `MStack.fromList`

```nanyx fragment
def MStack.fromList: [Memory] list(a) -> MStack(a)
```

A new stack holding `xs`, the first of them on top.

## `MStack.fromArray`

```nanyx fragment
def MStack.fromArray: [Memory] array(a) -> MStack(a)
```

A new stack holding `xs`, the first of them on top. What a literal builds a stack with,
`MStack[1, 2, 3]`, or `[1, 2, 3]` where a stack is expected.

## `MStack.push`

```nanyx fragment
def MStack.push: [Memory] MStack(a), a -> ()
```

Puts `value` on top of `stack`.

## `MStack.pop`

```nanyx fragment
def MStack.pop: [Memory] MStack(a) -> #some(a) | #none
```

Takes the value on top of `stack` off it, or `#none` when it is empty.

## `MStack.peek`

```nanyx fragment
def MStack.peek: [Memory] MStack(a) -> #some(a) | #none
```

The value on top of `stack`, left where it is, or `#none` when it is empty.

## `MStack.isEmpty`

```nanyx fragment
def MStack.isEmpty: [Memory] MStack(a) -> bool
```

Whether `stack` has nothing on it.

## `MStack.length`

```nanyx fragment
def MStack.length: [Memory] MStack(a) -> int
```

How many values are on `stack`.

## `MStack.toSeq`

```nanyx fragment
def MStack.toSeq: [Memory] MStack(a) -> seq(a)
```

The values on `stack` now, the top one first, as a sequence: pushing or popping later doesn't
change it. A mutable stack is `Iterable` by it, so a `for` loop walks one.

## `MStack.toList`

```nanyx fragment
def MStack.toList: [Memory] MStack(a) -> list(a)
```

The values on `stack`, the top one first.

<!-- End of the generated reference. -->
