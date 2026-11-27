---
title: "Attributes"
description: "Metadata annotations such as @entry and @partial"
order: 13
---

Attributes are annotations that start with `@` and modify how the compiler treats the declaration that follows.

# Basic syntax

Place an attribute immediately before the declaration it applies to:

```nanyx fragment
@entry
def main = { ... }
```

```nanyx fragment
@partial module utils
```

A declaration can have one or more attributes, each on its own line. An attribute may also precede a member in a `context` declaration.

An attribute that takes an argument has it in parentheses straight after its name, written as a tag's payload is: `@customKeywordType(#bind)`. Several values make a tuple and named ones a record. `@entry` takes no argument.

# Common attributes

## `@entry`

Marks the program entry point: the function the runtime calls when the package is run. A package may contain at most one `@entry`.

```nanyx
module main

@entry
def main = {
  println("Hello, World!")
}
```

See [Hello, World!](../getting-started/hello-world) and [Entrypoints](./packages#entrypoints).

## `@partial`

Marks a file's module declaration as one fragment of a module split across files. It is written on the declaration's line, and every file of the module must have it.

```nanyx fragment
@partial module utils
```

See [Partial modules](./modules#partial-modules).

## `@customKeywordType(...)`

Says how a custom keyword of a workflow behaves. A workflow builder runs its block under a context whose members are its keywords, and the attribute is placed before the member's declaration in that `context`.

```nanyx fragment
context Async = (
  @customKeywordType(#bind)
  await: [c] (Task(a), ([c] a -> Task(b))) -> Task(b)
)
```

See [Workflows](../advanced/workflows).

## `@extern(...)`

Declares a foreign function: a function the WebAssembly module imports from its embedder, such as a WASI call. Its two arguments are the import's module and name, and the def has a type and no value. The type takes and returns only WebAssembly's value types (`I32`, `I64`, `F32`, `F64`, or `()` for no result), and requires `HostRuntime`, so only a [host](../advanced/hosts) can call it.

```nanyx fragment
@extern("wasi_snapshot_preview1", "args_sizes_get")
def argsSizesGet: [HostRuntime] I32, I32 -> I32
```

See [Foreign functions](../advanced/hosts#foreign-functions).

## `@component`

Exports a function from the component that `nanyx build` writes, for a component written in another language to call.
The def must be exported from its module, whose name is the WIT interface it is exported in, and its type is written
into the component's WIT as it is: a `float` is an `f64`, a `string` a `string`, a named record a `record` and a
named tag union a `variant`. A function, a context or a type variable can't cross a component's boundary, so a type
that has one is an error.

```nanyx fragment
module shapes

@component
export def area: float, float -> float = { width, height -> width * height }
```

An export whose type writes the contexts it requires, as `[Clock] int -> int` does, runs under its host's, as the
entry point does; a context the host doesn't provide is an error at the attribute. A package with `@component` defs and
no `@entry` is a library component: `nanyx build` writes a component that exports them and has no `run`. See [Components](../deep-dives/components#a-programs-own-exports).

## `@import(...)`

Declares a function another component exports, which the component that `nanyx build` writes imports and calls. Its
arguments are the WIT interface's full name, with its version, the function's name, which is the def's in kebab case
if it is left out, and the names of the function's parameters, which are `p0`, `p1` and on if they are left out. The
def has a type and no value, and its type crosses a component's boundary as `@component`'s does.

```nanyx fragment
@import("acme:geometry/shapes@1.0.0", "area", ["width", "height"])
def area: float, float -> float
```

Only a component can call it, so `nanyx run` and `nanyx test` report an error where a program calls one. See
[Components](../deep-dives/components#a-programs-own-imports).

## `@test`

Marks a def as a test: a block, which takes no arguments and gives back `()`, that checks what it is about with
`assert`. It can sit beside the code it tests, where it sees the module's private definitions, or in a file the
manifest's `tests` selects ([Test files](./packages#test-files)).

```nanyx fragment
def double: int -> int = { n -> n * 2 }

@test
def `doubles` = {
  assert(double(2) == 4)
}
```

Tests are part of a test build alone, and every other build leaves them out, so only another test may refer to one
(`NYX0311`), and linters don't report a test as unused. A def of another type is an error (`NYX0310`).
[`nanyx test`](../standard-tools/command-line#nanyx-test) runs them.

A test runs under the test host, which provides `Console`, whose output it keeps and shows only for a test that fails,
and `Random`, seeded so that a test draws the same numbers when it runs with the same seed. A test that requires any
other context is an error (`NYX0312`), as an entry point is that requires one its host doesn't provide, so a test never
touches the file system or the environment unless it says so. It loads what it needs with `use`, as a fake that does
what the test wants:

```nanyx fragment
@test
def `loads the defaults when there is no config file` = {
  use FileSystem(readFile = { _ -> #notFound }, pathExists = { _ -> false })
  assert(config.load("app.toml") == Config.default)
}
```

`@test(#io)` runs a test with the console host's real `Console`, `Env` and `FileSystem`, and `Async`, for a test that is
meant to read the package's files or the environment. It runs in the package's directory, or the script's, and what it
prints is still kept:

```nanyx fragment
@test(#io)
def `reads the sample config` = {
  assert(config.load("tests/sample.toml") == #some(Config(port = 8080)))
}
```

Where a test lacks only contexts `#io` provides, `NYX0312` says so, and an editor offers to mark the test
`@test(#io)`.

A test can't catch a crash, so `@test(#crashes)` marks one that passes only if it crashes, as `crash`, `orCrash` and a
failed `assert` do, and fails if it finishes. `@test(#crashes("text"))` passes only if the crash's message contains the
text. With `#io`, the two are written together, `@test(#io, #crashes)`:

```nanyx fragment
@test(#crashes("no first element"))
def `first of an empty list crashes` = {
  println(first([]))
}
```

An error the compiler found and deferred, such as a type error or a `match` that doesn't handle every case, isn't a
crash a test can expect, and fails it as it fails any test, and so do a runtime error, such as an integer overflow, and
running past the time a test has.

## `@allow(...)`

Lets a definition break [linter](../standard-tools/official-linter) rules: before a def, a local one too, or a top-level type, it suppresses the diagnostics of the rules it names inside that definition. Each rule is written as its linter and its name, with no spaces around the `/`, and several are separated by commas. It is the only attribute a block can hold.

```nanyx fragment
@allow(default/unusedBindings, default/unusedResults)
def draft: [Console] () -> int = { ->
  def rate = 2
  readLine()
  100
}
```

An `@allow` that suppresses nothing is itself reported, by the official linter's `unusedAllows` rule.

## `@baseUnit`

Marks a unit declaration as the default display unit of its dimension: the unit its values are written in when no other unit is chosen, as zero is. It may be a multiple of another unit, and is optional: a dimension with no `@baseUnit` is written in its default storage unit.

```nanyx fragment
@baseUnit unit s: Time = 1_000ms
```

## `@storageUnit`

Marks a unit declaration as the default storage unit of its dimension: the unit its values are stored as a number of. Without it, the compiler works it out, as the largest unit every unit declared with the dimension is a whole number of. Marking it keeps it from changing: a smaller unit added later is an error, not a change to what every value holds.

```nanyx fragment
@storageUnit unit ns: Time
```

See [Units of measure](../advanced/units-of-measure#dimensions-and-units).

# Attributes on fields

A field of a record type's definition can have attributes of any name, written on the lines before it. The compiler gives them no meaning: a library reads them from the [type's description](functions#inspecting-a-type), as a JSON encoder reads the name to write a field under:

```nanyx fragment
type Account = (
  @json(name = "user_id")
  id: int
  @range(1, 10)
  level: int
)
```

An attribute's argument is made of literals: a string, a number, a bool or a tag, or a record or tuple of them (NYX0013 otherwise). A field's description gives each value as text, by its field's name or its position: `@json(name = "user_id")` has the argument `("name", "user_id")`, and `@range(1, 10)` the arguments `("0", "1")` and `("1", "10")`. An attribute on a field of any other record type, such as one written in a signature, is misplaced (NYX0008).

# Notes

- Attributes are compile-time metadata.
- Their meaning is defined by the compiler and language features that consume them.
- Prefer using documented attributes and patterns from the language guides.
