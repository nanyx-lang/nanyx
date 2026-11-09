# Memory management: Perceus reference counting

> **Status: proposed.** This page designs how compiled Nanyx programs free memory. It is part of the
> [compilation](compilation.md) workstream, and its issues carry the `compilation` and `wasm backend` labels.
> Tracked in [#296](https://github.com/nanyx-lang/nanyx/issues/296).

## Summary

Compiled programs never free anything today. This proposes reference counting in the style of
[Perceus](https://www.microsoft.com/en-us/research/publication/perceus-garbage-free-reference-counting-with-reuse/)
(Reinking, Xie, de Moura and Leijen, PLDI 2021), the scheme Koka uses and close to what Lean 4 does
([Counting Immutable Beans](https://arxiv.org/abs/1908.05647), Ullrich and de Moura, 2019). The compiler inserts
precise `dup` and `drop` operations, so an object is freed as soon as nothing refers to it. When a function takes
apart a value it holds the only reference to and builds a new one of the same size, the old memory is reused in
place. Generic code is monomorphised, so every object's layout is known when it is compiled.

## Where we are today

The WebAssembly backend's allocator can free (`__nanyx_alloc` and `__nanyx_free` in
`compiler/Nanyx.Compiler.Wasm/CodeGen.fs`; see Allocator below), but nothing calls `free` yet, so a long-running
program's memory only grows.

Every value is an `i32`. On the heap:

| Value | Layout |
|-------|--------|
| String | UTF-8 bytes, the byte length in the 4 bytes before the pointer, a 0 byte after |
| List | 0 for the empty list, else a pointer to an 8-byte cell of (head, tail) |
| Record, tuple | a 4-byte slot per field in field-name order, the layout's number in the 4 bytes before |
| Closure | the function's table slot, then the captured values |
| Float | an 8-byte box holding the `f64` |
| Tag with a payload | a pointer to an 8-byte cell of (code, payload) |

String literals live in the data segment. A tag without a payload is not on the heap: it is its code,
`(id << 1) | 1`. Codes are odd and allocations are 4-byte aligned, so a tag value is a code exactly when its low
bit is set ([#181](https://github.com/nanyx-lang/nanyx/issues/181)).

Code generation goes straight from the parser's `Expression` tree to WebAssembly text, with types looked up from
the type checker's results on the side. Generic functions are compiled once, over this uniform `i32`
representation.

## Why Perceus

- **It fits the language.** Nanyx values are immutable (a record update makes a copy) and there are no mutable
  references, so the heap cannot form cycles: reference counting alone frees everything.
- **It keeps performance predictable,** as the [principles](../../docs/src/content/foundations/principles.md)
  ask. Memory is freed at a point the program determines, with no collector pauses.
- **Reuse turns functional updates into in-place ones.** Mapping over a list the caller no longer needs rewrites
  its cells instead of allocating new ones. Nanyx code does this a lot.

The alternative is WasmGC: describe objects as GC structs and let the host's collector free them. It is less work,
but it gives up reuse and deterministic freeing, and ties the runtime to hosts with GC support.

## Design

### Monomorphisation

`drop` has to recurse into the fields of an object it frees, so it has to know which fields are pointers. In
generic code (a cons cell built by `List.map`, of type `List<a>`) nothing says whether a field is an integer or a
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
  [principles](../../docs/src/content/foundations/principles.md) warn about.
- **Reuse gets better.** Perceus reuses a cell only for an allocation of the same size, and every size is now a
  compile-time constant.
- **It is what the language already describes.** Generic contexts such as `Num(a)` are synthesized when a generic
  function is called at a concrete type ([contexts](../../docs/src/content/advanced/contexts.md)). A function that
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

### Value representation

With every type known, values no longer have to fit one `i32`. Integers, floats and booleans are stored unboxed at
their own width, in locals, fields and payloads alike, and a record's fields get a layout of their own (no longer a
4-byte slot each). Floats stop being boxed.

`int` is a 64-bit integer on every target, and the language adds numbers of other fixed widths (`I32`, `U8`, `F32`
and so on; see [Sized numbers](../../docs/src/content/work-in-progress/sized-numbers.md)). Each maps to the Wasm
type of its width: `I64` and `int` to `i64`, `F64` and `float` to `f64`, the narrower integers to `i32` in locals and
to their own width in fields.

Pointers stay 32-bit: linear memory stays `memory32`. A 64-bit address space (Wasm `memory64`) only matters past
4 GB of heap, and would double the size of every pointer field.

### Object header

Every heap object gets a reference count in the word before its existing info word, so a pointer `p` has its count
at `p - 8`. Objects that have an info word keep it at `p - 4` (a string's length, a record's layout). The drop
function for the object's type knows the rest, so no other run-time layout information is needed.

Static data (string literals) gets a header too, with a count that marks it as immortal: `dup` and `drop` leave it
alone and it is never freed.

### Allocator

Segregated free lists by size class, falling back to bumping `$heap_ptr` (and growing memory) when a class's list
has nothing to give ([#292](https://github.com/nanyx-lang/nanyx/issues/292), done). Most objects are a handful of
words, so a few small classes cover them; larger objects (long strings, wide records) take a general free list.

- **Sizes.** `alloc(n)` rounds `n` up to a multiple of 4 (and at least 4), so blocks stay 4-byte aligned and
  pointers stay even.
- **Size word.** A block's rounded size is stored in the word just before the block `alloc` returns, so
  `free(b)` needs only the pointer. The object header is part of the block: once objects have one, a block holds
  the count, then the info word (if any), then the fields, so the size word sits just before the count. For a
  string, say, that puts the count at `p - 8`, the length at `p - 4`, and the size at `p - 12`. Until headers
  land, the size word is at `p - 8` for strings and records and `p - 4` for the rest.
  The drop functions will know each object's size from its type, so the size word could later go; it costs 4
  bytes per object but keeps `free` independent of types, and lets the large list hand out a bigger block than
  asked for and still take all of it back.
- **Classes.** Each size from 4 to 32 bytes (8 words) has its own list, so a freed block goes back to exactly the
  size Perceus will next ask for. Larger blocks share one list, searched first-fit; a block found there is not
  split and keeps its own size.
- **Lists.** The heads are a table of 9 words at the start of the heap (address 4096), and `$heap_ptr` starts
  after it. A free block keeps the next block of its list in its first word. `free` pushes onto the front of the
  list and `alloc` takes from the front, so the most recently freed block is reused first.

Nothing coalesces adjacent free blocks or returns memory to the host; neither matters for the small,
same-sized objects that dominate, and both can come later if measurements on the examples call for them.

### Core IR

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
a body. Types are concrete (`int`, `bool`, `unit`, `string`, a union of tags, a record, a list), with no type
variables. A tuple is a record whose fields are its positions, and a nominal type is the type of its shape. A body is
an expression with a type on every node:

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
| `let x = value in body` | names a value; the value is never a `let` itself |
| `do value; rest` | evaluates a value for its effects and drops it |
| `if atom then e1 else e2` | |
| `match x1, ..., xn \| arms` | tests variables against each arm's patterns (`_`, a literal, a tag with a payload pattern, a tuple, a list, a nominal type's shape, a binding) |

Every operand is an atom, so every intermediate value has a name. Each variable says what kind it is: a parameter, a
local the program declares, a scrutinee, a name a pattern binds, or a temporary (a value the source does not name).

**Lowering.** `CoreIrLower.fs` lowers each definition from the typed module: its expressions as the backend sees
them, with each node's type from `ExprTypes` and the type of the function at each call from `CalleeTypes`. A
definition whose type is concrete is lowered as it is. A call to a generic definition binds the definition's type
variables to the types at the call, and asks for the specialisation at those type arguments (`id__int`,
`id__string`), which is lowered in turn with the variables replaced. Each specialisation is lowered once, however
many calls ask for it, and a definition can have at most 32 (polymorphic recursion would otherwise never end).
A definition that reads fields of any record that has them is specialised per record layout, so each specialisation
reads its fields from where they are stored rather than looking them up at run time (`__nanyx_open_field`).

**Generating code.** `CoreIrEmit.fs` writes WebAssembly text from the IR, still with every value an `i32` and laid out
as `CodeGen.fs` lays it out. A temporary is not stored: it is used exactly once, by the next instruction that needs
it, so its value is computed where it is used. When a local is stored in between (a `match` among a call's arguments,
say), the temporaries waiting are computed first and left on the stack. Record layouts are numbered as functions are
emitted, so functions from the IR are emitted in definition order with the rest, to number layouts as the old path does.

**Falling back.** Until the IR covers the whole language, a definition is compiled from the IR when it can be, and
the old way (`CodeGen.fs` straight from the syntax tree) when not:

- A definition falls back if anything in it is beyond the IR (closures, interpolated strings, `dbg`, pipes,
  contexts and so on), if it requires contexts, or if it calls a specialisation that could not be lowered. A
  function and the functions it calls through the IR are kept or dropped together.
- A generic definition is always compiled the old way too, unspecialised, for the callers that fell back. Callers
  compiled from the IR call its specialisations instead.
- Both ways give the same calling convention, so a function from either can call one from the other. A function from
  the IR compiles to the same text the old way would give it, apart from calling specialisations, and a test holds
  them to that.
- Only a type-checked module is lowered: `transpileModuleToWat`, which compiles an untyped module, uses the old path
  throughout. `transpileTypedModuleWithReport` says which way each function was compiled, and why a definition fell
  back.

The fallback goes once the IR lowers every construct; the remaining constructs and its removal are tracked from
[#293](https://github.com/nanyx-lang/nanyx/issues/293).

### Inserting counts

On the IR, following the paper:

1. **Ownership.** A function either owns a parameter (and must consume or drop it) or borrows it (the caller keeps
   it alive). Parameters are inferred as borrowed when the function only reads them.
2. **`dup` and `drop`.** Inserted by liveness: a variable is dropped where it dies, on every branch, and duplicated
   when it is used again after being passed to an owning call.
3. **Drop specialisation.** A `drop` right after a `match` that took the value apart becomes a check: if the count
   is one, free the cell and drop only the fields not used; otherwise decrement.
4. **Reuse.** When a dropped cell is the same size as one allocated later on the same path, the drop becomes a
   reuse token and the allocation writes into it when the count was one.

### Things that need care

- **Recursive local closures.** A local def that calls itself is bound to a closure that captures itself, the one
  cycle the language can make. It has to be lifted so that it refers to itself directly rather than through its
  environment.
- **Closures and contexts.** Capturing a value duplicates it, and freeing a closure drops what it captured. Context
  members are passed as closures and follow the same rules.
- **Host calls.** Strings handed to the runtime (stdout, `dbg`) are borrowed for the call.
- **Tail calls.** The language guarantees that a tail call uses no stack ([tail-calls.md](tail-calls.md)), so a
  variable that dies at a tail call is dropped before the call, and an argument the caller owns is passed owned:
  if the callee only borrowed it, the caller would have to outlive the call.

## Plan

1. **Groundwork.**
   - ~~Keep a tag's payload in full~~ ([#181](https://github.com/nanyx-lang/nanyx/issues/181), done).
   - ~~Replace the bump allocator with one that can free~~
     ([#292](https://github.com/nanyx-lang/nanyx/issues/292), done).
2. **Core IR and monomorphisation.** Introduce the IR, specialising generic functions as it is built, and move code
   generation onto it with no change in behaviour ([#293](https://github.com/nanyx-lang/nanyx/issues/293)).
3. **Headers and drop functions.** Give every heap object a count, and generate a drop function per type
   ([#291](https://github.com/nanyx-lang/nanyx/issues/291)). Needs step 2.
4. **Reference counting.** Insert `dup` and `drop` with inferred ownership. Programs now free memory
   ([#294](https://github.com/nanyx-lang/nanyx/issues/294)).
5. **Reuse.** Drop specialisation and reuse tokens; measure on the examples
   ([#295](https://github.com/nanyx-lang/nanyx/issues/295)).

## Open questions

- **Code size.** Monomorphisation multiplies generic functions; how much does that grow the examples, and when
  should the boxing fallback kick in?
- **Should reuse be visible?** Koka's `fip` annotation lets a function promise it runs fully in place, and the
  compiler checks it. That would suit Nanyx's stance on predictable performance, but is not needed to start.
- **Measuring.** What benchmark shows the benefit: peak memory on the examples, a long-running loop, or both?
