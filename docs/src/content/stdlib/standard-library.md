---
title: Overview
description: "Core abstractions and types"
order: 1
---

This page gives a high-level overview of core standard library abstractions.

# Modules in this section

- [io](./io) — console and stream-style input/output helpers
- [collections](./collections) — list and collection transforms
- [string](./string) — UTF-8 string utilities
- [option](./option) — optional-value helpers
- [result](./result) — success/error composition helpers
- [math](./math) — numeric utilities
- [sequence](./sequence) — lazy/iterable sequence helpers
- [iter](./iter) — generic `Iter(a)`-based helpers

# Named parameters

A [named parameter](../tour/functions#named-parameters) must be passed by name. The standard library names a parameter only when a call can't show what it is without the name: usually when two or more parameters after the receiver have the same type, or when a bare literal would leave you guessing its role. It never names the receiver (the value you pipe in), a lambda argument, or a single extra argument that the function's name already explains.

```nanyx
-- Named: a bare literal or two arguments of the same type
"a,b" \split(on = ",")
"7" \padStart(width = 3, fill = "0")
"hello world" \replace(pattern = "o", replacement = "0")
x \math.clamp(min = 0, max = 10)

-- Positional: one extra argument that the name explains
["a", "b"] \String.join("-")
"nanyx" \startsWith("nan")
```

The functions with named parameters are `String.split`, `String.substring`, `String.padStart`, `String.padEnd`, `String.replace` and `math.clamp`. Everything else takes its arguments by position.

Passing one of these by position is an error that names the parameters to write:

```nanyx
"7" \padStart(3, "0")
-- error NYX0218: 'String.padStart' takes 'width' and 'fill' by name, not by position: write 'width = ...' and 'fill = ...'
```

# Core abstractions

- `seq(a)` for finite sequences
- `gen(a)` for potentially infinite sequences
- `list(a)` for indexed lists
- `set(a)` for membership checks
- `map(k, v)` for key/value lookup
- `lookup(k, v)` for multi-value lookups

# Async collections

- `stream(a)` for async streams
- `async(a)` for async computations

# Option and result

There is no `Option` type. Any union containing `#some(a)` is [option-like](../advanced/option-like-types) by convention, and the `option` module's helpers work on all of them; `#some(a) | #none` is simply the most common shape.

The `Result` alias exists for the equally common success/error shape:

```nanyx
type Result(a, e) = #some(a) | #error(e)
```

# Converting to text

`int`, `float`, `bool` and `string` each have an attached `toString`. They are in the [prelude](../tour/modules#modules-in-the-standard-library), so no import is needed.

```nanyx
Int.toString: int -> string
Float.toString: float -> string
Bool.toString: bool -> string
String.toString: string -> string
```

Each writes the value as string interpolation, `"{n}"`, does, and `String.toString` gives back the string itself. Interpolation goes through the prelude's [`ToString(a)`](../advanced/contexts#tostringa) context, which a type of your own gets by attaching a `toString` of the same kind. Because they are functions, they can be passed where interpolation cannot:

```nanyx
import nanyx/text

def scores = [3, 14, 15]
def text = scores \map(Int.toString) \String.join(", ")
-- "3, 14, 15"

def answer = 42 \toString
-- "42"
```

A [context](../advanced/contexts) whose one member is `toString: a -> string` is [synthesized](../advanced/contexts#context-synthesis) for these types from their `toString`, as it is for any other type with one attached, so nothing in the compiler treats them specially:

```nanyx
context ToString(a) = (toString: a -> string)

def label: [ToString(a)] a -> string = { x -> "<" ++ toString(x) ++ ">" }

label(42)    -- "<42>"
label(true)  -- "<true>"
```

# Reading from text

`int` and `bool` each have an attached `parse`, also in the prelude. Each gives back an [option-like](../advanced/option-like-types) union, with its own tag for text it cannot read:

```nanyx
Int.parse: string -> #some(int) | #invalidInt
Bool.parse: string -> #some(bool) | #invalidBool
```

`Int.parse` reads a decimal integer with an optional leading `-` or `+`. `Bool.parse` reads `true` or `false`, exactly as `Bool.toString` writes them, so `"True"` and `" true"` are `#invalidBool`.

```nanyx
def n = Int.parse("42")          -- #some(42)
def b = "false" \Bool.parse      -- #some(false)
def bad = Bool.parse("yes")      -- #invalidBool
```

# Converting between numbers

Arithmetic doesn't mix `int` and `float`: `n * 1.5` is a type error when `n` is an `int`. `Int.toFloat`, in the prelude, turns an `int` into a `float`. Every `int` has an exact `float`, so it never fails.

```nanyx
Int.toFloat: int -> float
```

```nanyx
def n = 3
def x = Int.toFloat(n) * 1.5     -- 4.5
def half = (7 \toFloat) / 2.0    -- 3.5
```

To turn a `float` back into an `int`, choose how to drop the fraction with `floor`, `ceil`, `round` or `truncate` from [`nanyx/math`](math).

# Map literals

Maps are written with the square-bracket literal syntax and the `=>` operator:

```nanyx
def myMap = ["hello" => "world", "foo" => "bar"]
```

Square-bracket literals desugar to a call to the target type's builder, which is what makes the same syntax work for lists, sets and maps. See [Collections](../tour/collections).

If you want more detail, visit the specific module pages in this section.
