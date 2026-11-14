---
title: "Standard library implementation"
description: "How the standard library is written, shipped inside the compiler, and extended"
order: 26
---

> **Status: in progress.** This page describes how the standard library is built today. What it offers is
> described in the [Standard Library](../stdlib/standard-library) section, whose references are generated from the
> library (see [its pages on the docs site](#its-pages-on-the-docs-site)). It is part of the
> [compilation](./compilation) workstream.

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

This table names each module's topics, not its functions. The [Standard Library](../stdlib/standard-library) pages
list every function a module exports.

| Module          | What it holds                                                                                     |
| --------------- | ------------------------------------------------------------------------------------------------- |
| `nanyx/prelude` | Imported automatically. `proc`, the result workflow: in `proc { ... }`, `try x` continues with the payload of `#some` or gives back any other tag (the block's type collects them), and `return x` is `#some(x)`. The `Result(a, e)` type and its functions ([results and options](../stdlib/prelude-results)), and the helpers for option-like unions, named for the `#some` tag, among them `someOrElse`, which `a ?? b` calls by its reserved name `__someOrElse` ([results and options](../stdlib/prelude-results#options)). The contexts for equality, ordering, hashing and joining. `ToString(a)`, which every interpolation hole goes through (`"{x}"` is a call to `__interpolate(x)`, which requires it), and `FromString(a, e)`, with the `toString` and `fromString` attached to the built-in types. `int` and `float`, the number contexts (`Add`, `Num`) and powers, the sized number types, and conversions between them. Lists ([lists](../stdlib/prelude-lists)), maps and sets ([maps and sets](../stdlib/prelude-maps)) |
| `nanyx/console` | The `Console` context (`write`, `writeError`, `readLine`, `readToEnd`, `flush`), the `IoError` type, and `print`, `println`, `eprint` and `eprintln`, which write any value with a `ToString` through it ([console](../stdlib/console)) |
| `nanyx/console.host` | The [console host](./hosts): `run`, which takes the program's `@entry` and runs it with a `Console` built on `HostRuntime` |
| `nanyx/host.runtime` | The `HostRuntime` context (`writeStdout`, `writeStderr`, `readStdinByte`) and the instance the toolchain provides to hosts; anywhere else, requiring it is an error (`NYX0238`) |
| `nanyx/math`    | Arithmetic over `Num(a)`, so on `int` and `float`; comparisons over `Ord(a)` (`min`, `max`, `clamp` and the like); square roots, exponentials, logarithms and rounding on `float`; the constants `pi` and `e` ([math](../stdlib/math)) |
| `nanyx/result`  | Nothing: kept so that `import nanyx/result` still compiles now that `proc` is in the prelude      |
| `nanyx/text`    | Functions attached to `String`: measuring, searching, slicing, case, trimming, padding, splitting and joining, replacing, and characters; called as `string.trim(s)` or `s \trim` ([text](../stdlib/text)) |

`println` comes from `nanyx/console`. A script imports it explicitly; the module that declares `@entry` gets it
implicitly, because its host, `nanyx/console.host`, provides `Console` to the entry point.

Strings are UTF-8 and this first slice of `nanyx/text` works byte by byte: `length` counts bytes,
indices and widths are byte offsets, `uppercase`/`lowercase` only change ASCII letters and the trimming
functions and `words` only treat ASCII whitespace as whitespace. `indexOf` and `lastIndexOf` return
`#some(index)` or `#notFound`, as the language docs specify. `split`, `substring`, `replace`, `padStart` and
`padEnd` take their arguments after the string by name, as in `s \split(on = ",")`, and `join` takes its separator by
position ([Named parameters](../stdlib/standard-library#named-parameters)). `++` (concatenation) and `==`
on strings are language operators, lowered by the backend.

# Its pages on the docs site

The library is the source of truth for its pages in the [Standard Library](../stdlib/standard-library) section: each
file of the library that exports a name has a page, `docs/src/content/stdlib/<file>.md` with a `-` for each `.` in
the file's name (`console.host.nyx` is on `console-host.md`), so the prelude has a page for each of its files. A page's
reference, between the two marker comments on it, is generated; the rest of the page, its introduction and longer
examples, is written by hand.

The reference lists what the file exports, in the order it declares it: each type, context and def, attached ones
included, under a heading of its name, with its signature as hover writes it and its doc comment; a context's members
with theirs; and, on the page of the file named for the module, the module's doc comment first. Names the library
reserves, those that start with `__`, are left out. It is read from the compile of each module the compiler embeds,
not from a parse of its own (`StandardLibraryDocs` in `Nanyx.Compiler/StandardLibraryDocs.fs`), and a link in a doc
comment, `` [`name`] ``, leads to the name's entry, on its page or another.

After changing a doc comment, or what the library exports, write the references again from `compiler/`:

```bash
dotnet run --project Nanyx.Compiler.Cli/NanyxCompiler.Cli.csproj -- stdlib-docs ../docs/src/content/stdlib
```

`--check` writes nothing and lists the pages that are out of date. A test in the WebAssembly tests,
`StandardLibraryReferenceTests.fs`, fails, showing the difference, when a page's reference is not what the library
gives, when a file that exports names has no page, and when a page with a reference has no file. The `nanyx` blocks
on these pages outside the reference are compiled and run, as the tour's are
([Tour examples](./compilation#tour-examples)).

So explain a function in its doc comment, where hover shows it too, and keep a page's prose to what is about the
module as a whole. Open questions:

- A named parameter is written in a signature in the order of its name, not the order it is declared in, as hover
  writes it: `string.substring: (string, length: int, start: int)`.
- `proc`'s signature is a long record of its keyword members, which says little to a reader.

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
