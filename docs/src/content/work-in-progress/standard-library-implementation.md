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
program imports its modules like any other package's: `import nanyx/math`, `import nanyx/text`, or
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
source). So the language server and the compiler's tests check the prelude once, not on every compile. A CLI process
compiles once, so it still checks the prelude every time: about 85% of a one-line `nanyx check`. Decided
([#837](https://github.com/nanyx-lang/nanyx/issues/837)): the compiler's build will check the standard library and the
shipped packages and embed the result, so a command checks none of them; a development build whose sources differ
from the embedded hash checks them as now. A user's own
module that a compile imports is kept too, once it checks without errors (`checkedSourceFiles`), with the text of
every file its check read: its own, its other fragments' and those of the modules it imports, all the way down. A
later compile reuses it while each of those files reads the same, through the compile's own reader (the editor's
buffer for a file it has open), and while the package's module table is the same; otherwise it is checked again
([#1270](https://github.com/nanyx-lang/nanyx/issues/1270)). So typing in a small file that imports a large module
does not check the large one again on every change. A package build also hands each file's compile the modules the
files before it checked (`Package.build`).

One module, `nanyx/prelude`, is never imported by name: the compiler imports it, unqualified, into every
other module, so its definitions are what a file sees with no imports at all. A module's own definitions
and its explicit imports take precedence over the prelude's, so a module can define its own `proc`.

The prelude is split by topic across `prelude.nyx` and the files beside it that start `@partial module prelude`:
`prelude-contexts.nyx` (equality, ordering, hashing, joining and text), `prelude-numbers.nyx` (`int` and `float`,
`Add`, `Num`, powers and bitwise functions on ints), `prelude-sized-numbers.nyx`, `prelude-conversions.nyx`, `prelude-results.nyx` (results,
options and `proc`), `prelude-lists.nyx` and `prelude-maps.nyx` (maps and sets). `prelude.nyx` keeps the module's doc
comment and what fits no topic: `crash`, `ignore`, booleans and memory blocks.

# What is written

This table names each module's topics, not its functions. The [Standard Library](../stdlib/standard-library) pages
list every function a module exports.

| Module          | What it holds                                                                                     |
| --------------- | ------------------------------------------------------------------------------------------------- |
| `nanyx/prelude` | Imported automatically. `proc`, the result workflow: in `proc { ... }`, `try x` continues with the payload of `#some` or gives back any other tag (the block's type collects them), and `return x` is `#some(x)`. The `Result(a, e)` type and its functions ([results and options](../stdlib/prelude-results)), and the helpers for option-like unions, named for the `#some` tag, among them `someOrElse`, which `a ?? b` calls by its reserved name `__someOrElse` ([results and options](../stdlib/prelude-results#options)). The contexts for equality, ordering, hashing and joining. `ToString(a)`, which every interpolation hole goes through (`"{x}"` is a call to `__interpolate(x)`, which requires it), and `FromString(a, e)`, with the `toString` and `fromString` attached to the built-in types. `int` and `float`, the number contexts (`Add`, `Num`) and powers, the sized number types, and conversions between them. Lists ([lists](../stdlib/prelude-lists)), maps and sets ([maps and sets](../stdlib/prelude-maps)) |
| `nanyx/host.runtime` | The `HostRuntime` context, with no members, which the toolchain provides to hosts; anywhere else, requiring it is an error (`NYX0238`) |
| `nanyx/host.memory` | A host's access to linear memory, for the foreign functions it calls: blocks, reads and writes, strings' addresses ([host.memory](../stdlib/host-memory)) |
| `nanyx/math`    | Arithmetic over `Num(a)`, so on `int` and `float`; comparisons over `Ord(a)` (`min`, `max`, `clamp` and the like); square roots, exponentials, logarithms and rounding on `float`; the constants `pi` and `e` ([math](../stdlib/math)) |
| `nanyx/text`    | Functions attached to `String`: measuring, searching, slicing, case, trimming, padding, splitting and joining, replacing, and characters; called as `string.trim(s)` or `s \trim` ([text](../stdlib/text)) |

`println`, `Console` and `Env` aren't in the standard library: they are in the console host's interface package,
`nanyx.console.core`, which the compiler ships beside it, and the console host's prelude brings them into every module
of a program (see [Built-ins as packages](./built-ins-as-packages)).

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
the file's name (`host.runtime.nyx` is on `host-runtime.md`), so the prelude has a page for each of its files. A page's
reference, between the two marker comments on it, is generated; the rest of the page, its introduction and longer
examples, is written by hand.

The reference lists what the file exports, in the order it declares it: each type, context and def, attached ones
included, under a heading of its name, with its signature as hover writes it and its doc comment; a context's members
with theirs; and, on the page of the file named for the module, the module's doc comment first. Names the library
reserves, those that start with `__`, are left out. It is read from the compile of each module the compiler embeds,
and of each public module of the packages it ships beside it (`ShippedPackages`), not from a parse of its own (`StandardLibraryDocs` in `Nanyx.Compiler/StandardLibraryDocs.fs`), and a link in a doc
comment, `` [`name`] ``, leads to the name's entry, on its page or another.

A shipped package's public module, such as `nanyx.console.core`'s `console` and `env`, has a page in the same section,
named for its file as a library file's is, and its reference is generated in the same way from the package's files in
`packages/`. A host's modules (`nanyx.console`'s `console.host` and `console.prelude`) have none: a program doesn't
import them.

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
onto helper functions it emits with the module (`runtimeHelpers` in `Nanyx.Compiler.Wasm/WasmRuntime.fs`). The few that are one\nor two instructions, the wrapping arithmetic and the bitwise operations on ints, are emitted inline instead\n(`wrappingOperations` and `bitwiseOperations` in `Nanyx.Compiler.Wasm/CoreIrEmit.fs`).
Their names all start with `__nanyx_`.

# Adding to it

To add a module: create `stdlib/nanyx/<name>.nyx` declaring `module <name>`, mark what it offers with
`export`, and rebuild; the glob picks it up and `import nanyx/<name>` resolves. To split a module, start
`stdlib/nanyx/<name>.nyx` and each other file of it (`stdlib/nanyx/<name>.<part>.nyx`, say) with
`@partial module <name>`. Definitions inside a
module must be written before the definitions that use them (self-recursion is fine). Add an intrinsic
only when a function genuinely cannot be written in Nanyx, or not without copying far more than it needs
to: `__nanyx_string_join` joins a list of strings with one allocation (its body is in
`Nanyx.Compiler.Wasm/StringJoin.fs`), where `++` would copy the result so far for every piece, so a
string built from many pieces is built as a list of them and joined once. Give it a name and type in `Intrinsics`, and
a WebAssembly body of the same name in `runtimeHelpers`, and list it, with the helpers it calls, in
`helperDependencies` (the order of that list is the order helpers are emitted in, so add new ones at the end).

Format what you write before committing it: CI fails if any file under `stdlib/` is not formatted (see
[AGENTS.md](https://github.com/nanyx-lang/nanyx/blob/main/AGENTS.md#nanyx-code)).
