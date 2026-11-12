---
title: Overview
description: "Core abstractions and types"
order: 1
---

This page gives a high-level overview of core standard library abstractions.

# Modules in this section

- [io](./io) — console and stream-style input/output helpers
- [collections](./collections) — list and collection transforms
- [map](./map) — maps from keys to values
- [set](./set) — sets of values, each held once
- [string](./string) — UTF-8 string utilities
- [option](./option) — optional-value helpers
- [result](./result) — success/error composition helpers
- [math](./math) — numeric utilities
- [sequence](./sequence) — lazy sequence helpers (planned, not yet in the library)
- [iter](./iter) — generic `Iter(a)`-based helpers (planned, not yet in the library)

# Named parameters

A [named parameter](../tour/functions#named-parameters) must be passed by name. The standard library names a parameter only when a call can't show what it is without the name: usually when two or more parameters after the receiver have the same type, or when a bare literal would leave you guessing its role. It never names the receiver (the value you pipe in), a lambda argument, or a single extra argument that the function's name already explains.

```nanyx
-- Named: a bare literal or two arguments of the same type
"a,b" \split(on = ",")
"7" \padStart(width = 3, fill = "0")
"hello world" \replace(pattern = "o", replacement = "0")
x \math.clamp(min = 0, max = 10)

-- Positional: one extra argument that the name explains
["a", "b"] \string.join("-")
"nanyx" \startsWith("nan")
```

The functions with named parameters are `string.split`, `string.substring`, `string.padStart`, `string.padEnd`, `string.replace` and `math.clamp`. Everything else takes its arguments by position.

Passing one of these by position is an error that names the parameters to write:

```nanyx
"7" \padStart(3, "0")
-- error NYX0218: 'string.padStart' takes 'width' and 'fill' by name, not by position: write 'width = ...' and 'fill = ...'
```

# Core abstractions

These exist today:

- `list(a)` for indexed lists
- `set(a)` for membership checks
- `map(k, v)` for key/value lookup

These are planned, and not yet in the library:

- `seq(a)` for finite sequences. Today `seq(a)` is only the type of a range in a `for` loop; sequences as values, and the [sequence](./sequence) functions on them, are tracked in [#192](https://github.com/nanyx-lang/nanyx/issues/192).
- `gen(a)` for potentially infinite sequences
- `lookup(k, v)` for multi-value lookups

# Async collections

Both are planned, and not yet in the library:

- `stream(a)` for async streams
- `async(a)` for async computations

# Option and result

There is no `Option` type. Any union containing `#some(a)` is [option-like](../advanced/option-like-types) by convention, and the `option` module's helpers work on all of them; `#some(a) | #none` is simply the most common shape.

The `Result` alias exists for the equally common success/error shape:

```nanyx
type Result(a, e) = #some(a) | #error(e)
```

# Discarding a value

`ignore`, in the prelude, takes a value of any type, throws it away and gives back `()`:

```nanyx
ignore: a -> ()
```

It is for a result you don't need, without binding it to a name you never use, and reads well at the end of a pipeline:

```nanyx
items \list.map(process) \ignore
```

# Converting to text

`int`, `float`, `bool` and `string` each have an attached `toString`. They are in the [prelude](../tour/modules#modules-in-the-standard-library), so no import is needed.

```nanyx
int.toString: int -> string
float.toString: float -> string
bool.toString: bool -> string
string.toString: string -> string
```

Each writes the value as string interpolation, `"{n}"`, does, and `string.toString` gives back the string itself. Interpolation goes through the prelude's [`ToString(a)`](../advanced/contexts#tostringa) context, which a type of your own gets by attaching a `toString` of the same kind. Because they are functions, they can be passed where interpolation cannot:

```nanyx
import nanyx/text

def scores = [3, 14, 15]
def text = scores \map(int.toString) \string.join(", ")
-- "3, 14, 15"

def answer = 42 \toString
-- "42"
```

A float is written as the shortest decimal that reads back as the same float, as JavaScript and Python's `repr` write one, and always with a point, so that it never looks like an `int`. From `1e-6` up to (but not including) `1e21` it is written in full; otherwise it is one digit, a point, the rest of its digits and an exponent, which is how a float literal is written in Nanyx. NaN and the infinities are written as words, and negative zero keeps its sign:

```nanyx
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

A [context](../advanced/contexts) whose one member is `toString: a -> string` is [synthesized](../advanced/contexts#context-synthesis) for these types from their `toString`, as it is for any other type with one attached, so nothing in the compiler treats them specially:

```nanyx
context ToString(a) = (toString: a -> string)

def label: [ToString(a)] a -> string = { x -> "<" ++ toString(x) ++ ">" }

label(42)    -- "<42>"
label(true)  -- "<true>"
```

# Reading from text

`int`, `float` and `bool` each have an attached `fromString`, also in the prelude. Each gives back an [option-like](../advanced/option-like-types) union, with its own tag for text it cannot read:

```nanyx
int.fromString: string -> #some(int) | #invalidInt
float.fromString: string -> #some(float) | #invalidFloat
bool.fromString: string -> #some(bool) | #invalidBool
```

`int.fromString` reads a decimal integer with an optional leading `-` or `+`. `float.fromString` reads an optional sign, digits with an optional `.` among them, and an optional exponent, `e` or `E` and an integer, so `"1.5"`, `"-0.25"`, `"42"`, `".5"`, `"5."` and `"1.0e-7"` are all read; it also reads `NaN`, `Infinity` and `-Infinity` as `float.toString` writes them. `""`, `"-"`, `"abc"`, `"1.2.3"` and `" 1.5"` are `#invalidFloat`. `bool.fromString` reads `true` or `false`, exactly as `bool.toString` writes them, so `"True"` and `" true"` are `#invalidBool`.

```nanyx
def n = int.fromString("42")         -- #some(42)
def x = float.fromString("-0.25")    -- #some(-0.25)
def b = "false" \bool.fromString     -- #some(false)
def bad = bool.fromString("yes")     -- #invalidBool
```

A float read from text is the nearest float to the decimal when the decimal has at most 15 significant digits and its exponent, with the point moved to the end of the digits, is between -22 and 22. That covers numbers written by hand, and every float `float.toString` writes with 15 digits or fewer. Past that the result is not correctly rounded and can be a unit in the last place away from the nearest float, so a float written with 16 or 17 digits does not always read back as itself.

Each is the mirror of the type's `toString`, and, as `toString` gives a type a `ToString`, an attached `fromString` gives it the prelude's `FromString` context. It has a second parameter, `e`, for what `fromString` gives back when the text holds no value, so that each type keeps its own tag:

```nanyx
context FromString(a, e) = (fromString: string -> #some(a) | e)
```

A function that reads a value of any type from text requires it. A call finds the instance as it finds any other: [synthesized](../advanced/contexts#context-synthesis) from the attached `fromString` of the type it is called at, which also settles `e`. At `int` that is `FromString(int, #invalidInt)`:

```nanyx
def readAll: [FromString(a, e)] list(string) -> list(#some(a) | e) = { xs -> xs \list.map(fromString) }

def total: list(#some(int) | #invalidInt) -> int = { results -> results \somes \list.fold(0, { sum, n -> sum + n }) }

def sumAll = { texts -> total(readAll(texts)) }

sumAll(["1", "x", "30"])  -- 31
```

A type of your own gets one by attaching a `fromString`, with a tag of its own for text it cannot read:

```nanyx
def Point.fromString: string -> #some(Point) | #invalidPoint = { s -> ... }
```

The call has to settle `a` from what it is given or what its result is passed to. A result whose type is written only in an annotation, `def ints: list(#some(int) | #invalidInt) = readAll(texts)`, does not settle it yet ([#621](https://github.com/nanyx-lang/nanyx/issues/621)).

The `e` parameter is expected to become an [associated type](../work-in-progress/type-members-and-reflection#associated-types) once those exist, as `e` is always determined by `a`.

# Converting between numbers

Arithmetic doesn't mix `int` and `float`: `n * 1.5` is a type error when `n` is an `int`. `int.toFloat`, in the prelude, turns an `int` into a `float`. Every `int` has an exact `float`, so it never fails.

```nanyx
int.toFloat: int -> float
```

```nanyx
def n = 3
def x = int.toFloat(n) * 1.5     -- 4.5
def half = (7 \toFloat) / 2.0    -- 3.5
```

To turn a `float` back into an `int`, choose how to drop the fraction with `floor`, `ceil`, `round` or `truncate` from [`nanyx/math`](math).

# Map literals

Maps are written with the square-bracket literal syntax and the `=>` operator:

```nanyx
def myMap = ["hello" => "world", "foo" => "bar"]

myMap("hello")   -- #some("world")
```

A literal whose items are all `key => value` is a `map(k, v)`; calling it looks a key up. The functions on maps are on the [map](map) page, and sets are on the [set](set) page.

There is no set literal: a set is made from a list, `set.fromList([1, 2, 3])`. Literals that build any collection type, through the target type's builder, are planned; see [Collections](../tour/collections#literal-types).

If you want more detail, visit the specific module pages in this section.
