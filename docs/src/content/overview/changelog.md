---
title: "Changelog"
description: "What changed in each release of the Nanyx compiler"
order: 2
---

Each release of the compiler, newest first: what it is about, and the changes you will notice. The binaries for every
release are on the [GitHub releases page](https://github.com/nanyx-lang/nanyx/releases?q=compiler); see
[Installation](../getting-started/installation) for how to get them. The VS Code extension is released on its own.

# 0.28.0

*14 November 2026*

Programs now run under a host: the console host supplies `Console` and the new `Env`, and a package can choose its
host. The backend gained an inliner and a smaller, faster heap.

**Language**

- Workflow builders are contexts: `proc { ... }` is `proc({ ... })` under `use Proc(...)`
  ([Workflows](../advanced/workflows)).
- Protected types are declared with `protected` after the `=`, and a `$` before a type's name is a syntax error
  ([Nominal and protected types](../advanced/nominal-and-protected-types)).
- `;` separates statements on one line.
- Functions that require contexts compose, and a point-free value carries the contexts its parts need
  ([Contexts](../advanced/contexts)).
- A record that names a field twice is an error.

**Standard library**

- `Env` gives a program its arguments, environment variables and exit code ([Env](../stdlib/env)).
- `Console` only comes from the host now; `nanyx/console` has no ambient instance ([Console](../stdlib/console)).
- `string.take`, `skip`, `codePoints` and `fromCodePoint`, and `list.init` and `list.replicate`
  ([Text](../stdlib/text), [Lists](../stdlib/prelude-lists)).
- Maps and sets have a derived `toString`.
- The standard library's pages on this site are generated from its doc comments
  ([Standard library](../stdlib/standard-library)).

**Tools**

- A package picks its host with `[host]` in its manifest or `--host` on the command line ([Hosts](../advanced/hosts)).
- `nanyx --version` prints the release version.
- Scripts run their statements under the console host.
- `--timings` works with `nanyx run` in development mode.
- In the editor, double-clicking an inlay hint inserts the type annotation, nominal types are shown in italics, and
  the outline lists local defs ([VS Code extension](../standard-tools/vs-code-extension)).

**Performance**

- Calls are inlined in the core IR, and closures and matches it makes known are simplified away
  ([Inlining](../compiler-internals/inlining)).
- The allocator joins and splits freed blocks and gives back the top of the heap.
- A record held once is updated in place by `with`.
- Only the functions a program reaches are emitted, and a top-level value is built on its first read.
- Strings are joined with one allocation, and bytes are copied with `memory.copy`.

# 0.27.0

*13 November 2026*

Open record types, a reference page for every compiler error, and a live `--timings` tree.

**Language**

- Open record types are written with `...` and `...r` ([Records](../tour/records)).
- A type's name starts with a capital letter; lowercase names in types are reserved
  ([Naming conventions](../tour/naming-conventions)).
- A def that writes its contexts can be defined point-free.

**Tools**

- The [compiler errors page](../standard-tools/errors) explains each diagnostic, and every diagnostic's code links to
  its section.
- The command line shows each diagnostic's line of source with the span underlined.
- `--timings` shows a live tree of module checks, with cached imports ([Command line](../standard-tools/command-line)).
- A standard-library file open in the editor is checked as the library module it is.

**Performance**

- Closures that capture nothing are static data.
- A function that requires contexts is specialised on the members known where it is called.

**Fixes**

- Hashing a structure goes through its parts' own `Hash`, and an open union's payload compares with its type's `==`.
- A call to a context member generic in its own type variables is reported instead of trapping.

# 0.26.0

*13 November 2026*

Mutable cells take shape, imports get finer control, and the type checker gets much faster on large inputs.

**Language**

- Cells are declared with `mut x = e`, passed to functions with `mut`, and a memory block names its instance:
  `memory { m -> ... }` ([Memory](../advanced/memory)).
- Selective imports, `as (a, b)` and `as m & (a, b)`, and re-exports, `export m (a, b)` ([Modules](../tour/modules)).
- `scoped context`, for contexts that must stay inside the call that supplies them ([Contexts](../advanced/contexts)).
- `[]` says a function requires no contexts; `use C(T)(members)` loads an instance written out in place.
- A lambda can annotate its result, `{ (x: int): int -> x * 2 }`, and `f()(args)` calls the result of a call
  ([Lambdas and higher-order functions](../tour/lambdas-and-higher-order-functions)).
- Optional fields and types (`name?: T`, `T?`) are gone.
- A local def that calls itself needs `rec`.
- A `for` loop whose body uses `try` or `return` runs through the builder's `for` member
  ([Workflows](../advanced/workflows)).

**Standard library**

- `map.union` and `map.unionWith`, `string.splitOnce`, and `fromString` on the sized number types
  ([Maps](../stdlib/prelude-maps), [Sized numbers](../stdlib/prelude-sized-numbers)).
- `int.fromString` gives back `#invalidInt` for a number out of range.

**Tools**

- The [info view](../standard-tools/vs-code-extension) is organised by scope and refreshes when a compile finishes.

**Performance**

- The type checker solves only the constraints added since its last solve, and checks a chain of 10,000 operators
  in linear time.
- Small integer types are stored at their own width in fields, list cells and payloads.

# 0.25.0

*13 November 2026*

Sized numbers, `while` loops and partial modules, with the editor showing where each context comes from.

**Language**

- The sized integer types `I8` to `U64`, and `F32`, with conversions between the number types and wrapping
  arithmetic ([Sized numbers](../advanced/sized-numbers)).
- A number literal takes the type the code around it expects.
- `while` loops ([Loops](../tour/loops)).
- Record spread: `(...a, f = v, ...b)` ([Records](../tour/records)).
- `??` unwraps an option-like value with a lazy fallback ([Option-like types](../advanced/option-like-types)).
- Operators are a closed list, now with `>>` and `<<` for composition; a binary operator needs a space on each side,
  and a pipe binds tighter than every operator ([Operators](../tour/operators)).
- `@partial module` splits a module across files, and a package imports its modules by their declared names
  ([Modules](../tour/modules)).
- Top-level values that need each other to be built are an error.

**Standard library**

- `list.distinctBy`, `minBy`, `maxBy` and `findIndex` ([Lists](../stdlib/prelude-lists)).
- The option helpers are named for the `#some` tag: `mapOption` is now `mapSome`.

**Tools**

- The info view shows where the call at the cursor finds each context, and the editor draws lines from a provider of
  contexts to the calls it serves ([VS Code extension](../standard-tools/vs-code-extension)).
- `nanyx` shows a stopwatch on stderr while it compiles.
- The editor compiles a changed document once you pause typing, and a package build checks each module once.

# 0.24.0

*12 November 2026*

Contexts become part of function types, `int` is a true 64-bit integer, and the editor gains an info view.

**Language**

- Function types carry their contexts, joined with `&`; lambdas get the contexts they use, and a lambda can't use a
  context outside the scope that supplies it ([Contexts](../advanced/contexts)).
- The capitalised built-in names, `I64`, `F64` and `Boolean`, are the real types; `int`, `float` and `bool` are their
  aliases ([Types](../tour/types)).
- `int` compiles to a 64-bit integer.
- Memory blocks with `mut` cells and `:=` ([Memory](../advanced/memory)).
- A tag union alias can refer to itself, and `with` takes a bracketed block of updates.
- Type parameters are one letter long.

**Standard library**

- `list.filterMap`, `ignore`, `math.minMax`, `exp`, `ln`, `sign`, `pi` and `e` ([Math](../stdlib/math)).
- Maps and sets compare by their contents with `==`, and `dbg` prints any value, maps and sets included.

**Tools**

- The info view: a panel showing what the compiler knows at the cursor, with doc comments
  ([VS Code extension](../standard-tools/vs-code-extension)).
- Quick fixes add a missing context, fill in a match's missing arms, and suggest the closest name for an unknown one.
- The `unusedResults` and `unannotatedExports` linter rules, and a warning when a definition hides a prelude member
  ([Official linter](../standard-tools/official-linter)).
- Hover shows a definition's contexts.

# 0.23.0

*11 November 2026*

Doc comments, string escapes and digit separators.

**Language**

- `--|` starts a doc comment, and `---` a block comment ([Comments](../getting-started/basics#comments)).
- Backslash escapes in double-quoted strings, and digit separators such as `1_000_000`.
- `panic`, and `use ... in` with an indented block.
- A trailing lambda after several arguments is passed as the last one.
- A top-level name defined twice in a module is an error.

**Standard library**

- `string.chars` and `string.reverse`, by code point ([Text](../stdlib/text)).
- `isEmpty`, `fold`, `map` and `filter` on maps and sets, with `map.update` and `set.isSubset`
  ([Maps](../stdlib/prelude-maps)).
- Any value with a `ToString` can be printed.

**Tools**

- The formatter sorts imports ([Formatter](../standard-tools/formatter)).
- The `unreachableCode` linter rule.

# 0.22.0

*11 November 2026*

The well-known contexts arrive: operators go through `Add`, `Num`, `Eq`, `Ord` and `Hash`, and maps and sets join the
standard library.

**Language**

- `+` and `-` go through `Add`, `*`, `/` and `%` through `Num`, comparisons through `Ord`, and `**` is a generic
  power ([Operators](../tour/operators), [Contexts](../advanced/contexts)).
- `ToString`, `Eq` and `Hash` are derived for tags, tuples, records and lists from their parts.
- The `map(k, v)` type and map literals, and `set(a)` ([Collections](../tour/collections)).
- `do` blocks start a block anywhere an expression goes.
- A pipe chain can continue on more indented lines, and `\match` pipes into a match ([Piping](../tour/piping)).
- `undefined` is a bottom type, assignable to every type.

**Standard library**

- The `Map` and `Set` modules, `List.groupBy`, `sort`, `skipWhile`, `partition`, `unzip`, `get`, and the search and
  slicing helpers ([Maps](../stdlib/prelude-maps), [Lists](../stdlib/prelude-lists)).
- `parse` is renamed `fromString`, with a `FromString` context; `Float.fromString` is new.
- `math.abs`, `min`, `max` and `clamp` are generic.

**Tools**

- The `unusedPrivateDefinitions` linter rule ([Official linter](../standard-tools/official-linter)).

**Fixes**

- Floats print with the fewest digits that read back.
- A top-level value is evaluated once.

# 0.21.0

*10 November 2026*

Structural equality, inferred parameterised contexts, and stack-safe list functions.

**Language**

- `==` compares records, tuples, lists and tag payloads by structure, and requires `Eq`.
- An unannotated def infers the parameterised contexts it needs ([Contexts](../advanced/contexts)).
- `use <expr>` loads an instance; `use C(T) = ...` is gone.
- A record pattern names every field or ends in `...`, and a closed record rejects extra or missing fields
  ([Records](../tour/records)).
- A `for` loop walks several iterables in step, and ranges can be unbounded ([Loops](../tour/loops)).
- `_` in a type is inferred, and a name in backticks can be any text.

**Standard library**

- `Option.toList`, `Option.values`, `Result.mapOr`, `Result.toOption`, `Result.orElse`, `Int.toFloat` and
  `math.truncate`.
- `List.map`, `List.filter` and the other walks are stack-safe.

**Tools**

- `nanyx run --check-memory` checks reference counts ([Command line](../standard-tools/command-line)).
- `dbg` prints bools as `true` and `false`, and functions.

# 0.20.0

*10 November 2026*

Types are shown with `--ascii` instead of `--latin-type-vars`, and a union's lone rest is hidden. A lambda with
several parameters is rejected where its input isn't a record.

# 0.19.0

*9 November 2026*

Memory is reference counted, and `nanyx new` starts a package.

**Language**

- Every heap object is reference counted, with cells reused in place
  ([Reference counting](../foundations/reference-counting)).
- And-patterns, `n & { > 100 }`, any predicate lambda as a guard, and patterns as lambda parameters
  ([Pattern matching](../tour/pattern-matching)).
- The except operator handles some cases of a union.
- `def x, y = value` without brackets.

**Standard library**

- `toString` on `int`, `float`, `bool` and `string`; `Bool.parse`; `List.distinct`, `sortBy` and `unfold`;
  `math.sqrt`, `floor`, `ceil` and `round` ([Math](../stdlib/math)).

**Tools**

- `nanyx new`, and `nanyx check` on a package ([Command line](../standard-tools/command-line)).
- The manifest's extension is `.nyxp` ([Files and projects](../getting-started/files-and-projects)).
- Unused imports are reported, with a fix that removes them, and a quick fix adds a missing standard-library import.
- Types are shown in Unicode by default.

# 0.18.0

*9 November 2026*

Errors no longer stop a debug build: the compiler reports every error and turns the broken code into a trap. Code is
now compiled through a monomorphising core IR.

**Language**

- `???` is the placeholder for code not written yet ([Error handling](../tour/error-handling)).
- Any pattern on the left of a def, unit patterns such as `#some()`, and bracketed lambda parameters.
- `#:` directives let a script say what a manifest would.
- Tail calls use no stack ([Tail calls](../compiler-internals/tail-calls)).
- Strings and lists have a built-in `length` property.

**Tools**

- `run`, `build` and `wasm` make debug builds that defer errors to run time; `--release` refuses them
  ([Deferred errors](../tour/error-handling#deferred-errors)).
- Every type error in a module is reported, not just the first.
- Analyzers are now linters, off unless something turns them on ([Linters](../advanced/linters)).

**Performance**

- An allocator that can free memory replaces the bump allocator.
- Match exhaustiveness is checked by usefulness over rows of patterns.

# 0.17.0

*8 November 2026*

A fuller language server: go to definition across types, context members and the standard library, find references,
document outline and quick fixes. A package builds and runs from its manifest, and unused locals are reported.

# 0.16.0

*7 November 2026*

The formatter breaks lines that don't fit the line width, reads its settings from the manifest, and formats documents
on save in VS Code ([Formatter](../standard-tools/formatter)).

# 0.15.0

*7 November 2026*

`nanyx format`, with `--check` and `--write`, completion of names, record fields, module exports and import paths in
the editor, and the first analyzers.

# 0.14.0

*7 November 2026*

A record type is inferred from the fields a function reads.

# 0.13.1

*7 November 2026*

Fields of a record bound by a match pattern can be read.

# 0.13.0

*7 November 2026*

`typeof x` as a type, a piped value goes in front of the written arguments, and signatures can mix positional and
named parameters.

# 0.12.0

*6 November 2026*

The automatically imported prelude, with the `List` functions, `Int.parse` and more `Console` functions, and
structured diagnostics with codes and spans ([Prelude](../stdlib/prelude)).

# 0.11.0

*6 November 2026*

A name exported by two imports is ambiguous, pipes resolve left to right, and only the imported definitions a module
reaches are compiled.

# 0.10.0

*6 November 2026*

Attached definitions on built-in types, and module blocks that keep what they don't export private
([Attached definitions](../advanced/attached-definitions)).

# 0.9.0

*6 November 2026*

`indexOf` and more string functions in `nanyx/text`.

# 0.8.0

*6 November 2026*

The `proc` result workflow, which collects the failure of each `try` ([Workflows](../advanced/workflows)).

# 0.7.0

*6 November 2026*

`try x` is a workflow bind, record fields and arguments can go one per line, and type aliases keep their names in
displayed types.

# 0.6.2

*6 November 2026*

You choose where the editor shows top-level defs' types.

# 0.6.1

*6 November 2026*

A union's rest closes when nothing can add tags to it.

# 0.6.0

*6 November 2026*

The editor shows inferred types in lenses and inlay hints, and goes to definitions; tags print in interpolated
strings.

# 0.5.3

*6 November 2026*

A union's rest variable is named from the end of the alphabet.

# 0.5.2

*6 November 2026*

Inferred type variables are named with Greek letters, and a match's scrutinee can be any expression.

# 0.5.1

*5 November 2026*

Types print as they are written.

# 0.5.0

*5 November 2026*

Hovering in the editor shows inferred types.

# 0.4.1

*5 November 2026*

The language server starts from the VS Code extension again.

# 0.4.0

*5 November 2026*

Multi-branch `if` with `is` tests, rigid type variables in annotations, and `nanyx hello`.

# 0.3.0

*4 November 2026*

Closures, functions as values, `for` loops over ranges and lists, `rec` local functions, floats, record update,
or-patterns, guard patterns, `if ... is` and tuple destructuring.

# 0.2.0

*4 November 2026*

A language server for diagnostics, and the start of the standard library: `nanyx/console` and `nanyx/text`.

# 0.1.0

*4 November 2026*

The first release: `nanyx run` compiles a program to WebAssembly and runs it on an embedded Wasmtime.
