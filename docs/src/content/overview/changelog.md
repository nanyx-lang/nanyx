---
title: "Changelog"
description: "What changed in each release of the Nanyx compiler"
order: 2
---

Each release of the compiler, newest first: what it is about, and the changes you will notice. The binaries for every
release are on the [GitHub releases page](https://github.com/nanyx-lang/nanyx/releases?q=compiler); see
[Installation](../getting-started/installation) for how to get them. The VS Code extension is released on its own.

# 0.41.0

*25 November 2026*

A release about units of measure. A number can carry a unit, `500ms`, the type checker keeps a dimension's units
apart, and arithmetic and printing know them. The prelude declares `Time`, and `sleep` takes one. It also fixes a
regression in 0.40.0 with imported generic types. One change breaks code: it is marked below.

**Language**

- `dimension` and `unit` declarations are checked. A dimension is a type, `Time(int)`, stored as the number it is
  generic in, and a unit is a scale of its dimension's base unit, worked out exactly. A unit that can't be one is an
  error, from [NYX0286](../standard-tools/errors#nyx0286) to [NYX0291](../standard-tools/errors#nyx0291)
  ([Units and dimensions](../work-in-progress/units-and-dimensions)).
- A number with a unit, `500ms` or `0.5s`, is a value of the unit's dimension, stored as its number of base units.
  It must be a whole number of them in an integer storage, and a number with no unit is never a measured value.
- A signature that names a dimension, `Time, Time -> bool`, gives each storage it leaves out a type variable of its
  own that requires `Num`. A storage that no parameter fixes is [NYX0293](../standard-tools/errors#nyx0293), and a
  storage written for a dimension that fixes its own is [NYX0294](../standard-tools/errors#nyx0294).
- `+`, `-` and `%` take two values of one dimension, `*` scales one by a number, and `/` and `//` of two values of one
  dimension give a plain number. Two dimensions in one sum, or a product of two dimensions, is a type mismatch that
  names them.
- A measured value has a `toString`, which writes it in the largest unit it is a whole number of: `120s` prints as
  `2min`, and zero as `0ns`.
- A `char`'s type description is `#char` ([Inspecting a type](../tour/functions#inspecting-a-type)).

**Standard library**

- The prelude declares `dimension Time` with the units `ns`, `us`, `ms`, `s`, `min`, `hr` and `d`, seen by every
  module on every host. A module's own `Time` or unit of the same name is the one its names mean
  ([prelude: time](../stdlib/prelude-time)).
- `sleep` takes a `Time(int)`: `sleep(500ms)`, `sleep(0.5s)`. A host waits for the time's whole milliseconds, rounded
  up. **This breaks code** that calls `sleep` with a number of milliseconds, or that writes a `Clock` whose `sleep`
  takes an `int`: write `sleep(500ms)` ([clock](../stdlib/clock)).
- `char` has `isAsciiUppercase`, `isAsciiLowercase`, `isAsciiAlphanumeric`, `uppercase` and `lowercase`
  ([prelude-chars](../stdlib/prelude-chars)).

**Fixes**

- An imported generic type whose `==` is inferred to require `Eq`, such as one that compares its members with
  `list.contains`, can be compared again where its values are made without a written type; 0.40.0 rejected these with
  a type mismatch. A generic record type's constructor makes a value of each type it is given, so `Box(v = 1)` and
  `Box(v = "x")` no longer clash.
- A local function that declares `[Eq(t)]`, `[Ord(t)]` or `[Hash(t)]` can be called with a built-in type such as
  `int`, `string` or a list; before, it was a backend error.
- A tag that isn't one of a union's tags is reported with the union's tags listed, and the closest suggested.
- An `if` with no `else` whose `then` branch gives a value says that it has no `else`, and so gives `()`.

# 0.40.0

*25 November 2026*

A release about numbers, characters and maps. `/` gives a fraction and a new `//` divides and rounds down, a `char`
type holds one Unicode scalar value, and maps and sets are hash tries, much faster than before. The shipped packages
take new names, with every host named `<name>.host`. Five changes break code: they are marked below.

**Language**

- `/` divides with a fraction on every number type, so `7 / 2` is `3.5`, and `//` divides and rounds down. `%` is
  floored to match, so `-7 % 2` is `1`. `Num` has a `fraction` type, which its `/` gives. **This breaks code** that
  divides ints and expects an int: write `//` ([Operators](../tour/operators#arithmetic-and-comparison),
  [Sized numbers](../advanced/sized-numbers#division)). A negative power of a type whose fraction is another type is
  [NYX0292](../standard-tools/errors#nyx0292).
- `char` is one Unicode scalar value. A string literal is a `char` where a `char` is expected, and one that isn't
  exactly one scalar value there is [NYX0284](../standard-tools/errors#nyx0284) ([Types](../tour/types#characters)).
- A record pattern can name some fields alone and rename others, as in `(x, y = b)`
  ([Pattern matching](../tour/pattern-matching#records)).
- A manifest's `package` can give `nanyx = "0.40"`, the oldest compiler the package builds with, which `nanyx new`
  writes and `nanyx publish` requires. An older compiler reports [NYX0052](../standard-tools/errors#nyx0052)
  ([Packages](../tour/packages#package-manifests)).
- A number with a unit straight after it, `500ms`, is read as one literal, and `dimension` and `unit` declarations are
  parsed, the first step of units of measure. A letter straight after a hexadecimal, octal or binary number is an
  error.

**Standard library**

- The shipped packages are renamed: `nanyx.console.core` is `nanyx.cli`, the console host `nanyx.console` is
  `nanyx.cli.host`, `nanyx.http.core` is `nanyx.http`, the HTTP host `nanyx.http` is `nanyx.http.host`, and
  `nanyx.playground` is `nanyx.playground.host`. **This breaks code** that names one by its old name, which is
  [NYX0054](../standard-tools/errors#nyx0054) and gives the new one ([Hosts](../advanced/hosts)).
- `nanyx.regex` is no longer shipped: it is a package of its own in the package index.
- `char` has `toInt`, `fromInt`, `toString`, `isDigit`, `isAsciiLetter` and `isWhitespace`, `string.chars` gives a
  `list(char)`, and `string.fromChars` joins one back. **This breaks code** that used `string.chars` as a list of
  strings ([prelude-chars](../stdlib/prelude-chars)).
- Maps and sets give their entries in the order of their keys' hashes, which is unspecified, rather than the order
  they were added, and `json.print` and `json.encode` write an object's keys in that order too. **This breaks code**
  that relied on insertion order: sort for an order of your own
  ([Collections](../tour/collections#common-collection-abstractions)). A map or a set can now be a key, or an element
  of a set.
- `Array` has `list`'s core collection functions, among them `filter`, `filterMap`, `select`, `find`, `any`, `all`,
  `sort`, `sortBy`, `take`, `skip`, `zip` and `scan`, none of which builds a list on the way ([array](../stdlib/array)).
- `string.build` builds a string from the pieces a block emits, in one growing buffer ([text](../stdlib/text)).
- `int` and each sized integer type have `maxValue` and `minValue`
  ([Sized numbers](../advanced/sized-numbers#overflow)).
- `math.mod` is gone, since `%` is now floored. **This breaks code** that calls it: write `%`.
- `float.pow` of an exponent that isn't a whole number is within a unit in the last place of the exact power.

**Tools**

- Hover, go to definition, references, rename and Info View show a call last in a scope def as the call written,
  rather than by a generated name.
- Info View names a protected or private context, with its link.
- The formatter writes a def's tuple value without brackets, `def x, y = 1, 2`.
- `nanyx stdlib-docs` reports a pinned package that doesn't compile, where it crashed.

**Performance**

- A map's lookup, insert and remove take time in proportion to the log of its size: building a 100,000-key map and
  reading it back takes about 1.3 s, from 91 s. A `for` loop over a map or a set walks it in place and allocates
  nothing.
- Reading a decimal of up to 19 digits as a float uses Eisel–Lemire, without big numbers.
- The compiler runs with server garbage collection, and stops inlining a function once its body is settled.
- A def of thousands of calls that pass lambdas, or of thousands of `start!`s, is type checked in linear time.
- A lambda is specialised on the context members its function knows, so `list.sum` and the other folds call them
  directly, and a function called through a function value borrows its context members.
- A task's memory is freed when it finishes, and a def that starts a task and calls itself last runs as a loop, as
  does a scope def that writes no type.

**Fixes**

- A `for` over a map or a set in a builder, whose body uses `try` or `return`, compiles and runs.
- Each item of a list literal is checked against a known expected element type, so two lambdas giving different tags
  of the expected union fit.
- A lambda that calls an unannotated def that waits waits too, and a def in a cycle with it sees the contexts it
  requires.
- A lambda finds the instance it is given inside a `use` of the same context at another type.
- Comparing maps whose types hold a type variable nothing solves, or maps in a trie, compares their entries.
- A task's function that holds a `memory` block's value is caught however the task is started.
- A script's statements no longer share type variables, which gave a spurious NYX0233.
- A def that starts a task without naming `start` is reported at the call, with a span.
- A lambda that `yield`s given to a builder is NYX0201 at its `yield`, saying to write `emit`.
- A keyword call such as `start!` that calls a local named `start` is reported at the keyword, naming the local.
- A unit argument's work is done inside a call through a function value, where it could be lost across a wait.

# 0.39.0

*24 November 2026*

A release about building collections and type arguments. The collection builders take a function that pushes each
value with `emit`, through a new `Emit` context, so `list.select` and the others allocate a cell per value and no more.
A parameter that takes a type is written `Type(a)`. It also fixes a run of contexts given by `use`, and makes writing
floats as text much faster. One change breaks code: it is marked below.

**Language**

- A parameter that takes a type is written with its type, `Type(a)`, as in `def parse: [Decode(a)] (string, into:
  Type(a)) -> a`; a named one written `into: Type(into)` makes `into` a type variable of the signature. Calls don't
  change. The old forms, `type a` and `into: type`, still compile, with warning
  [NYX0282](../standard-tools/errors#nyx0282) and a quick fix ([Functions](../tour/functions#type-arguments)).
- A def whose value calls a function needing a context of a type only a later use would fix is
  [NYX0283](../standard-tools/errors#nyx0283) at the def, which says to write its type or pass a type argument.
  Before, it was NYX0205 at the call, or a crash in the backend inside a function
  ([Type inference](../tour/type-inference-and-annotations#target-typing)).
- `???` has type `undefined`, and `undefined` agrees with any type expected of it, so `[crash("no")]` is a `list(int)`
  where one is expected, and using a value of type `undefined` reports no further errors
  ([Types](../tour/types#top-and-bottom-types)).
- A lambda that a `use` gives a context's member is inferred where the `use` is, so the contexts it needs besides the
  member's own are required by the def around it, and `use Emit(emit = ...)` without its type compiles where the def
  settles it ([Contexts](../advanced/contexts)).
- A destructuring def, `def (a, b) = pair`, can be written in a `module` block, and exported from it.

**Standard library**

- The prelude has `Emit(a) = (emit: a -> ())`. `list.build`, `list.select` and the `build` and `select` of `array`,
  `MArray`, `MList`, `set`, `map`, `Slice`, `Queue` and `Stack` take a function that requires `Emit` and calls
  `emit(x)` for each value, and add each value as it is emitted. `list.select` over 300,000 elements makes 750,000
  allocations, from 3,150,000. **This breaks code** that gives one of them a block that `yield`s: write `emit` for
  `yield` ([Building a collection](../advanced/sequences-and-queries#building-a-collection)).

**Tools**

- The official linter's `resultOnlyTypeVariable` rule warns about a function whose result has a type variable that a
  context it requires names, but that no parameter fixes
  ([Official linter](../standard-tools/official-linter#type-variable-only-in-the-result)).
- A runtime function's exception is reported with its type and stack.

**Performance**

- Writing a float as text takes about a tenth of the time it did, and a whole-number float is written from its integer.
- A `match` on a tag union reads the tag once, and not at all for a union with no payloads.
- A `memory` block whose cells are all locals allocates no region.
- `MArray.at` and `MArray.set` no longer count the array twice.
- A function of thousands of locals compiles in linear time in a debug build, where 8,000 ran out of memory, and a
  long chain of statements in one block is lowered in linear time.

**Fixes**

- A range nested in a range's bound runs, where it was NYX0301.
- A list literal of one spread is a literal: `[...s]` of a set is a list of its elements, `[...r]` of a record is
  NYX0258, and the formatter keeps `[...xs]` as written.
- A function that waits no longer frees a value an argument still needs, which `--check-memory` caught in
  `nanyx.regex`.
- A value that failed to check, or a `???`, no longer gives a second error, NYX0202, at the def.

# 0.38.0

*24 November 2026*

A release about concurrency. Programs can start tasks with `start!`, wait for them with `await`, race them and cancel
them, and a request or a `sleep` lets the program's other tasks run, on the console and HTTP hosts and in the
playground. It also brings constructor patterns, a pipe shorthand for lambdas, short-circuiting `and` and `or`, and a
run of faster type checking. Four changes break code: they are marked below.

**Language**

- Tasks: `start! e` runs `e` at the same time as the code that started it and gives back a `Task`, and `await(t)` waits
  for its result. A def that starts tasks waits for all of them before it returns, and `concurrent({ ... })` makes a
  smaller scope. A task can't leave the def that started it (NYX0233), or hold a `memory` block's cells or references
  (NYX0279) ([Async](../advanced/async#tasks)).
- `race` runs functions as tasks, gives back the first result and cancels the rest. In a cancelled task a request or a
  file's function gives `#cancelled`, and a `sleep` returns at once. **This breaks code** that matches every case of
  `HttpError` or `FsError`: add a `#cancelled` arm ([Async](../advanced/async#racing)).
- `start`, `await`, `race` and `concurrent` belong to `Async`: a def attached to a context is in scope by its bare name
  wherever the context is. **This breaks code** that imports them, as `import nanyx/async as (start, await)`, which is
  now NYX0104 ([Attached definitions](../advanced/attached-definitions)).
- Functions that need different contexts can share a list or an `if`'s branches, so `race([{ #some(http.get(url)) },
  { sleep(5_000); #timedOut }])` type-checks ([Contexts](../advanced/contexts)).
- A constructor pattern takes a nominal value apart: `Email(s)` binds the whole value, and several patterns, `...` or
  named fields read its fields, as `Email(address = s)` ([Pattern matching](../tour/pattern-matching#named-types)).
- A lambda that starts with a pipe, `{ \math.abs }`, is `{ x -> x \math.abs }`
  ([Lambdas](../tour/lambdas-and-higher-order-functions)).
- `and` and `or` evaluate their right operand only when the left doesn't decide, so `x != 0 and 10 / x > 1` no longer
  divides by zero ([Operators](../tour/operators)).
- A lambda checked against a function type whose input has named fields takes them by name. **This breaks code** that
  wrote `{ a, b -> ... }` for `(x: int, y: int) -> int`: it is now NYX0280; rename a field with a record pattern,
  `{ (x = a, y = b) -> ... }` ([Functions](../tour/functions)).
- A lambda given for a record field, or given back by a function, can use the contexts of the function type it is
  checked against ([Contexts](../advanced/contexts)).

**Standard library**

- `Clock` has a `sleep` member, in milliseconds. **This breaks code** that writes `use Clock(now = ..., ticks = ...)`:
  give it a `sleep` too ([clock](../stdlib/clock)).
- `string.fromUtf8` copies its bytes once, and `replace`, `repeat`, `padStart`, `padEnd` and `reverse` make one
  allocation each.
- `nanyx/math` has the hyperbolic functions, `hypot` and `cbrt` ([math](../stdlib/math)).

**Tools**

- The official linter's `unawaitedTask` rule warns about a task that is started and never awaited
  ([Official linter](../standard-tools/official-linter#task-that-is-never-awaited)).
- The playground provides `Async`, so a program's tasks take turns while one sleeps.
- A mismatch among a call's arguments is reported once, at the argument that fixed the type, and a record field
  mismatch names each record's own fields.

**Performance**

- Checking a function that uses an operator thousands of times on an unannotated parameter takes linear time: 8,000
  uses of `+` take 4.8 s, from 46.8 s.
- A range nested in its own bound is inferred once per level: 18 deep checks in under 2 s, from 113 s.
- A small function on the right of `and` or `or` is inlined.

**Fixes**

- A list of records built up in a fold no longer reads freed strings after a record spread.
- Calling a record field whose function type has named parameters no longer traps.
- An array of tuples can be written, and a `crash` in a list of numbers no longer makes invalid WebAssembly.
- A deferred error in a `memory` block's lambda is reported instead of trapping.
- A `mut` at a script's top level is NYX0281, a cell declared at the top level, instead of advice that didn't apply.
- An option nested in an option finds its `Default`, so `valueOrDefault` works on one.
- `float.fromString` reads every decimal as the float nearest it, so every float `float.toString` writes reads back
  as itself, and `math.exp` is right near the ends of the float range.

# 0.37.0

*23 November 2026*

A small release with one fix to local recursive functions and contexts. An annotated local `rec` is now checked like an
annotated local `def`, which can report errors in code that compiled before.

**Fixes**

- A local `rec` that writes its type runs only in the contexts its type declares, and is checked against the parameter
  and result types it writes, as a local `def` is. **This breaks code** whose `rec` calls something that needs a context
  its type doesn't declare: it is now NYX0211, fixed by adding the context, such as `[c]`, to the type
  ([Contexts](../advanced/contexts)).
- A local function that writes a context variable of the function around it, such as `def go: [c] int -> int` inside a
  `[c]` function, compiles and runs, instead of failing with NYX0301.

# 0.36.0

*23 November 2026*

A release about patterns, loops and sequences. A pattern can name the fields it wants and leave out the rest, a
`while` condition can bind names with `is`, and a builder's `for` walks any collection. Sequence blocks make about half
as many allocations, a new lint catches a sequence with effects pulled twice, and a run of fixes closes holes in
shadowing, workflows and comments.

**Language**

- A record or tag payload pattern whose fields have no `=` may end in `...`, which leaves the other fields out:
  `(name, ...)`, `#rectangle(width, ...)` ([Pattern matching](../tour/pattern-matching)).
- A `while` condition's `is` test binds its names in the loop's body, through an `and` chain, as an `if` condition does
  in its `then` branch ([Loops](../tour/loops)).
- A `for` loop that yields, in `seq { ... }` or `list.build { ... }`, walks any `Iterable` collection, not only a
  sequence ([Workflows](../advanced/workflows)).

**Tools**

- The official linter's `repeatedPulls` rule warns when a sequence whose steps require contexts, such as
  `[Log] seq(int)`, is pulled more than once, and offers to collect it into a list
  ([Official linter](../standard-tools/official-linter#sequence-with-effects-pulled-more-than-once)).
- `use` of a context that is misspelt or not imported is NYX0108, an unknown context, with a "did you mean" or the
  import to add, instead of NYX0249 ([Errors](../standard-tools/errors)).
- Hover at a call leaves out a context variable that resolved to no contexts: `list.select` shows no `[d]`.

**Performance**

- A sequence block makes fewer steps: a `seq { for 1..n | i -> yield i }` allocates 11 blocks per element, down from 20
  ([Sequences and queries](../advanced/sequences-and-queries)).
- A nested type is parsed once per level: `list(list(…))`, tuples, function types and tags 20 deep took minutes, and
  now take a fraction of a second.
- Checking a module after a type error no longer solves the whole module again for each type looked up: a large
  package with one error is checked in seconds, not minutes.

**Fixes**

- A `def` or `mut` in a lambda that shadows an outer name can read that name in its own value, as
  `mut state = #continue(state)` in a `memory` block does.
- A recursive structural record can be built by its type's name: `S(next = ...)` for
  `type S = (next: () -> #next(int, S) | #done)`.
- A context member generic in its own type variables takes a tuple whole: `proc { return (try num(x), try num(y)) }`
  runs instead of trapping.
- A comment before a lambda's `}` is never a syntax error, whether after its last arm or after a one-line body, and the
  formatter keeps it inside the lambda ([Formatter](../standard-tools/formatter)).

# 0.35.0

*23 November 2026*

A release about memory and reflection. A `memory` block now frees the mutable objects made in it when it ends,
cycles included, and the escape checks behind that are stated as a guarantee. Code that walks a type's description
handles recursive types, and `Result` is gone in favour of failures as tags of their own. Programs can now call
other WebAssembly components and be called by them.

**Language**

- `Result` and its functions are removed, and the standard library no longer wraps failures in `#error(...)`: a
  failure is a tag of its own, named for what went wrong, such as `#invalidPattern(RegexError)`, and a union alias can
  name a group of them, as `IoError` and `HttpError` do. **This breaks code** that uses `Result`, or that matches
  `#error(e)` from the standard library or the shipped packages ([Error handling](../tour/error-handling)).
- The sequence type is `Sequence(a)`, not `Seq(a)`. The lowercase `seq(a)`, `seq.` and `seq[1, 2, 3]` are unchanged,
  and `Sequence[1, 2, 3]` is a sequence too. **This breaks code** that names `Seq`
  ([Collections](../tour/collections)).
- A tag's payload may be positional types followed by named fields, as a record type writes them:
  `#rect(string, width: float, height: float)` ([Tags](../tour/tags)).
- A `try`, `yield` or `return` that is an argument's value ends at the argument's `,`, so
  `P(a = try num(x), b = try num(y))` works on one line, and a one-line builder body such as
  `proc { return try num(x) }` works ([Workflows](../advanced/workflows)).
- A private type can't be taken apart, read or updated outside its module: its fields, destructuring, spreads and
  `with` are NYX0212 there. A private type gets no `==`, `<` or hashing derived from its hidden representation, and
  nor does a structure holding one ([Nominal and protected types](../advanced/nominal-and-protected-types)).
- A scoped context may have members, and a `use` of an instance of it supplies it
  ([Contexts](../advanced/contexts)).
- A `memory` block frees the mutable objects made in it when it ends: its references (`MRef`), the boxes of cells a
  lambda captures, and `MArray`s, cycles through them included. Each block has a region of its own; a mutable object
  stored into an outer block's object, or given back from the block, moves to the outer region
  ([Memory](../advanced/memory)).
- NYX0233, a value of a `memory` block escaping it, is now a guarantee the runtime relies on, with the routes the red
  team found closed: a callback's result, an outer block's cell or `MRef`, a named `Memory`, a captured reference, a
  `use`'s instance, and a context member named as a value ([Memory](../advanced/memory)).

**Standard library**

- `string.toUtf8` and `string.fromUtf8`, between a string and its UTF-8 bytes ([text](../stdlib/text)).
- `Slice` gets the core collection functions, with `list`'s names: `filter`, `select`, `find`, `any`, `all`, `sort`,
  `take`, `skip`, `zip` and more. `Slice.ofArray` is renamed `Slice.fromArray`, so `Slice[1, 2, 3]` builds a slice.
  **This breaks code** that calls `Slice.ofArray` ([Slice](../stdlib/slice)).
- `MList.add` makes one allocation per element, not two.
- `use Log(log = println)`: a generic function given as a context member finds its instances
  ([Contexts](../advanced/contexts)).

**Tools**

- Components: a program can import functions another component exports with `@import`, naming their parameters, and
  build a library component with no `@entry`. An exported function that needs contexts runs under the ones its host
  provides. A Nanyx component and Rust components call each other in CI ([Attributes](../tour/attributes)).
- `nanyx yank` is now `nanyx unlist`, and the index records `unlisted`. A command that reads the index for a locked or
  pinned version that is unlisted warns, NYX0051 ([Packages](../tour/packages)).
- The package index is at `https://packages.nanyx.dev/`, where each package has a page with its versions, README and
  install line ([Packages](../tour/packages)).
- The formatter breaks a long signature's contexts, parameters and result union over lines
  ([Formatter](../standard-tools/formatter)).
- A type is displayed with a union alias's name when it holds every tag of the alias: `#some(string) | FsError`.
- Unused and unreachable code is drawn faint in the editor ([Official linter](../standard-tools/official-linter)).
- Building the compiler from source no longer needs Rust: the componentizer is committed.

**Performance**

- A long `else if` chain is checked and compiled in time that grows with its length: 10,000 branches take about
  10 seconds to run, from about 2 minutes. Long chains no longer overflow the stack in the backend, the WAT
  assembler or `nanyx parse`.

**Fixes**

- Walking a recursive type's description reads the right payload; a match on a `Type` value tells `set(int)` from
  `list(int)`; `map.map` compiles at a type known only at run time; and a `[Typed(a)]` function whose result names a
  type of an import under an alias finds `Typed` ([Functions](../tour/functions#type-arguments)).
- `dbg` of an open union prints it, and an error in a `dbg` printer is reported at the `dbg`.
- A private non-record type can be unwrapped by pattern inside a `module` block.

# 0.34.0

*22 November 2026*

A release that brings the playground up to date with the tour, which now uses `is` anywhere. It also finishes WASI 0.3
components for both of Nanyx's hosts, lets a program export functions of its own as a component, and gives every
collection the same `at`.

**Language**

- `x is <pattern>` is a `bool` expression anywhere, not only in an `if` condition, and the names a pattern binds are in
  scope for the rest of an `and` chain: `x is #some(v) and v > 3`. `is` binds tighter than `and` and `or`, and `not`
  tighter than `is`, so a negated test is written `not (x is P)` ([Pattern matching](../tour/pattern-matching)).

**Standard library**

- `at` is the indexed read on every collection, and gives `#some(a) | #notFound`. **This breaks code** that calls
  `list.get` or `seq.get`, now `list.at` and `seq.at`, or that matches `#none` or `#outOfRange` from an `at`
  ([Lists](../stdlib/prelude-lists)).
- `set` and `map` get `any`, `all` and `filterMap`, and `set` gets `select` ([Maps and sets](../stdlib/prelude-maps)).
- `list.toList`, `list.fromList`, `seq.fromList` and `seq.fromArray` ([Lists](../stdlib/prelude-lists)).

**Tools**

- `nanyx build` writes an HTTP-host program as a `wasi:http/service` component, and a console program that uses
  `Http` as a component of `nanyx:console/command`, which adds `wasi:http/client` to `wasi:cli/command`
  ([Hosts](../advanced/hosts)).
- A program can export functions of its own from its component: `@component export def` puts a def in the WIT
  interface its module names, with numbers, strings, records, tuples, lists and tag unions crossing the boundary, and
  `nanyx build` writes the WIT to `out/wit/` ([Attributes](../tour/attributes)).
- A shipped host library, `nanyx.wasi`, holds the WASI code the console and HTTP hosts share; a host's package and
  the host libraries it depends on may use `HostRuntime` ([Hosts](../advanced/hosts)).
- The tour's examples that only declare things no longer show a Run button, only "Open in the playground".

# 0.33.0

*22 November 2026*

A release about running Nanyx in more places. The docs site gets a playground, and the tour's examples run and can be
edited where they are. `nanyx build` writes a WASI 0.3 component for a console program, which runs on any host that
runs WASI 0.3, such as `wasmtime run`. Packages build faster from the cache, and `nanyx publish` is safer to run again.

**Language**

- A range works for any type with an `Enumerable` (its `succ`) and an `Ord`: `a..b` is no longer only for `int`s
  ([Ranges](../tour/ranges)).
- An exported scoped context is written `export scoped context`. **This breaks code** that writes
  `scoped export context`: it is now `NYX0050`, with a fix that rewrites it, and `nanyx format` rewrites it too
  ([Errors](../standard-tools/errors)).

**Tools**

- The [playground](../standard-tools/playground) at `/playground`: an editor, Run and Stop, output that keeps each
  run, every tour example in its Examples menu, input from a line in the page, and share links. Each runnable example
  in the tour has a Run button, can be edited in place, and opens in the playground.
- `nanyx build` writes a WASI 0.3 component of `wasi:cli/command` for a program under the console host, with its
  `Console`, `Env`, `Clock`, `Random`, `Async` and `FileSystem` on the standard `wasi:*` interfaces; `--core` writes a
  core module as before. `nanyx run`, `test`, the debugger and the profiler are unchanged
  ([Command line](../standard-tools/command-line), [Hosts](../advanced/hosts)).
- A package from the cache is checked once and its checked modules kept in the cache, so a later build reads them
  instead of checking the package again.
- `nanyx pack` gives the same bytes on every machine, whatever its line endings; a file that isn't UTF-8 is
  `NYX0049`. `nanyx publish` checks access before it makes a release, registers through a branch of the index when it
  can push to it, and run again finishes a publish that stopped half way ([Command line](../standard-tools/command-line)).
- An unknown record field suggests the closest one, with a fix: `'lsat' is not a field of P: did you mean 'last'?`
  ([Errors](../standard-tools/errors)).
- The official linter's new `unusedContexts` rule warns on a context a def requires but doesn't use, with a fix, and
  the editor draws it faint ([Official linter](../standard-tools/official-linter)).
- The language server marks a name it can't resolve with an `unresolved` semantic token, drawn in the theme's error
  colour.

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
  ([Reference counting](../deep-dives/reference-counting)).
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
