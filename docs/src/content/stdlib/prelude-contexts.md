---
title: "prelude: contexts"
description: Equality, ordering, hashing, joining, and converting to and from text
order: 3
---

The contexts the operators are read through, and the `toString` and `fromString` of the built-in types. They are in the [prelude](prelude), so no import is needed. How a context is found, and synthesized from the definitions attached to a type, is described in [Contexts](../advanced/contexts).

# Converting to text

`int`, `float`, `bool` and `string` each have an attached `toString`. Each writes the value as string interpolation, `"{n}"`, does, and `string.toString` gives back the string itself. Interpolation goes through the [`ToString(a)`](#tostring) context, which a type of your own gets by attaching a `toString` of the same kind. A tag with a payload, a tuple, a record, a list, a map and a set have one the compiler derives from their parts: a map is written as its literal is, `[tea => 3, cake => 1]`, and a set as a list of its elements, `[3, 1]`. Because they are functions, they can be passed where interpolation cannot:

```nanyx
import nanyx/text

def scores = [3, 14, 15]
def text = scores \map(int.toString) \string.join(", ")
-- "3, 14, 15"

def answer = 42 \toString
-- "42"
```

A float is written as the shortest decimal that reads back as the same float, as JavaScript and Python's `repr` write one, and always with a point, so that it never looks like an `int`. From `1e-6` up to (but not including) `1e21` it is written in full; otherwise it is one digit, a point, the rest of its digits and an exponent, which is how a float literal is written in Nanyx. NaN and the infinities are written as words, and negative zero keeps its sign:

```nanyx fragment
"{0.1 + 0.2}"        -- "0.30000000000000004"
"{1.0 / 3.0}"        -- "0.3333333333333333"
"{5.0}"              -- "5.0"
"{0.000001}"         -- "0.000001"
"{0.0000001}"        -- "1.0e-7"
"{1.0e20}"           -- "100000000000000000000.0"
"{1.0e21}"           -- "1.0e21"
"{math.sqrt(-1.0)}"  -- "NaN"
"{1.0 / 0.0}"        -- "Infinity"
"{-1.0 / 0.0}"       -- "-Infinity"
"{0.0 * -1.0}"       -- "-0.0"
```

`ToString` is [synthesized](../advanced/contexts#context-synthesis) for these types from their `toString`, as it is for any other type with one attached, so nothing in the compiler treats them specially:

```nanyx fragment
def label: [ToString(a)] a -> string = { x -> "<" ++ toString(x) ++ ">" }

label(42)    -- "<42>"
label(true)  -- "<true>"
```

# Reading from text

`int`, `float` and `bool` each have an attached `fromString`, on the [numbers](prelude-numbers) and [prelude](prelude) pages. Each gives back an [option-like](../advanced/option-like-types) union, with its own tag for text it cannot read: `int.fromString("42")` is `#some(42)`, and `bool.fromString("yes")` is `#invalidBool`. The [sized number types](../advanced/sized-numbers#reading-from-text) have one each too, `I8.fromString` to `U64.fromString` and `F32.fromString`, each with its own tag: `U8.fromString("300")` is `#invalidU8`.

Each is the mirror of the type's `toString`, and, as `toString` gives a type a `ToString`, an attached `fromString` gives it the [`FromString`](#fromstring) context. Its second parameter, `e`, is what `fromString` gives back when the text holds no value, so that each type keeps its own tag. A function that reads a value of any type from text requires it, and a call finds the instance as it finds any other: synthesized from the attached `fromString` of the type it is called at, which also settles `e`. At `int` that is `FromString(int, #invalidInt)`:

```nanyx fragment
def readAll: [FromString(a, e)] list(string) -> list(#some(a) | e) = { xs -> xs \list.map(fromString) }

def total: list(#some(int) | #invalidInt) -> int = { results -> results \somes \list.fold(0, { sum, n -> sum + n }) }

def sumAll = { texts -> total(readAll(texts)) }

sumAll(["1", "x", "30"])  -- 31
```

A type of your own gets one by attaching a `fromString`, with a tag of its own for text it cannot read:

```nanyx fragment
def Point.fromString: string -> #some(Point) | #invalidPoint = { s -> ... }
```

The call has to settle `a` from what it is given or what its result is passed to. A result whose type is written only in an annotation, `def ints: list(#some(int) | #invalidInt) = readAll(texts)`, does not settle it yet ([#621](https://github.com/nanyx-lang/nanyx/issues/621)). The `e` parameter is expected to become an [associated type](../work-in-progress/type-members-and-reflection#associated-types) once those exist, as `e` is always determined by `a`.

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ by `nanyx stdlib-docs`: edit those, not this. -->

## `Eq`

```nanyx fragment
context Eq(a) = (
  `==`: a, a -> bool
)
```

How two values of type `a` are compared. `x == y` is read through it, and `x != y` is
`not (x == y)`. The compiler has its own for every type that holds no function, which compares
by structure: records field by field, lists element by element, tags by name and then payload.
Functions have no equality. A function that compares values of one of its type variables
requires it: `def same: [Eq(a)] a, a -> bool`.

## `Append`

```nanyx fragment
context Append(a) = (
  `++`: a, a -> a
)
```

The values of type `a` that join, end to end. `x ++ y` is read through it:
`def surround: [Append(a)] a, a -> a = { edge, x -> edge ++ x ++ edge }` works at `string` and
at any `list`. A type gets one by attaching `` def Path.`++` = ... ``.

## `` string.`++` ``

```nanyx fragment
def string.`++`: string, string -> string
```

`x` followed by `y`.

## `` list.`++` ``

```nanyx fragment
def list.`++`: list(a), list(a) -> list(a)
```

The elements of `xs` followed by those of `ys`.

## `Ord`

```nanyx fragment
context Ord(a) = (
  compare: a, a -> #less | #equal | #greater
)
```

How two values of type `a` are put in order. `x < y`, `x <= y`, `x > y` and `x >= y` are read
through it, so the four cannot disagree. The compiler has its own for numbers, `bool` (`false`
first), `string` (by bytes, which is the order of code points), and tuples and lists of them
(position by position, and a list that is a prefix of another first). Records and tags have no
order: a nominal type gets one by attaching `compare`, and a list of records is sorted by a key
with [`list.sortBy`](prelude-lists#listsortby). A function that orders values of one of its type variables requires it:
`def largest: [Ord(a)] list(a) -> ...`. `Ord` does not require `Eq`; a type that attaches both
should keep them consistent.

## `int.compare`

```nanyx fragment
def int.compare: int, int -> #less | #equal | #greater
```

Whether `x` is less than, equal to or greater than `y`.

## `float.compare`

```nanyx fragment
def float.compare: float, float -> #less | #equal | #greater
```

Whether `x` is less than, equal to or greater than `y`, with NaN after every other float, as
`__compare` orders floats.

## `bool.compare`

```nanyx fragment
def bool.compare: bool, bool -> #less | #equal | #greater
```

Whether `x` is less than, equal to or greater than `y`, with `false` first.

## `string.compare`

```nanyx fragment
def string.compare: string, string -> #less | #equal | #greater
```

Whether `x` is less than, equal to or greater than `y`, by bytes, which is the order of code
points.

## `Hash`

```nanyx fragment
context Hash(a) = (
  hash: a -> int
)
```

How a value of type `a` is hashed: an `int` that is the same for any two values `==` finds equal,
so that a map can find a key by its hash. The compiler has its own for every type it has its own
`Eq` for, which hashes by structure. A nominal type that attaches its own `==` must attach a
`hash` that agrees with it, or it has none: `def Email.hash = { e -> string.hash(lower(e)) }`
for an `==` that ignores case. A function that hashes values of one of its type variables
requires it: `def bucket: [Hash(a)] a, int -> int`.

## `int.hash`

```nanyx fragment
def int.hash: int -> int
```

The compiler's own hash of `x`, as [`Hash`](#hash) gives it to a type with no `hash` of its own.

## `float.hash`

```nanyx fragment
def float.hash: float -> int
```

The compiler's own hash of `x`, as [`Hash`](#hash) gives it to a type with no `hash` of its own.

## `bool.hash`

```nanyx fragment
def bool.hash: bool -> int
```

The compiler's own hash of `x`, as [`Hash`](#hash) gives it to a type with no `hash` of its own.

## `string.hash`

```nanyx fragment
def string.hash: string -> int
```

The compiler's own hash of `s`, as [`Hash`](#hash) gives it to a type with no `hash` of its own.

## `ToString`

```nanyx fragment
context ToString(a) = (
  toString: a -> string
)
```

How a value of type `a` is written as text. A hole in a string, `"Total: {total}"`, is written
through it, so a type can be interpolated when it has one: a type gets one by attaching a
`toString`, `def Point.toString = { p -> "({p.x}, {p.y})" }`, and a block can load another with
`use`. The compiler has its own for `int`, `float`, `bool`, `string` and tags without a payload,
and derives one for a tag with a payload, a tuple, a record, a list, a map and a set from the
`ToString` of each part: `#some(3)`, `(1, two)`, `(x = 1, y = 2)`, `[1, 2, 3]`, `[a => 1]`.

## `int.toString`

```nanyx fragment
def int.toString: int -> string
```

`n` in decimal, with a leading `-` when it is negative.

## `float.toString`

```nanyx fragment
def float.toString: float -> string
```

`x` in decimal, with the fewest digits that read back as `x`.

## `bool.toString`

```nanyx fragment
def bool.toString: bool -> string
```

`true` or `false`.

## `string.toString`

```nanyx fragment
def string.toString: string -> string
```

`s` itself.

## `FromString`

```nanyx fragment
context FromString(a, e) = (
  fromString: string -> #some(a) | e
)
```

How a value of type `a` is read from text, with `e` for text that does not hold one: [`int.fromString`](prelude-numbers#intfromstring)
gives back `#some(int) | #invalidInt`. A type gets one by attaching a `fromString`, as `int`,
`float` and `bool` do, so a function can read a value of whichever type it is called at:
`def readAll: [FromString(a, e)] list(string) -> list(#some(a) | e)`.

<!-- End of the generated reference. -->
