---
title: queue
description: Immutable queues, first in, first out
order: 10.82
---

Import it as `nanyx/queue`. A `Queue` takes values in at the back with `enqueue` and gives them out at the front with `dequeue`, each in constant time averaged over all the calls, and each gives back a new queue, leaving the one it was given as it was.

```nanyx
import nanyx/queue

def waiting = Queue["ada", "bob"] \enqueue("cy")

match Queue.dequeue(waiting)
  | #some(next, rest) -> println("{next} {Queue.toList(rest)}") -- ada [bob, cy]
  | #none -> println("nobody")
```
# Reference

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

Immutable queues: first in, first out.

A `Queue(a)` takes values in at the back with [`Queue.enqueue`](#queueenqueue) and gives them out at the front
with [`Queue.dequeue`](#queuedequeue). It holds two lists, the front in order and the back reversed: `enqueue`
puts a value on the back list, and `dequeue` takes one off the front list, reversing the back list
into it when it runs out. Each value is moved once, so both take constant time averaged over all
the calls. Nothing about a queue changes once it is made: each gives back a new queue, and the old
one still holds what it held.

Two queues are `==` when they hold equal values in the same order, however each splits them
between its two lists, and [`Queue.hash`](#queuehash) agrees. [`Queue.toString`](#queuetostring) writes one as
`Queue[1, 2, 3]`, the front first.

Each function is attached to `Queue`, so with `import nanyx/queue` it is called as
`Queue.enqueue(q, 5)`, or as `q \enqueue(5)`. A literal makes one, the first value at the front:
`Queue[1, 2, 3]`, or `[1, 2, 3]` where a queue is expected.

## `Queue`

```nanyx fragment
type Queue(a) = protected (front: list(a), back: list(a), count: int)
```

## `Queue.empty`

```nanyx fragment
def Queue.empty: Queue(a)
```

The queue with nothing in it.

## `Queue.fromList`

```nanyx fragment
def Queue.fromList: list(a) -> Queue(a)
```

A queue holding `xs`, the first of them at the front.

## `Queue.fromArray`

```nanyx fragment
def Queue.fromArray: array(a) -> Queue(a)
```

A queue holding `xs`, the first of them at the front. What a literal builds a queue with,
`Queue[1, 2, 3]`, or `[1, 2, 3]` where a queue is expected.

## `Queue.enqueue`

```nanyx fragment
def Queue.enqueue: Queue(a), a -> Queue(a)
```

`queue` with `value` at the back of it.

## `Queue.dequeue`

```nanyx fragment
def Queue.dequeue: Queue(a) -> #some(a, Queue(a)) | #none
```

The value at the front of `queue` and the queue behind it, or `#none` when it is empty.

## `Queue.peek`

```nanyx fragment
def Queue.peek: Queue(a) -> #some(a) | #none
```

The value at the front of `queue`, or `#none` when it is empty.

## `Queue.isEmpty`

```nanyx fragment
def Queue.isEmpty: Queue(a) -> bool
```

Whether `queue` has nothing in it.

## `Queue.count`

```nanyx fragment
def Queue.count: Queue(a) -> int
```

How many values are in `queue`.

## `Queue.toList`

```nanyx fragment
def Queue.toList: Queue(a) -> list(a)
```

The values in `queue`, the front one first.

## `` Queue.`==` ``

```nanyx fragment
def Queue.`==`: [Eq(a)] Queue(a), Queue(a) -> bool
```

Whether `p` and `q` hold equal values in the same order.

## `Queue.hash`

```nanyx fragment
def Queue.hash: [Hash(a)] Queue(a) -> int
```

The hash of the values in `queue`, in order: the same for any two queues `==` finds equal.

## `Queue.toString`

```nanyx fragment
def Queue.toString: [ToString(a)] Queue(a) -> string
```

`queue` as its literal is written, the front first: `Queue[1, 2, 3]`, and `Queue[]` when it is
empty.

<!-- End of the generated reference. -->