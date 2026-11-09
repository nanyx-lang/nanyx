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

The WebAssembly backend allocates from a bump allocator: `$heap_ptr` starts after the data segment and moves
forward on every allocation, growing linear memory when it runs out (`__nanyx_alloc` in
`compiler/Nanyx.Compiler.Wasm/CodeGen.fs`). Nothing is freed, so a long-running program's memory only grows.

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

Replace the bump allocator with one that can free: segregated free lists by size class, falling back to bumping
`$heap_ptr` (and growing memory) when a class's list is empty. Most objects are a handful of words, so a few small
classes cover them; larger objects (long strings, wide records) take a general free list.

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
   - Replace the bump allocator with one that can free
     ([#292](https://github.com/nanyx-lang/nanyx/issues/292)). Independent of the rest.
2. **Core IR and monomorphisation.** Introduce the IR, specialising generic functions as it is built, and move code
   generation onto it with no change in behaviour ([#293](https://github.com/nanyx-lang/nanyx/issues/293)).
3. **Headers and drop functions.** Give every heap object a count, and generate a drop function per type
   ([#291](https://github.com/nanyx-lang/nanyx/issues/291)). Needs step 2.
4. **Reference counting.** Insert `dup` and `drop` with inferred ownership. Programs now free memory
   ([#294](https://github.com/nanyx-lang/nanyx/issues/294)).
5. **Reuse.** Drop specialisation and reuse tokens; measure on the examples
   ([#295](https://github.com/nanyx-lang/nanyx/issues/295)).

## Open questions

- **Overflow.** Does integer arithmetic wrap, as WebAssembly's does, or trap? Open in
  [Sized numbers](../../docs/src/content/work-in-progress/sized-numbers.md).
- **Code size.** Monomorphisation multiplies generic functions; how much does that grow the examples, and when
  should the boxing fallback kick in?
- **Should reuse be visible?** Koka's `fip` annotation lets a function promise it runs fully in place, and the
  compiler checks it. That would suit Nanyx's stance on predictable performance, but is not needed to start.
- **Measuring.** What benchmark shows the benefit: peak memory on the examples, a long-running loop, or both?
