---
title: "Changelog"
description: "What changed in each release of the Nanyx compiler"
order: 2
---

Each release of the compiler, newest first: what it is about, and the changes you will notice. The binaries for every
release are on the [GitHub releases page](https://github.com/nanyx-lang/nanyx/releases?q=compiler); see
[Installation](../getting-started/installation) for how to get them. The VS Code extension is released on its own.

# 0.32.0

*21 November 2026*

A small release about packages. `nanyx.json` is now a package published to the index rather than one the compiler
ships, a script can use a package from the index by naming its exact version, and `nanyx publish` can publish a
package's first version.

**Standard library**

- `nanyx.json` is published from [nanyx-lang/json](https://github.com/nanyx-lang/json) at 0.1.x instead of shipping
  with the compiler. **This breaks code** that uses it without declaring it: a package adds
  `(package = "nanyx.json", version = "0.1")` to its dependencies (or runs `nanyx install nanyx.json`), and a script
  writes `::dependency nanyx.json 0.1.1` ([JSON](../stdlib/json)).
- **This breaks code** that calls `reduce`: it starts from the first element and gives `#none` for an empty
  collection, instead of starting from `Default(a)`'s value, on every collection type.

**Tools**

- A script names a package from the index at an exact version, `::dependency nanyx.json 0.1.0`, and that version is
  its pin, so a script needs no lockfile. `nanyx run` and `nanyx pull script.nyx` download it, and a build reads the
  cache alone. A dependency with no version, or a range, is `NYX0009`, with a quick fix that writes in the newest
  version ([Files and projects](../getting-started/files-and-projects), [Packages](../tour/packages)).

**Fixes**

- `nanyx publish` no longer fails on a package's first version, and `install`, `pull` and resolution now explain that
  the index doesn't list a package instead of reporting a missing file
  ([The command line](../standard-tools/command-line)).

# 0.31.0

*20 November 2026*

A release about packages and errors. A package can now depend on published packages by name and version: `nanyx
install` adds one, the lockfile is written for you, and `nanyx publish` releases a new version, refusing a version
bump too small for what changed. Every error now has a short headline that names what it's about, with the details
below it. Programs can run in a web page, under a new playground host, and the old parser is gone.

**Language**

- An error now shows a short headline, its code's title and the one name it's about, such as `Missing context
  'Console'`, with the details on the next line, in the editor and in the terminal. Every error and linter message has
  been rewritten to fit ([Compiler errors](../standard-tools/errors)).
- **This breaks** anything that relied on the old parser behind `NANYX_PARSER=fparsec`: FParsec and the switch are
  gone. After a syntax error, the parser carries on at the next top-level item, so the rest of the file is still
  checked.

**Standard library**

- `nanyx/clock`: a `Clock` context with `now` (Unix milliseconds) and `ticks` (milliseconds that never go
  backwards), provided by the console and HTTP hosts ([Clock](../stdlib/clock)). **This breaks code** that declares
  its own context named `Clock`.
- `nanyx.playground`, a host that stands in for the console host in a web page, providing `Console`, `Random` and
  `Clock`, with a runner that runs a program in a web worker ([Hosts](../advanced/hosts)).

**Tools**

- `nanyx install <name>` adds a dependency from the package index at its newest usable version, and `nanyx pull`,
  `build` and `run` write `nanyx.lock` when the manifest asks for something it doesn't lock. Versions are chosen with
  PubGrub, and a conflict (`NYX0047`) is explained step by step. `nanyx build --locked` fails on a stale lockfile, for CI
  ([Package dependencies](../tour/packages#package-dependencies), [`nanyx install`](../standard-tools/command-line#nanyx-install)).
- `nanyx publish` checks, packs and releases a package on its GitHub repository, refusing a version bump too small
  for what changed, and registers its first version with the index. `nanyx yank` withdraws a version. A manifest
  names its repository with `repository = "owner/name"` ([Publishing](../tour/packages#publishing)).
- `nanyx api-check` compares two versions of a package's API, as the index does for each release
  ([`nanyx api-check`](../standard-tools/command-line#nanyx-api-check)).
- `NYX0048` (Dependency not locked) replaces the old error for a dependency the lockfile doesn't lock, and says to
  run `nanyx pull`.
- The formatter keeps a union type written over several lines, one case per line starting with `|`, and wraps a long
  type definition ([Formatter](../standard-tools/formatter)).

**Performance**

- The checked standard library is read with a typed reader per type, its maps built straight from their sorted items:
  `nanyx check` of a one-line program allocates about 70 MB, from 138 MB, and is faster to start.

# 0.30.0

*20 November 2026*

A release about types you can look at and a parser you can build on. Functions take types as arguments, a `Type` can
be matched on, and records and unions can be built from their descriptions, which is enough to write a generic JSON
encoder and decoder in Nanyx. `dbg` says where it was called and goes to the host, crashes print a stack with file and
line, and `nanyx run --profile` writes a profile. The parser is new: a lexer, a layout pass and a recursive-descent
parser replace FParsec. Floats are no longer boxed, and the first compile of a program is faster. Several changes break
existing code; they are marked below.

**Language**

- Type arguments: a function can take a type, as `type a`, and inspect it with a `match` on a `Type`; a record or a
  union case can be built from its description with `build` ([Type arguments](../tour/functions#type-arguments),
  [Inspecting a type](../tour/functions#inspecting-a-type), [Building a value](../tour/functions#building-a-value)).
- `Typed(a)`: the compiler gives an instance for every type ([Typed(a)](../advanced/contexts#typeda)).
- An associated type is also a type argument, `type Iter(c).elem`, and signatures write associated types by name, as
  `Iter(c, elem = string)` ([Associated types](../advanced/contexts#associated-types)).
- `Iterable(c)` in the prelude: a `for` loop walks anything `Iterable`, and a slice, a set, a `seq` or anything
  `Iterable` can be spread into a collection literal ([Loops](../tour/loops)).
- A structural type can be protected or private, with a constructor
  ([Protected types](../advanced/nominal-and-protected-types#protected-types)).
- A def that writes its type can call itself at other types
  ([Type inference](../tour/type-inference-and-annotations)).
- `export m` re-exports a whole module, and a file can re-export one of its own `module name =` blocks, which can
  also be nested ([Re-exporting](../tour/modules#re-exporting)).
- An `@extern` function can be passed as a value.
- **This breaks code** that calls a parameter needing a scoped context without supplying it: that is now `NYX0211`,
  unless the def loads an instance with `use` ([Contexts](../advanced/contexts)).
- **This breaks code** with a match arm for a tag the matched union doesn't have: it is now a type error, with a fix
  that removes the arm ([Pattern matching](../tour/pattern-matching)).
- **This breaks code** that imports a block's names selectively and relies on getting the rest: only the named ones
  come in, and an unknown one is `NYX0104`.
- Parse errors come from the new parser. Where the old one had a focused message, the text and place are the same;
  elsewhere, an error names what was expected and what was found, as ``Expected `]`, found `xs` ``.

**Standard library**

- `nanyx.regex`, a linear-time regular expression engine written in Nanyx, with `#Matches` and `#MatchesRegex` for
  regexes in match arms ([Regex](../stdlib/regex)).
- A `Random` context, supplied by the hosts and seedable ([Random](../stdlib/random)).
- `seq` has every function `list` has. **This breaks code** that calls `concatMap`: it is `flatMap`
  ([Sequences](../stdlib/prelude-sequences)).

**Tools**

- `dbg` prints the file and line it was called from, and release builds strip it. It goes to the host: the console
  host writes it to stderr, the HTTP host to its log with the request. **This breaks** anything that read `dbg`
  output from stdout ([Debug and release builds](../tour/error-handling#debug-and-release-builds)).
- A crash, a deferred error or a runtime error prints the stack, with each frame's file and line.
- `nanyx run --profile <file>` samples the run and writes a profile for the Firefox profiler, with each function's
  file and line ([`nanyx run`](../standard-tools/command-line#nanyx-run)).
- The linter reports a value that a `for` loop's body ends with as unused
  ([Official linter](../standard-tools/official-linter)).
- Rename a `module name =` block.
- `NANYX_PARSER=fparsec` switches back to the old parser, for this release only.

**Performance**

- A `float` is an `f64` and an `F32` an `f32`, not a box on the heap: float arithmetic allocates nothing.
- The checked standard library is read a module at a time, only when it is imported, and carries its exports: the
  first compile of a one-line program allocates 146 MB instead of 261 MB, and `nanyx check` of it is about 25% faster.

**Fixes**

- A function that requires a parameterised context, named as a value, finds its instance when only the def's
  annotation settles its type, instead of crashing the backend.
- A match whose arms join a tag with a named union type checks whichever arm comes first, and no longer leaks memory
  when a `map.get` match is followed by a call with a lambda.
- A module block nested in a module block parses, and the outer block's later items stay in the outer module.
- A package module with a parse error reports that error.

# 0.29.0

*17 November 2026*

Packages arrive: a manifest written in Nanyx, path and GitHub dependencies, a lock file, and shipped packages for
files, JSON and HTTP, including a host that serves a web handler. The standard library gains arrays, slices and
mutable collections, the language gains custom patterns and associated types, and the editor gains a set of
refactorings. Several changes break existing code; they are marked below.

**Language**

- Custom patterns: declare total and partial patterns, give them arguments, and compose them with `&`
  ([Custom patterns](../advanced/custom-patterns)).
- Associated types: a context can have a member of kind type, as `FromString(a)` does for its failure type; generic
  values can be context members, and a type can be generic in contexts ([Contexts](../advanced/contexts)).
- Square-bracket literals build any collection type through `fromArray`, and an array can be called: `xs(i)` is
  `array.at(xs, i)` ([Collections](../tour/collections)).
- `seq { }` with a bare `yield`, and a `for` loop walks a `seq` ([Sequences and queries](../advanced/sequences-and-queries)).
- `Async` marks a function that can wait; the console and HTTP hosts provide it ([Async](../stdlib/async)).
- Foreign functions are declared with `@extern`, and hosts get typed access to linear memory ([Hosts](../advanced/hosts)).
- A type can be declared inside a block, and `+=` is a spare operator (`MList` attaches it to append).
- **This breaks code** that separates contexts with `&`: contexts are separated with `,`, as in `[Eq(a), Hash(a)]`.
- **This breaks code** that writes a context as a type or holds an instance in a def: a context's instance is written
  only in a `use`. A scoped context must be protected or private.
- **This breaks code** that requires `Add(a)` for `-`: `Add(a)` has `zero` and `+`, and the new `Sub(a)` has `-` and
  `negate`.
- **This breaks code** that lists `publicModules` in a manifest: a module is made public with `export module`.
- Errors are rewritten in one house style across the parser, the type checker and the backend, and a look-alike
  character such as U+2212 for `-` is named, with a fix ([Errors](../standard-tools/errors)).
- A program that crashes says why: integer overflow, division by zero, a float that can't be an int, a negative
  power, running out of stack (naming the function) or out of memory.

**Standard library**

- `Array`, `Slice`, `MSlice` and `StringSlice`, and the mutable `MRef`, `MArray`, `MStack` and `MList`
  ([Array](../stdlib/array), [Slice](../stdlib/slice), [MArray](../stdlib/marray), [MList](../stdlib/mlist)).
- Immutable `Stack`, `Queue`, `Trie` and `StringTrie` ([Stack](../stdlib/stack), [Queue](../stdlib/queue),
  [Trie](../stdlib/trie)).
- `seq` has a runtime form; `seq.zip`, `set.toSeq`, `map.toSeq`, and `fromSeq`, `build` and `reduce` on every
  collection type ([Sequences](../stdlib/prelude-sequences)).
- `MArray.sort` and its family sort in place, stably.
- `list.zipWith`, `intersperse`, `chunks`, `windows`, `scan`, `sortDescending`, `sortByDescending` and `sortWith`;
  `allSome`; `float.format`; `Default(a)` ([Lists](../stdlib/prelude-lists)).
- Trigonometry, base-10 and base-2 logarithms, `infinity`, `nan` and its tests, and bitwise functions on ints
  ([Math](../stdlib/math)).
- Shipped packages: `nanyx.fs` for files and directories (`file.lines` reads lazily), `nanyx.json` with decoders and
  encoders, and `nanyx.http.core` with an HTTP client ([File system](../stdlib/filesystem), [JSON](../stdlib/json),
  [HTTP](../stdlib/http)).
- **This breaks code** that calls `panic`: it is `crash`, and `orCrash` joins it.
- **This breaks code** that calls `count` for a size: maps, sets, `MList` and `MStack` have `length`, as every other
  collection does.
- **This breaks code** that imports `nanyx/console`: `Console` and `Env` come from the console host's
  `nanyx.console.core` ([Console](../stdlib/console)).

**Tools**

- The package manifest is a `.nyxp` file holding a Nanyx record, not TOML. **This breaks** every `.toml` manifest,
  which the compiler recognises and reports ([Packages](../tour/packages)).
- Dependencies on a directory, a file or a GitHub repository pinned to a commit; `nanyx.lock`, `.nyxpkg` packs and a
  package cache, with `nanyx pull` and `nanyx install`; `nanyx migrate` turns scripts into a package
  ([Packages](../tour/packages)).
- Any package can be a host. The HTTP host serves a `Request -> Response` handler with `nanyx run`
  ([Hosts](../advanced/hosts), [HTTP server](../cookbook/http-server)).
- Scripts name their host and dependencies with `::host` and `::dependency` lines
  ([Files and projects](../getting-started/files-and-projects)).
- `nanyx bump` compares a package's API with its last version and picks the next one; `nanyx search` finds library
  functions by name and type; `nanyx format --stdin`; `nanyx run --memory`; and the compiler writes `.wasm` binary
  ([The nanyx command](../standard-tools/command-line)).
- The linter has `@allow(rule, ...)` and the rules `uselessExpressions`, `unreachableCode` and an unexported nominal
  type ([Official linter](../standard-tools/official-linter)).
- In the editor: rename and find references across a package, workspace symbols, semantic tokens, expand selection,
  format a range, and refactorings that introduce a local, a function or a type alias, inline one, write an inferred
  type, convert between a call and a pipe, a match and an if, a lambda and its shorthand, and `++` and an
  interpolated string, organise imports, and write stubs for a context's missing definitions
  ([VS Code extension](../standard-tools/vs-code-extension)).

**Performance**

- The standard library and shipped packages are checked when the compiler is built, and a module is checked in
  linear time.
- The inliner reaches only what the start reaches, takes a function called once whatever its size, and specialises a
  function on a lambda it is given ([Compiler optimisations](../meta/compiler-optimisations)).
- Functions borrow what they only read, cells taken apart by a pattern are reused, and `MList.set` allocates nothing.
- Small blocks have free lists, and large ones boundary tags, so freeing never walks the heap.
- A program imports only the WASI functions it uses.

**Fixes**

- Many type-checker fixes around contexts: point-free functions, lambdas that require contexts, imported aliases,
  recursive sequence types, and errors that no longer cascade after an earlier one.
- A literal is checked against the type expected of it, through tags, lists and maps.
- Derived `ToString` and `==` work for a type seen only under an import's alias.

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
  ([Inlining](../deep-dives/inlining)).
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
  ([Reference counting](../meta/reference-counting)).
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
- Tail calls use no stack ([Tail calls](../deep-dives/tail-calls)).
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
