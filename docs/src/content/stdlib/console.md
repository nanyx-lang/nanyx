---
title: console
description: Reading from and writing to the terminal
order: 11
---

Import it as `nanyx/console`. The program's `@entry` function runs in `Console`, so `print` and the rest are available in `main` without any setup; any other function that calls them must declare `[Console]` in its type.

```nanyx
import nanyx/console

match readLine()
  | #some(line) -> println("You said: {line}")
  | #error(#endOfInput) -> println("No input")
```

# dbg

`dbg` is built into the language rather than defined in this module, but it writes through `Console` too:

```nanyx fragment
dbg: [Console] a -> a
```

It prints any value, after `dbg: `, and gives it back, so it can be put in the middle of a pipeline:

```nanyx fragment
[1, 2, 3]
  \map { * 2 }
  \dbg
-- Prints: dbg: [2, 4, 6]
```

A value is printed as you would write it in code, from its type, whatever `toString` the type has:

| Value | Printed |
| --- | --- |
| `42`, `1.5`, `true` | `42`, `1.5`, `true` |
| `"Ada"` | `"Ada"`, in quotes and escaped |
| `#some(5)`, `#pair(1, "a")` | `#some(5)`, `#pair(1, "a")` |
| `(1, "a")`, `(name = "Ada", age = 36)` | `(1, "a")`, `(age = 36, name = "Ada")` |
| `[1, 2]` | `[1, 2]` |
| `["tea" => 3, "coffee" => 1]`, an empty map | `Map["tea" => 3, "coffee" => 1]`, `Map[]` |
| `set.fromList([1, 2])`, an empty set | `Set[1, 2]`, `Set[]` |
| `Money(250)` | `Money(250)` |
| `()` | `()` |
| `{ n -> n + 1 }` | `<function int -> int>` |

A record's fields are printed in alphabetical order. A map's entries and a set's elements are printed in the order the map or set holds them, after its type's name. A nominal type is printed by its name around its shape, in its own module or any other.

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ by `nanyx stdlib-docs`: edit those, not this. -->

How a program talks to the terminal.

[`Console`](#console) is a context. Code that needs the terminal declares `[Console]` and calls its members,
which come from whichever `Console` instance its caller supplies. The members are primitives that
take text as it is: [`print`](#print), [`println`](#println), [`eprint`](#eprint) and [`eprintln`](#eprintln) are written on top of them,
so an instance supplies `write` and `writeError` and gets all four.

## `IoError`

```nanyx fragment
type IoError = #endOfInput
```

Why reading from standard input gave no line: the input has ended.

## `Console`

```nanyx fragment
context Console = (
  write: string -> ()
  writeError: string -> ()
  readLine: () -> #some(string) | #error(IoError)
  readToEnd: () -> #some(string) | #error(IoError)
  flush: () -> ()
)
```

The terminal: standard output, standard error and standard input. Code that needs it declares
`[Console]`, and its caller supplies an instance; the host supplies one to the program's entry point
(`nanyx/console.host`'s reads and writes the standard streams). `print`, `println`, `eprint` and `eprintln` write through it.

- `write`: Writes `text` to standard output, as it is.
- `writeError`: Writes `text` to standard error, as it is.
- `readLine`: The next line of standard input, without its line break, or `#error(#endOfInput)` when the
  input has ended.
- `readToEnd`: Everything left on standard input (the empty string when nothing is).
- `flush`: Writes out anything the streams hold back. The console host holds nothing back, so there it
  does nothing.

## `print`

```nanyx fragment
def print: [Console & ToString(a)] a -> ()
```

Writes `value` to standard output as its `toString` writes it: a string as it is, `print(42)`
as `42`, `print([1, 2])` as `[1, 2]`. A value with no `ToString`, such as a function, is an error
at the call.

## `println`

```nanyx fragment
def println: [Console & ToString(a)] a -> ()
```

Writes `value` followed by a line break to standard output, as `print` writes it.

## `eprint`

```nanyx fragment
def eprint: [Console & ToString(a)] a -> ()
```

Writes `value` to standard error, as `print` writes it.

## `eprintln`

```nanyx fragment
def eprintln: [Console & ToString(a)] a -> ()
```

Writes `value` followed by a line break to standard error, as `print` writes it.

<!-- End of the generated reference. -->
