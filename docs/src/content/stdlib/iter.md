---
title: "iter"
description: "Generic iterable helpers via Iter(a) context"
order: 11
---

> **Planned.** This module is not yet in the library: neither the `Iter(a)` context nor any of the functions on this page exists. The work is tracked in [#192](https://github.com/nanyx-lang/nanyx/issues/192). Until then, use the [list](./collections), [map](./map) and [set](./set) functions.

The `iter` module provides generic operations for any type `a` for which an `Iter(a)` context is provided.

# Contexts

## Iter(a)

```nanyx
context Iter(a) = (
  elem: type
  next: a -> #some(elem, a) | #done
)
```

Associated types are not implemented yet, so this module is a design rather than working code. The element type for the iterable is stored as an [associated type](../work-in-progress/type-members-and-reflection#associated-types) on the `Iter(..)` context with the name `elem`; for an iterable argument, it is always available as `elem` in the function signature (or `Iter(a).elem`, if disambiguation is needed).

# Definitions

## all

```nanyx
all: [Iter(a)] (a, (elem -> bool)) -> bool
```

Returns `true` if all elements satisfy the predicate.

```nanyx
items\all { > 0 }
```

---

## any

```nanyx
any: [Iter(a)] (a, (elem -> bool)) -> bool
```

Returns `true` if any element satisfies the predicate.

```nanyx
items\any { > 0 }
```

---

## count

```nanyx
count: [Iter(a)] a -> int
```

Counts elements by iterating through the input.

```nanyx
items\count
```

---

## find

```nanyx
find: [Iter(a)] (a, (elem -> bool)) -> #some(elem) | #notFound
```

Finds the first matching element.

```nanyx
items\find { .id == targetId }
```

---

## first

```nanyx
first: [Iter(a)] a -> #some(elem) | #emptyIterable
```

Returns the first element wrapped in `#some`, or `#emptyIterable`.

```nanyx
items\first
```

---

## firstOr

```nanyx
firstOr: [Iter(a)] (a, elem) -> elem
```

Returns the first element, or the fallback when the iterable is empty.

```nanyx
items\firstOr(fallback)
```

---

## fold

```nanyx
fold: [Iter(a)] (a, s, ((s, elem) -> s)) -> s
```

Folds elements from left to right.

```nanyx
items\fold(0) { + }
```

---

## toList

```nanyx
toList: [Iter(a)] a -> list(elem)
```

Materializes an iterable into a list.

```nanyx
items\toList
```
