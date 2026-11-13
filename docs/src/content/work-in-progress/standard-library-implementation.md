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
resource named `nanyx/<file>.nyx`, so the published single-file `nanyx` binary carries the library
and reads nothing from disk. A module is in the file named for it, `nanyx/<module>.nyx`, and may have more
files, each starting `@partial module <module>` (see [partial modules](../tour/modules#partial-modules)): the
library's modules are the names its files declare (`StandardLibrary.moduleNames`, read from each file's first
`module` line). `import nanyx/<module>` reads the module's files (`StandardLibrary` in
`Nanyx.Compiler/Builtins.fs`), compiles the module together with the program, and inlines its
definitions into the program the backend sees; importing a module the library does not have is a
compile error that lists the ones it does.

Each standard-library module is checked once per process, the first time a compile imports it, and every later
compile reuses the result (`checkedStandardLibrary` in `Nanyx.Compiler/Library.fs`, keyed by the module and its
source). So the language server and the compiler's tests check the prelude once, not on every compile. A user's own
modules are checked again in each compile, but once in a package build, which hands each file's compile the modules
the files before it checked (`Package.build`).

One module, `nanyx/prelude`, is never imported by name: the compiler imports it, unqualified, into every
other module, so its definitions are what a file sees with no imports at all. A module's own definitions
and its explicit imports take precedence over the prelude's, so a module can define its own `proc`.

The prelude is split by topic across `prelude.nyx` and the files beside it that start `@partial module prelude`:
`prelude-contexts.nyx` (equality, ordering, hashing, joining and text), `prelude-numbers.nyx` (`int` and `float`,
`Add`, `Num` and powers), `prelude-sized-numbers.nyx`, `prelude-conversions.nyx`, `prelude-results.nyx` (results,
options and `proc`), `prelude-lists.nyx` and `prelude-maps.nyx` (maps and sets). `prelude.nyx` keeps the module's doc
comment and what fits no topic: `panic`, `ignore`, booleans and memory blocks.

# What is written

| Module          | What it holds                                                                                     |
| --------------- | ------------------------------------------------------------------------------------------------- |
| `nanyx/prelude` | Imported automatically. `proc`, the result workflow: in `proc { ... }`, `try x` continues with the payload of `#some` or gives back any other tag (the block's type collects them), and `return x` is `#some(x)`. The `Result(a, e)` type (`#some(a) \| #error(e)`) with `Result.map`, `mapError`, `bind`, `defaultValue`, `mapOr`, `orElse`, `isOk`, `isError`. The helpers for option-like unions, plain functions named for the `#some` tag: `mapSome`, `bindSome`, `someOr`, `someOrElse` (what `a ?? b` calls, by its reserved name `__someOrElse`), `isSome`, `isNone`, `someToResult`, `mapSomeOr`, `mapSomeOrElse`, `orElseSome`, `someToList`, `somes`. `int.fromString`, which reads a decimal integer (`#some(int) \| #invalidInt`). `float.fromString`, which reads a decimal with an optional point and exponent (`#some(float) \| #invalidFloat`). `bool.fromString`, which reads `true` or `false` (`#some(bool) \| #invalidBool`). `int.toFloat`, which turns an `int` into a `float`. The `ToString(a)` context every interpolation hole goes through (`"{x}"` is a call to `__interpolate(x)`, which requires it), and `int.toString`, `float.toString`, `bool.toString` and `string.toString`, which write a value as interpolation does. The `FromString(a, e)` context, which an attached `fromString` gives a type, as `int.fromString`, `float.fromString` and `bool.fromString` give `int`, `float` and `bool`. The `List` functions: `list.length`, `head`, `map`, `filter`, `find`, `all`, `any`, `fold`, `foldBack`, `reverse`, `append`, `flatten`, `take`, `skip`, `zip`, `get`, `partition`, `skipWhile`, `unzip` |
| `nanyx/console` | The `Console` context (`write`, `writeError`, `readLine`, `readToEnd`, `flush`), the `IoError` type, the instance the runtime supplies to the top level of a module, and `print`, `println`, `eprint` and `eprintln`, which write any value with a `ToString` through it |
| `nanyx/console.host` | The [console host](./hosts): `run`, which takes the program's `@entry` and runs it with a `Console` built on `HostRuntime` |
| `nanyx/host.runtime` | The `HostRuntime` context (`writeStdout`, `writeStderr`, `readStdinByte`) and the instance the toolchain provides to hosts; anywhere else, requiring it is an error (`NYX0238`) |
| `nanyx/math`    | `abs` and `pow` over `Num(a)`, so on `int` and `float`; `min`, `max` and `clamp` over `Ord(a)`; `sqrt`, `floor`, `ceil`, `round` and `truncate` on `float` |
| `nanyx/result`  | Nothing: kept so that `import nanyx/result` still compiles now that `proc` is in the prelude      |
| `nanyx/text`    | Attached to `String`: `string.length`, `substring`, `startsWith`, `endsWith`, `indexOf`, `lastIndexOf`, `contains`, `count`, `removePrefix`, `removeSuffix`, `uppercase`, `lowercase`, `trim`, `trimStart`, `trimEnd`, `isEmpty`, `isBlank`, `repeat`, `padStart`, `padEnd`, `split`, `join`, `concat`, `replace`, `lines`, `words`; called as `string.trim(s)` or `s \trim` |

`println` comes from `nanyx/console`. A script imports it explicitly; the module that declares `@entry` gets it
implicitly, because its host, `nanyx/console.host`, provides `Console` to the entry point.

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
`export`, and rebuild; the glob picks it up and `import nanyx/<name>` resolves. To split a module, start
`stdlib/nanyx/<name>.nyx` and each other file of it (`stdlib/nanyx/<name>.<part>.nyx`, say) with
`@partial module <name>`. Definitions inside a
module must be written before the definitions that use them (self-recursion is fine). Add an intrinsic
only when a function genuinely cannot be written in Nanyx: give it a name and type in `Intrinsics`, and
a WebAssembly body of the same name in `runtimeHelpers`, and list it, with the helpers it calls, in
`helperDependencies` (the order of that list is the order helpers are emitted in, so add new ones at the end).

Format what you write before committing it: CI fails if any file under `stdlib/` is not formatted (see
[AGENTS.md](https://github.com/nanyx-lang/nanyx/blob/main/AGENTS.md#nanyx-code)).
