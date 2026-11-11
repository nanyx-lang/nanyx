---
title: "Memory management"
description: "How the compiler inserts Perceus reference counting, lays values out and reuses memory"
order: 25
---

> **Status: implemented** in the WebAssembly backend. This page designs how compiled Nanyx programs free memory. It
> is part of the [compilation](./compilation) workstream, and its issues carry the `compilation` and `wasm backend`
> labels. Tracked in [#296](https://github.com/nanyx-lang/nanyx/issues/296). The user-facing account is
> [Reference counting](../foundations/reference-counting).

# Summary

Compiled programs free memory by reference counting in the style of
[Perceus](https://www.microsoft.com/en-us/research/publication/perceus-garbage-free-reference-counting-with-reuse/)
(Reinking, Xie, de Moura and Leijen, PLDI 2021), the scheme Koka uses and close to what Lean 4 does
([Counting Immutable Beans](https://arxiv.org/abs/1908.05647), Ullrich and de Moura, 2019). The compiler inserts
precise `dup` and `drop` operations, so an object is freed as soon as nothing refers to it. When a function takes
apart a value it holds the only reference to and builds a new one of the same size, the old memory is reused in
place. Generic code is monomorphised, so every object's layout is known when it is compiled.

# Where we are today

The WebAssembly backend's allocator can free (`__nanyx_alloc` and `__nanyx_free` in
`compiler/Nanyx.Compiler.Wasm/WasmRuntime.fs`; see Allocator below), every object on the heap has a reference count
and a drop function for its type (see Object header below), and the compiler inserts `dup` and `drop` (see
Inserting counts below), so a program frees each object once nothing refers to it, and reuses an object it takes
apart and holds the only reference to for a new one of the same size.

Every value is an `i32`. On the heap, after each object's header:

| Value | Layout |
|-------|--------|
| String | UTF-8 bytes, the byte length in the 4 bytes before the pointer, a 0 byte after |
| List | 0 for the empty list, else a pointer to an 8-byte cell of (head, tail) |
| Record, tuple | a 4-byte slot per field in field-name order, the layout's number in the 4 bytes before |
| Closure | the function's table slot, then the captured values; the table slot of its environment's drop function in the 4 bytes before |
| Float | an 8-byte box holding the `f64` |
| Tag with a payload | a pointer to an 8-byte cell of (code, payload) |

String literals live in the data segment, with a header of their own. A tag without a payload is not on the heap: it is its code,
`(id << 1) | 1`. Codes are odd and allocations are 4-byte aligned, so a tag value is a code exactly when its low
bit is set ([#181](https://github.com/nanyx-lang/nanyx/issues/181)).

Code generation goes from the typed module through the core IR (see Core IR below) to WebAssembly text. Generic
functions are specialised for each set of type arguments they are used at, though every value is still an `i32`.

# Why Perceus

- **It fits the language.** Nanyx values are immutable (a record update makes a copy) and there are no mutable
  references, so the heap cannot form cycles: reference counting alone frees everything.
- **It keeps performance predictable,** as the [principles](../foundations/principles)
  ask. Memory is freed at a point the program determines, with no collector pauses.
- **Reuse turns functional updates into in-place ones.** Mapping over a list the caller no longer needs rewrites
  its cells instead of allocating new ones. Nanyx code does this a lot.

The alternative is WasmGC: describe objects as GC structs and let the host's collector free them. It is less work,
but it gives up reuse and deterministic freeing, and ties the runtime to hosts with GC support.

# Design

## Monomorphisation

`drop` has to recurse into the fields of an object it frees, so it has to know which fields are pointers. In
generic code (a cons cell built by `list.map`, of type `List<a>`) nothing says whether a field is an integer or a
pointer.

**Decision: monomorphise.** The core IR compiles every generic function once for each set of concrete type
arguments it is used at, so every value's type, and so every object's layout, is known at compile time. Each type
gets a generated drop function (`drop` of a `List<string>` drops each head as a string and each tail as a list).
Nothing at run time has to say what a field is.

How other languages answer this, and why we chose this way:

| Approach | Used by | Cost |
|----------|---------|------|
| Tag scalars in the low bit | OCaml, Erlang | 31-bit (or 63-bit) integers; a shift on arithmetic |
| Box scalars where they cross into generic code | Koka, Lean 4, GHC | hidden allocations when a value passes through a generic function |
| Pass type information to generic functions | Swift | an extra argument on every generic call |
| Monomorphise | Rust, MLton, Roc | code size and compile time grow with the number of instantiations |

- **Integers keep their full width.** Tagging would change what `int` means.
- **No hidden boxing,** which is the kind of invisible cost the
  [principles](../foundations/principles) warn about.
- **Reuse gets better.** Perceus reuses a cell only for an allocation of the same size, and every size is now a
  compile-time constant.
- **It is what the language already describes.** Generic contexts such as `Num(a)` are synthesized when a generic
  function is called at a concrete type ([contexts](../advanced/contexts)). A function that
  reads fields of any record that has them is specialised per record layout, which also removes the run-time field
  lookup (`__nanyx_open_field`).
- **The compiler already sees the whole program.** The definitions a module reaches in the modules it imports (the
  prelude included) are inlined into it before code generation (`Inlined` in `Library.fs`), so every generic
  function's body is available to specialise. Roc, the closest language to Nanyx
  (pure, reference counted with in-place reuse, compiling to WebAssembly), takes the same route.

Tags keep their odd codes: a value of type `#some(string) | #none` is still a code or a cell, decided at run time,
and its drop function checks the low bit before reading the cell.

If code size becomes a problem, the fallback is Koka and Lean's: box values only at the generic boundaries that are
not specialised. Monomorphising first does not rule that out.

## Value representation

With every type known, values no longer have to fit one `i32`. Integers, floats and booleans are stored unboxed at
their own width, in locals, fields and payloads alike, and a record's fields get a layout of their own (no longer a
4-byte slot each). Floats stop being boxed.

`int` is a 64-bit integer on every target, and the language adds numbers of other fixed widths (`I32`, `U8`, `F32`
and so on; see [Sized numbers](./sized-numbers)). Each maps to the Wasm
type of its width: `I64` and `int` to `i64`, `F64` and `float` to `f64`, the narrower integers to `i32` in locals and
to their own width in fields.

Pointers stay 32-bit: linear memory stays `memory32`. A 64-bit address space (Wasm `memory64`) only matters past
4 GB of heap, and would double the size of every pointer field.

## Object header

Every heap object has a reference count in the word before its info word, so a pointer `p` to an object with one has
its count at `p - 8` and the info word at `p - 4`: a string's length, a record's layout, and a closure's environment
drop function (below). An object with no info word (a list cell, a tag's cell, a float's box) has its count just
before its fields, at `p - 4`. Every `dup` and `drop` knows the type of what it counts, so it knows which; giving
the commonest objects, list cells, an empty info word would make each 20 bytes rather than 16 (with the
allocator's size word). The drop function for the object's type knows the rest, so no other run-time layout
information is needed. `__nanyx_new_object` and `__nanyx_new_cell` allocate an object of each kind with a count of
one, and `__nanyx_release` gives up a reference, telling the caller whether it was the last.

Static data (string literals) gets a header too, with a count of 0, which marks it as immortal: `release` leaves it
alone, so it is never freed. A literal in the data segment is laid out as a string on the heap is (the count, the
length, the bytes and a 0 byte), padded to 4 bytes so that the next one is aligned.

**Drop functions.** `CoreIrDrop.fs` generates one for each type on the heap that the module's functions use
(`__nanyx_drop_list_string`, named by the type), together with the types of their fields. Each gives up a reference
and, when it was the last, drops the fields that are on the heap and frees the object:

- a list's walks along the list cell by cell, as long as each cell was held only by the one before it, so dropping
  a long list takes no stack;
- a tag union's returns at once for a code (an odd value); for a cell, it reads the code to tell the payload's type,
  unless every tag with a payload has a payload of the same type;
- a function value's calls the function whose table slot is in the closure's info word, which drops what the
  closure captures. There is one such function per list of captured types (`__nanyx_drop_env__string`), and one,
  `__nanyx_drop_env`, for closures that capture nothing on the heap. It is called through the function table, so it
  has the type of a closure of one parameter, and returns 0.

## Allocator

Segregated free lists by size class, falling back to bumping `$heap_ptr` (and growing memory) when a class's list
has nothing to give ([#292](https://github.com/nanyx-lang/nanyx/issues/292), done). Most objects are a handful of
words, so a few small classes cover them; larger objects (long strings, wide records) take a general free list.

- **Sizes.** `alloc(n)` rounds `n` up to a multiple of 4 (and at least 4), so blocks stay 4-byte aligned and
  pointers stay even.
- **Size word.** A block's rounded size is stored in the word just before the block `alloc` returns, so
  `free(b)` needs only the pointer. The object header is part of the block: once objects have one, a block holds
  the count, then the info word (if any), then the fields, so the size word sits just before the count. For a
  string, say, that puts the count at `p - 8`, the length at `p - 4`, and the size at `p - 12`; for a list cell,
  the count at `p - 4` and the size at `p - 8`.
  The drop functions will know each object's size from its type, so the size word could later go; it costs 4
  bytes per object but keeps `free` independent of types, and lets the large list hand out a bigger block than
  asked for and still take all of it back.
- **Classes.** Each size from 4 to 32 bytes (8 words) has its own list, so a freed block goes back to exactly the
  size Perceus will next ask for. Larger blocks share one list, searched first-fit; a block found there is not
  split and keeps its own size.
- **Lists.** The heads are a table of 9 words at the start of the heap, and `$heap_ptr` starts after it. The
  string literals run from address 256, and the table goes at 4096 or, when they end past that, after them,
  8-byte aligned (`heapBaseAfter`); the module starts with enough pages of memory to hold them. A free block keeps the next block of its list in its first word. `free` pushes onto the front of the
  list and `alloc` takes from the front, so the most recently freed block is reused first.

Nothing coalesces adjacent free blocks or returns memory to the host; neither matters for the small,
same-sized objects that dominate, and both can come later if measurements on the examples call for them.

## Core IR

Perceus works per variable: it inserts a `drop x` after the last use of `x`, and a `dup x` before every use but the
last. That needs a program where every intermediate value has a name and a definite position. A new core IR sits
between the typed AST and WebAssembly text:

```
source → AST → typed AST → core IR → WAT
```

It has few constructs (`let`, call, `match`, allocate, read field, and the like), in A-normal form, with a
concrete type on every node: generic functions are specialised as the IR is built (see Monomorphisation). Pipes,
interpolation, `if ... is`, record updates, workflows and context calls are lowered into it. Code generation then
works from the IR. This is worth doing for its own sake: later optimisations, a second
backend and a simpler `CodeGen.fs` all benefit.

**Shape.** The IR is in `compiler/Nanyx.Compiler.Wasm/CoreIr.fs`. A function has typed parameters, a result type and
a body. Types are concrete (`int`, `bool`, `unit`, `string`, `float`, a union of tags, a record, a list, a function),
with no type variables. A tuple is a record whose fields are its positions, and a nominal type is the type of its
shape. A body is an expression with a type on every node:

| Node | Meaning |
|------|---------|
| atom | a variable or a constant (an int, a bool, unit, a string literal, a tag without a payload) |
| `prim(op, atoms)` | an operator on ints or booleans |
| `call(f, atoms)` | a call to a top-level function, by its name in WebAssembly text |
| `makeTag(tag, atom)` | allocates the cell of a tag with a payload |
| `makeRecord(f1 = atom, ...)` | allocates a record, its fields in the order they are stored |
| `field(atom, slot)` | reads the field stored in a slot of a record |
| `updateRecord(atom, slot = atom, ...)` | copies a record with some fields replaced (`record with f = v`) |
| `makeList(atoms)` | builds a list |
| `callRuntime(helper, atoms)` | calls a runtime helper: an intrinsic, or what `++` and `==` on strings, lists and tags need |
| `makeClosure(f, atoms)` | allocates a closure block: the lifted function's table slot, then what it captures |
| `callClosure(closure, atoms)` | calls through a function value, passing the closure block as the environment |
| `boxFloat(value)` | boxes a float constant (floats are boxed, and `prim` on floats unboxes its operands) |
| `forEach(loop)` | a `for` loop over lists and ranges in step, its body run for each step until one of them ends |
| `panic(messages)` | an error deferred to run time: writes the messages to stderr and ends the program |
| `interpolate(parts)` | builds an interpolated string from its text and its holes, each already stored in its hole local |
| `dbg(atom, form)` | prints a value as `dbg` does for its type, and leaves it |
| `let x = value in body` | names a value; the value is never a `let` itself |
| `do value; rest` | evaluates a value for its effects and drops it |
| `if atom then e1 else e2` | |
| `match x1, ..., xn \| arms` | tests variables against each arm's patterns (`_`, a literal, a guard such as `> 5`, a tag with a payload pattern, a tuple, a list, a nominal type's shape, a binding) |

Every operand is an atom, so every intermediate value has a name. Each variable says what kind it is: a parameter, a
local the program declares, a scrutinee, a name a pattern binds, or a temporary (a value the source does not name).

**Lowering.** `CoreIrLower.fs` lowers each definition from the typed module: its expressions as the backend sees
them, with each node's type from `ExprTypes` and the type of the function at each call from `CalleeTypes`. A
definition whose type is concrete is lowered as it is. A call to a generic definition binds the definition's type
variables to the types at the call, and asks for the specialisation at those type arguments (`id__int`,
`id__string`), which is lowered in turn with the variables replaced. Each specialisation is lowered once, however
many calls ask for it, and a definition can have at most 32 (polymorphic recursion would otherwise never end).
A type variable that nothing constrains (the element of `[]`, the rest of an open union no use closes) is `unit`:
nothing can make or inspect a value of such a type.
A definition that reads fields of any record that has them is specialised per record layout, so each specialisation
reads its fields from where they are stored. A generic definition exists only as its specialisations: it is neither
compiled nor exported as it is.

A definition inlined from an imported module (the prelude's `list.map`, say) is specialised the same way. Only the root
module's types by node are resolved for the editor's sake, so the types of the inlined definitions' nodes are resolved
apart, each by the module it came from, and only the IR reads them (`TypedModule.InlinedExprTypes`).

Lambda lifting still runs on the syntax tree first (`LambdaLifting.fs`): a lambda becomes a top-level function that
takes a closure block as its environment, and the lambda is replaced by a closure over it. The IR lowers a lifted
function where a closure over it is made, since only there are the types of what it captures known. A lambda written
in a generic definition is specialised along with it (`__lambda_0__int`), and the wrapper that lets a top-level
function be passed as a value is lowered for each type it is passed at. Lifting lambdas in the IR instead is
[#425](https://github.com/nanyx-lang/nanyx/issues/425).

How a function value is called follows from its type, not from how its lambda names its parameters or how the call
writes its argument, since `int, int -> int` and `(int, int) -> int` are one type. A function value whose input is a
tuple of two or more takes one argument for each element (`IrType.tupleElements` in `CoreIr.fs`): a lambda or wrapper of
one parameter (`{ t -> ... }`, `{ (a, b) -> ... }`) takes the elements and builds the tuple again, and a call that
passes a tuple whole (`f(t)`) reads the elements from it ([#441](https://github.com/nanyx-lang/nanyx/issues/441)).

A function that requires contexts takes each of their members as a closure, in a parameter of its own after its
others, typed from the context's definition. A call finds each member the callee requires where the type checker
found its context (a `use`, the caller's own members, an instance built from attached definitions, or the ambient
`Console`). A lambda of no parameters is lifted like any other, so `def block = { ... }` is a function to call. A workflow block that uses a keyword is not a lambda by then: desugaring leaves its body where it
is written.

The module's top-level expressions are lowered as one more function, the start function, which evaluates each in
turn and drops its value. A module with type or name errors is compiled all the same in a debug build: what failed to
check is a `panic` that reports the error when it is reached, and a value of no known type because of it has a type of
its own (still an `i32`). Matching on or interpolating such a value stops the program there, if the error has not
already. A definition (or top-level expression) that holds such errors and cannot be lowered because of them is
lowered to a trap that reports them as soon as it is called. A definition that is generic only because an error left
its type open, and is not specialised, is lowered with its type variables defaulted, so that it still exists.

**Generating code.** `CoreIrEmit.fs` writes WebAssembly text from the IR, still with every value an `i32` and laid out
as `CodeGen.fs` lays it out. A temporary is not stored: it is used exactly once, by the next instruction that needs
it, so its value is computed where it is used. An operand that stores to a local on its way to its value (a `match`
or an interpolated string among a call's arguments, say) is one temporary whose value is the whole sequence, stores
and all, so nothing waits on the stack across a store. Record layouts and function table slots are numbered as
functions are emitted, in definition order, each definition followed by its specialisations. What the module needs
besides its functions comes from the IR of exactly the functions it emits (`CoreIrFacts.fs`): the tags it numbers
(sorted by name, so every case of a tag type a hole prints has one), the string literals in its data segment, and the
host imports and helpers `dbg` calls. The heap is set up when some function calls `$alloc`.

**What cannot be compiled.** Every function is compiled from the IR; there is no other way. What the IR cannot lower
(a local function, `==` on records and lists, `dbg` of a record, and the like) is an error,
`UnsupportedByBackend`, reported where it is written: `tryTranspileTypedModuleToWat` gives the first one. `==` is built
in only for numbers, strings and tags; comparing and printing records and lists structurally is
[#424](https://github.com/nanyx-lang/nanyx/issues/424). Only a type-checked module is compiled.
`transpileTypedModuleWithReport` also lists the functions it was compiled into, specialisations among them.

## Inserting counts

On the IR, following the paper:

1. **Ownership.** A function either owns a parameter (and must consume or drop it) or borrows it (the caller keeps
   it alive). Parameters are inferred as borrowed when the function only reads them.
2. **`dup` and `drop`.** Inserted by liveness: a variable is dropped where it dies, on every branch, and duplicated
   when it is used again after being passed to an owning call.
3. **Drop specialisation.** A `drop` right after a `match` that took the value apart becomes a check: if the count
   is one, free the cell and drop only the fields not used; otherwise decrement.
4. **Reuse.** When a dropped cell is the same size as one allocated later on the same path, the drop becomes a
   reuse token and the allocation writes into it when the count was one.

Steps 1 and 2 are `CoreIrCounting.fs`, run on every function of the module after it is lowered, which adds three
nodes to the IR: `dup x1, ...; e`, `drop x1, ...; e`, and `e then drop x1, ...` (drop once `e` is done).
`CoreIrDrop.fs` generates the dup and drop function of each type.

- **Every variable owns its reference,** except a parameter its function borrows, a name a pattern binds that the
  arm does not use, and, inside a `for` loop's body or a guard, every variable from outside it. Each instruction
  either takes an operand over (a call that owns the parameter, a field of a new object, a value returned, `dbg`)
  or borrows it (a field read, an operator, a runtime helper): a variable is duplicated where it is taken over and
  still used after, and dropped after the instruction that used it last if that one only borrowed it.
- **Temporaries are counted where they are used.** A temporary's value is computed where it is used (see Generating
  code), so its value is analysed there, in the order the operands are evaluated. A temporary an instruction only
  borrows is kept in a local as it is pushed (`local.tee`), and dropped after the instruction.
- **Drops happen before a store.** What dies in a `let`'s value is dropped as the value is computed, before it is
  stored, so a local the variable shares with another of the same name (see the IR's `VarKind`) still holds it.
- **Matching.** An arm takes a reference to each name its patterns bind that it uses, then drops the scrutinee if
  it does not use it either. Reading a field takes a reference to the value read.
- **Ownership.** A function called through a closure (a lifted lambda, or the wrapper of a function passed as a
  value) owns all its parameters, the environment among them: a call through a closure passes it on owned, or a
  new reference to it if it is used again. Another function borrows a parameter on the heap it never takes over,
  and owns it otherwise; this is worked out over the whole module at once, until nothing changes.
- **Runtime helpers borrow.** Every helper, the ones that write to stdout and stderr among them, borrows its
  operands and returns a value of its own, except `list.cons` (which takes both over) and `++` on lists (which
  takes the second over, and copies the first's cells with a reference to each element). Strings handed to the
  host are borrowed for the call.
- **Tail calls.** An argument passed in a tail call that the caller owns makes the callee own that parameter, so
  nothing is left to drop after a tail call (see [Tail calls](./tail-calls)).
- **Recursive local closures** need nothing more: lambda lifting already makes a `rec` local refer to itself
  through the environment it is called with, never through what it captures, so it cannot capture itself. Lifting
  lambdas in the IR ([#425](https://github.com/nanyx-lang/nanyx/issues/425)) is not needed first.

Steps 3 and 4 are `CoreIrReuse.fs`, run on the counted IR, which adds two more nodes: `release x; e` and an
allocation `reusing` a token.

- **Drop specialisation.** An arm that takes apart one object (a list's first cell, a tag's cell, a record) whose
  fields its patterns bind or ignore, and that does not use the object itself, releases it instead of taking a
  reference to each name and dropping it. The release checks the count: when it is one, the names keep the fields'
  references, the fields no name the arm uses are dropped, and the block is freed; otherwise the names take
  references of their own and the count goes down.
- **Reuse.** When the arm then always allocates a block of the same size (not in a branch, a loop or a guard), the
  release keeps the block in a token instead of freeing it, or sets the token to 0 when the object was shared, and
  the allocation writes into the block when the token holds one (`__nanyx_list_cons_at`, `__nanyx_tag_box_at`,
  `__nanyx_reuse_object`). Mapping over a list held once rewrites its cells in place.
- **`[a, ...rest]`** is `[a] ++ rest` to the parser. It is lowered to a cell for `a` in front of `rest`, rather than
  a one-element list whose cell `++` copies, so that it allocates one cell, and can reuse one.

The tests in `Features/Memory` check that a loop over lists, records, closures and tags runs in constant memory,
that the examples that compile free every block they allocate, and, counting what the allocator hands out, that
mapping over a list, a tuple or a tag's cell held once allocates nothing.

**Memory checks.** `nanyx run --check-memory` (`WasmOptions.CheckMemory`) compiles the program with an allocator
and counts that check themselves (`checkedRuntimeHelpers` and `MemoryChecks` in `WasmRuntime.fs`):

- `alloc` and `free` count the blocks still in use. The program's start function is exported rather than run on
  instantiation, so the CLI can read the count once the program has finished. A count above 0 fails the run.
- `free` sets the low bit of a block's size word (sizes are multiples of 4), and `alloc` clears it when it hands
  the block out again. Freeing a marked block, or a `retain` or `release` of an object on the heap whose block is
  marked, writes what went wrong to stderr and exits with 70. A check uses WASI `proc_exit`, not a trap: a trap
  raised inside the Wasm test host crashes it.
- `free` fills a block after its free-list link with `0xDEADBEEF`, so a field read after the block was freed is a
  pointer far outside memory, and following it traps.

Every Wasm test that runs a program through the CLI passes `--check-memory`, and the in-process tests compile with
the checks to read the counters. So a leak, a double free or a use after free in any test program fails that test.

On the examples that compile (blocks allocated, and the heap's peak in bytes):

| Example | No counting | Counting (#294) | Reuse (#295) |
|---------|-------------|-----------------|--------------|
| `directives` | 13, 256 | 13, 136 | 13, 136 |
| `money` | 6, 120 | 6, 104 | 6, 104 |
| `shop` | 2,605, 54,868 | 2,605, 1,832 | 2,562, 1,832 |

## Things that need care

- **Recursive local closures.** A local def that calls itself would be bound to a closure that captures itself, the
  one cycle the language can make. Lambda lifting avoids it: the lifted function refers to itself through the
  closure it is called with, which it does not capture.
- **Closures and contexts.** Capturing a value duplicates it, and freeing a closure drops what it captured. Context
  members are passed as closures and follow the same rules.
- **Host calls.** Strings handed to the runtime (stdout, `dbg`) are borrowed for the call.
- **Tail calls.** The language guarantees that a tail call uses no stack ([Tail calls](./tail-calls)), so a
  variable that dies at a tail call is dropped before the call, and an argument the caller owns is passed owned:
  if the callee only borrowed it, the caller would have to outlive the call.

# Plan

1. **Groundwork.**
   - ~~Keep a tag's payload in full~~ ([#181](https://github.com/nanyx-lang/nanyx/issues/181), done).
   - ~~Replace the bump allocator with one that can free~~
     ([#292](https://github.com/nanyx-lang/nanyx/issues/292), done).
2. ~~**Core IR and monomorphisation.** Introduce the IR, specialising generic functions as it is built, and move code
   generation onto it~~ ([#293](https://github.com/nanyx-lang/nanyx/issues/293), done).
3. ~~**Headers and drop functions.** Give every heap object a count, and generate a drop function per type~~
   ([#291](https://github.com/nanyx-lang/nanyx/issues/291), done).
4. ~~**Reference counting.** Insert `dup` and `drop` with inferred ownership. Programs now free memory~~
   ([#294](https://github.com/nanyx-lang/nanyx/issues/294), done).
5. ~~**Reuse.** Drop specialisation and reuse tokens; measure on the examples~~
   ([#295](https://github.com/nanyx-lang/nanyx/issues/295), done).

# Open questions

- **Code size.** Monomorphisation multiplies generic functions; how much does that grow the examples, and when
  should the boxing fallback kick in?
- **Should reuse be visible?** Koka's `fip` annotation lets a function promise it runs fully in place, and the
  compiler checks it. That would suit Nanyx's stance on predictable performance, but is not needed to start.
- **Measuring.** Both, for now: every test program runs with the memory checks, the tests run long loops in
  constant memory, and the table above gives allocations and peak memory on the examples. The examples are small
  and mostly build strings; a benchmark that transforms large data structures would show what reuse is worth.
