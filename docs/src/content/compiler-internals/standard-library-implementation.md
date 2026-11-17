---
title: "Standard library implementation"
description: "How the standard library is written, shipped inside the compiler, and extended"
order: 3
---

This page describes how the standard library is built today. What it offers is described in the
[Standard Library](../stdlib/standard-library) section, whose references are generated from the library (see
[its pages on the docs site](#its-pages-on-the-docs-site)). What is still open about it is on
[Compilation](../work-in-progress/compilation), the workstream it is part of.

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

The compiler's build checks every standard-library module once and writes the result beside the compiler, so a
compile checks none of them ([#837](https://github.com/nanyx-lang/nanyx/issues/837)). `Nanyx.Compiler.CheckedLibrary`,
a small program, calls `Compiler.writeCheckedLibrary` when it is built, which checks each module as an import of it
does and writes the checked modules to `checked-library.bin` (`BinaryData` writes the F# data, sharing what the modules
share; `CheckedLibrary` holds the format). It writes the file after `CoreBuild`, inside its `Build` target, not after
it: in a parallel build, a project that asks for `Build` while the writer is building gets the result as soon as
`Build` is done, before the targets that run after it, so the file has to be there by then
([#1648](https://github.com/nanyx-lang/nanyx/issues/1648)). `compiler/CheckedLibrary.targets`, which the CLI and the
test projects import, copies the file beside them and into the published `nanyx`. It builds the writer for the machine
that builds, without the program's runtime or the publish properties that would pick one, and makes it a reference
of the program while restoring, so that building or publishing one project alone restores it
([#1656](https://github.com/nanyx-lang/nanyx/issues/1656)). Its syntax nodes and symbols have ids from
`CheckedLibrary.firstId` (2⁵⁶) on, which no process reaches, so they never meet a process's own. A process reads the
file the first time it imports a library module, about 0.2 s, and a module read from it is reported as reused,
`nanyx/prelude (cached)` under `--timings`. The file is used only where it is what checking would give: by a compiler
whose types are written as the writer's were (`BinaryData.schema`), while every file of the library hashes as it did
then, and for a module only with the source it was checked from. Anything else, as a development build whose library
changed after the file was written, is checked as before.

Each standard-library module the file does not have is checked once per process, the first time a compile imports
it, and every later compile reuses the result (`checkedStandardLibrary` in `Nanyx.Compiler/Library.fs`, keyed by the
module and its source).

The file has the modules of the shipped packages too: the console host's package, `nanyx.console`, and the packages it
depends on, `nanyx.console.core`, `nanyx.fs` and `nanyx.http.core` ([#1637](https://github.com/nanyx-lang/nanyx/issues/1637)). The writer checks each as
a loose script's import of it does and writes what `checkedSourceFiles` keeps of it: the module, the modules it
imports, and the text of every file its check read. Their files are beside the compiler (`ShippedPackages.directory`),
so the file names a path there relative to that directory, and a path read back is in the reading compiler's
(`BinaryData.write` and `read` take a function over each string). A compile takes such a module from the file when the
file is in its build's host package or one that package depends on, so it resolves its imports as the writer's check
did, and while every file the check read reads the same through the compile's reader; else it checks it as a user's
module. Under `--timings`, `nanyx.console/src/console.prelude.nyx (cached)`. With them, a one-line `nanyx check` checks
nothing but the file itself: 0.86 s against 1.00 s, and 0.91 s against 1.22 s for `dbg("hello")`, which imports the host
too (Release build, medians of 9). The file grew from 1.8 MB to 2.2 MB.

A user's own
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
`Add`, `Sub`, `Num`, powers and bitwise functions on ints), `prelude-sized-numbers.nyx`, `prelude-conversions.nyx`, `prelude-results.nyx` (results,
options and `proc`), `prelude-lists.nyx`, `prelude-sequences.nyx` (lazy sequences and ranges), `prelude-maps.nyx`
(maps and sets) and `prelude-arrays.nyx` (reading an array, and what collection literals are built with). `prelude.nyx` keeps the module's doc
comment and what fits no topic: `crash`, `ignore`, booleans and memory blocks.

# What is written

What each module holds is in the [Standard Library](../stdlib/standard-library) section, generated from the library's
doc comments. `println`, `Console` and `Env` aren't in the standard library: they are in the console host's interface
package, `nanyx.console.core`, which the compiler ships beside it, and the console host's prelude brings them into every
module of a program (see [Hosts](../advanced/hosts#the-console-host)).

# Its pages on the docs site

The library is the source of truth for its pages in the [Standard Library](../stdlib/standard-library) section: each
file of the library that exports a name has a page, `docs/src/content/stdlib/<file>.md` with a `-` for each `.` in
the file's name (`host.runtime.nyx` is on `host-runtime.md`), so the prelude has a page for each of its files. A page's
reference, between the two marker comments on it, is generated; the rest of the page, its introduction and longer
examples, is written by hand.

The reference lists what the file exports, in the order it declares it: each type, context and def, attached ones
included, under a heading of its name, with its signature as hover writes it and its doc comment; a context's members
with theirs; and, on the page of the file named for the module, the module's doc comment first. Names the library
reserves, those that start with `__`, are left out. It is read from the compile of each module the compiler embeds, as an import of it reads it, so no module is checked
again (`Compiler.compileStandardLibraryModuleReporting`),
and of each public module of the packages it ships beside it (`ShippedPackages`), not from a parse of its own (`StandardLibraryDocs` in `Nanyx.Compiler/StandardLibraryDocs.fs`), and a link in a doc
comment, `` [`name`] ``, leads to the name's entry, on its page or another.

A shipped package's public module, such as `nanyx.console.core`'s `console` and `env`, or `nanyx.fs`'s `file`, has a page in the same section,
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
module as a whole. The open questions about the generated references are on
[Compilation](../work-in-progress/compilation#open-questions).

# Intrinsics

The library is written over a handful of **compiler intrinsics**: operations that cannot be expressed in
Nanyx itself, such as writing to standard output or reading a byte of a string. They are listed with
their types in one place, `Intrinsics` in `Nanyx.Compiler/Builtins.fs`, are only in scope while a
standard-library module is being checked (user code cannot call them), and are lowered by the backend
onto helper functions it emits with the module (`runtimeHelpers` in `Nanyx.Compiler.Wasm/WasmRuntime.fs`). The few that are one
or two instructions, the wrapping arithmetic and the bitwise operations on ints, are emitted inline instead
(`wrappingOperations` and `bitwiseOperations` in `Nanyx.Compiler.Wasm/CoreIrEmit.fs`).
Their names all start with `__nanyx_`.

# Adding to it

To add a module: create `stdlib/nanyx/<name>.nyx` declaring `module <name>`, mark what it offers with
`export`, and rebuild; the glob picks it up and `import nanyx/<name>` resolves. To split a module, start
`stdlib/nanyx/<name>.nyx` and each other file of it (`stdlib/nanyx/<name>.<part>.nyx`, say) with
`@partial module <name>`. Add an intrinsic
only when a function genuinely cannot be written in Nanyx, or not without copying far more than it needs
to: `__nanyx_string_join` joins a list of strings with one allocation (its body is in
`Nanyx.Compiler.Wasm/StringJoin.fs`), where `++` would copy the result so far for every piece, so a
string built from many pieces is built as a list of them and joined once. Give it a name and type in `Intrinsics`, and
a WebAssembly body of the same name in `runtimeHelpers`, and list it, with the helpers it calls, in
`helperDependencies` (the order of that list is the order helpers are emitted in, so add new ones at the end).

Format what you write before committing it: CI fails if any file under `stdlib/` is not formatted (see
[AGENTS.md](https://github.com/nanyx-lang/nanyx/blob/main/AGENTS.md#nanyx-code)).
