---
title: "Standard library implementation"
description: "How the standard library is written, shipped inside the compiler, and extended"
order: 26
---

> **Status: in progress.** This page describes how the standard library is built today. What it offers is
> described in the [Standard Library](../stdlib/standard-library) section, most of which is not written yet. It is
> part of the [compilation](./compilation) workstream.

# An ordinary package

The standard library is an ordinary package named `nanyx`, written in Nanyx, with one module per file
under [`stdlib/nanyx/`](https://github.com/nanyx-lang/nanyx/tree/main/stdlib/nanyx) at the repository root. A
program imports its modules like any other package's: `import nanyx/console`, `import nanyx/text`, or
`import nanyx/text as t` for qualified access.

It ships inside the compiler. `NanyxCompiler.fsproj` embeds every `stdlib/nanyx/*.nyx` file as a
resource named `nanyx/<module>.nyx`, so the published single-file `nanyx` binary carries the library
and reads nothing from disk. `import nanyx/<module>` reads that resource (`StandardLibrary` in
`Nanyx.Compiler/Builtins.fs`), compiles the module together with the program, and inlines its
definitions into the program the backend sees; importing a module the library does not have is a
compile error that lists the ones it does.

One module, `nanyx/prelude`, is never imported by name: the compiler imports it, unqualified, into every
other module, so its definitions are what a file sees with no imports at all. A module's own definitions
and its explicit imports take precedence over the prelude's, so a module can define its own `proc`.

# What is written

| Module          | What it holds                                                                                     |
| --------------- | ------------------------------------------------------------------------------------------------- |
| `nanyx/prelude` | Imported automatically. `proc`, the result workflow: in `proc { ... }`, `try x` continues with the payload of `#some` or gives back any other tag (the block's type collects them), and `return x` is `#some(x)`. The `Result(a, e)` type (`#some(a) \| #error(e)`) with `Result.map`, `mapError`, `bind`, `defaultValue`, `mapOr`, `toOption`, `orElse`, `isOk`, `isError`. The helpers for option-like unions, attached to the name `Option`: `Option.map`, `bind`, `defaultValue`, `isSome`, `isNone`, `toResult`, `mapOr`, `toList`, `values`. `Int.parse`, which reads a decimal integer (`#some(int) \| #invalidInt`). `Bool.parse`, which reads `true` or `false` (`#some(bool) \| #invalidBool`). `Int.toFloat`, which turns an `int` into a `float`. The `ToString(a)` context every interpolation hole goes through (`"{x}"` is a call to `__interpolate(x)`, which requires it), and `Int.toString`, `Float.toString`, `Bool.toString` and `String.toString`, which write a value as interpolation does. The `List` functions: `List.length`, `head`, `map`, `filter`, `find`, `all`, `any`, `fold`, `foldBack`, `reverse`, `append`, `flatten`, `take`, `skip`, `zip` |
| `nanyx/console` | The `Console` context (`print`, `println`, `eprint`, `eprintln`, `readLine`, `readToEnd`, `flush`), the `IoError` type, and the instance the runtime supplies to the entry point |
| `nanyx/math`    | `abs`, `min`, `max`, `clamp` and `pow` on `int` (the docs make them generic over `Num(a)`, [#186](https://github.com/nanyx-lang/nanyx/issues/186)); `sqrt`, `floor`, `ceil`, `round` and `truncate` on `float` |
| `nanyx/result`  | Nothing: kept so that `import nanyx/result` still compiles now that `proc` is in the prelude      |
| `nanyx/text`    | Attached to `String`: `String.length`, `substring`, `startsWith`, `endsWith`, `indexOf`, `lastIndexOf`, `contains`, `count`, `removePrefix`, `removeSuffix`, `uppercase`, `lowercase`, `trim`, `trimStart`, `trimEnd`, `isEmpty`, `isBlank`, `repeat`, `padStart`, `padEnd`, `split`, `join`, `concat`, `replace`, `lines`, `words`; called as `String.trim(s)` or `s \trim` |

`println` comes from `nanyx/console`. A script imports it explicitly; the module that declares `@entry` gets it
implicitly, because the runtime calls the entry point inside its `Console` context.

Strings are UTF-8 and this first slice of `nanyx/text` works byte by byte: `length` counts bytes,
indices and widths are byte offsets, `uppercase`/`lowercase` only change ASCII letters and the trimming
functions and `words` only treat ASCII whitespace as whitespace. `indexOf` and `lastIndexOf` return
`#some(index)` or `#notFound`, as the language docs specify; `split`, `replace`, `substring` and `join`
take their arguments positionally, where the docs name them. `++` (concatenation) and `==`
on strings are language operators, lowered by the backend. (The language docs call this module
`string`; it is imported as `nanyx/text`.)

# Intrinsics

The library is written over a handful of **compiler intrinsics**: operations that cannot be expressed in
Nanyx itself, such as writing to standard output or reading a byte of a string. They are listed with
their types in one place, `Intrinsics` in `Nanyx.Compiler/Builtins.fs`, are only in scope while a
standard-library module is being checked (user code cannot call them), and are lowered by the backend
onto helper functions it emits with the module (`runtimeHelpers` in `Nanyx.Compiler.Wasm/WasmRuntime.fs`).
Their names all start with `__nanyx_`.

# Adding to it

To add a module: create `stdlib/nanyx/<name>.nyx` declaring `module <name>`, mark what it offers with
`export`, and rebuild; the glob picks it up and `import nanyx/<name>` resolves. Definitions inside a
module must be written before the definitions that use them (self-recursion is fine). Add an intrinsic
only when a function genuinely cannot be written in Nanyx: give it a name and type in `Intrinsics`, and
a WebAssembly body of the same name in `runtimeHelpers`, and list it, with the helpers it calls, in
`helperDependencies` (the order of that list is the order helpers are emitted in, so add new ones at the end).

Format what you write before committing it: CI fails if any file under `stdlib/` is not formatted (see
[AGENTS.md](https://github.com/nanyx-lang/nanyx/blob/main/AGENTS.md#nanyx-code)).
