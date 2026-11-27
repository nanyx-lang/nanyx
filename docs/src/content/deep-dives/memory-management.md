---
title: "Memory management"
description: "How the WebAssembly backend lays values out, inserts Perceus reference counting and reuses memory"
order: 4
---

This page says how the WebAssembly backend lays values out, counts references and reuses memory. What a user sees is
in [Reference counting](./reference-counting), and what is still to do or open is on
[Memory management](../work-in-progress/memory-management) in Work in progress.

# Summary

Compiled programs free memory by reference counting in the style of
[Perceus](https://www.microsoft.com/en-us/research/publication/perceus-garbage-free-reference-counting-with-reuse/)
(Reinking, Xie, de Moura and Leijen, PLDI 2021), the scheme Koka uses and close to what Lean 4 does
([Counting Immutable Beans](https://arxiv.org/abs/1908.05647), Ullrich and de Moura, 2019). The compiler inserts
precise `dup` and `drop` operations, so an object is freed as soon as nothing refers to it. When a function takes
apart a value it holds the only reference to and builds a new one of the same size, the old memory is reused in
place. Generic code is monomorphised, so every object's layout is known when it is compiled. The host's garbage
collector (WasmGC) is not used: it would give up reuse and deterministic freeing, and need hosts with GC support.

An `int` and a `U64` are `i64`s, a `float` an `f64`, an `F32` an `f32`, and every other value an `i32` (see [Value representation](#value-representation)).
On the heap, after each object's header:

| Value | Layout |
|-------|--------|
| String | UTF-8 bytes, the byte length in the 4 bytes before the pointer, a 0 byte after |
| List | 0 for the empty list, else a pointer to a cell of (head, tail) |
| Record, tuple | the fields one after another in field-name order, the layout's number in the 4 bytes before |
| Closure | the function's table slot, then the captured values; the table slot of its environment's drop function in the 4 bytes before |
| Tag with a payload | a pointer to a cell of (code, payload) |

String literals live in the data segment, with a header of their own. A tag without a payload is not on the heap: it is its code,
`(id << 1) | 1`. Codes are odd and allocations are 4-byte aligned, so a tag value is a code exactly when its low
bit is set ([#181](https://github.com/nanyx-lang/nanyx/issues/181)).

Code generation goes from the typed module through the [core IR](#core-ir) to WebAssembly text.

# Design

## Monomorphisation

`drop` has to recurse into the fields of an object it frees, so it has to know which fields are pointers. The core IR
compiles every generic function once for each set of concrete type arguments it is used at, so every value's type,
and so every object's layout, is known at compile time. Each type gets a generated drop function (`drop` of a
`List<string>` drops each head as a string and each tail as a list), and nothing at run time has to say what a field
is. Integers keep their full width, nothing is boxed behind the program's back, and every size Perceus reuses is a
compile-time constant. A function that reads fields of any record that has them is specialised per record layout. The
definitions a module reaches in the modules it imports (the prelude included) are inlined into it before code
generation (`Inlined` in `Library.fs`), so every generic function's body is available to specialise.

Tags keep their odd codes: a value of type `#some(string) | #none` is still a code or a cell, decided at run time,
and its drop function checks the low bit before reading the cell. A `match` reads the code once, into a local, before
its first tag arm, and each arm compares that local; a value of a union with no payloads is its own code, so a match on
one compares the value itself ([#2309](https://github.com/nanyx-lang/nanyx/issues/2309)).

If code size becomes a problem, the fallback is Koka and Lean's: box values only at the generic boundaries that are
not specialised (see [Open questions](../work-in-progress/memory-management#open-questions)).

## Value representation

Every value is held at its own width, in locals, fields and payloads alike, and no number is boxed.

An `int` is an `i64` in locals, parameters and results, and takes 8 bytes in a field, a list cell's head, a tag's
payload and a closure's captures, with the narrower sized integers in their own width and every other value in 4
(`CoreIrLayout.fs`). A local of an `int` is named with `_i64` after the variable's name, since variables of the same
kind and name share a local. The runtime helpers that store a value (`listCons`, `tagBox`, `recordSet` and the rest)
have an `_i64` variant, and a call through a closure uses a function type of the values it passes
(`Wat.indirectTypeName`).

A `U64` is held as an `int` is. `I8`, `I16`, `I32`, `U8`, `U16` and `U32` are
`i32`s in locals, sign-extended or zero-extended from their width as they are signed or not. In a field, a list
cell's head, a tag's payload and a closure's captures each takes its own width: an `I8` or a `U8` 1 byte, written
with `i32.store8` and read with `i32.load8_s` or `i32.load8_u`, an `I16` or a `U16` 2, with `i32.store16` and
`i32.load16_s` or `i32.load16_u`, and an `I32` or a `U32` 4 (`CoreIrLayout.storage`). Fields are laid one after
another with no padding, so an `int` after a `U8` is read unaligned, which WebAssembly allows. Each storing helper has
a variant for each width, `_i8`, `_i16` and `_i64` after its name (`WasmRuntime.Storage`).

A `float` is an `f64` in locals, parameters and results, and takes 8 bytes wherever an `int` does, written with
`f64.store` and read with `f64.load`; the storing helpers have an `_f64` variant for it. It is not on the heap, so nothing
counts it (`CoreIrDrop.isHeapType`), and arithmetic on floats allocates nothing. A float constant is an `f64.const`.
The float intrinsics and the helper that writes a float as text take and give `f64`s, and an extern passes a float as
the `f64` it is.

An `F32` is held as a `float` is, as an `f32`: in locals, parameters and results, and in 4 bytes in memory, written
with `f32.store` and read with `f32.load`, with an `_f32` variant of each storing helper. Nothing counts it either, and
arithmetic on `F32`s allocates nothing. `%` on `F32`s is taken as a float's, which is exact, and narrowed back.

Pointers stay 32-bit: linear memory stays `memory32`. A 64-bit address space (Wasm `memory64`) only matters past
4 GB of heap, and would double the size of every pointer field.

## Object header

Every heap object has a reference count in the word before its info word, so a pointer `p` to an object with one has
its count at `p - 8` and the info word at `p - 4`: a string's length, a record's layout, and a closure's environment
drop function (below). An object with no info word (a list cell, a tag's cell) has its count just
before its fields, at `p - 4`. Every `dup` and `drop` knows the type of what it counts, so it knows which; giving
the commonest objects, list cells, an empty info word would make each 20 bytes rather than 16 (with the
allocator's size word). The drop function for the object's type knows the rest, so no other run-time layout
information is needed. `__nanyx_new_object` and `__nanyx_new_cell` allocate an object of each kind with a count of
one, and `__nanyx_release` gives up a reference, telling the caller whether it was the last.

Static data (string literals, and closures that capture nothing) gets a header too, with a count of 0, which marks it
as immortal: `release` leaves it alone, so it is never freed. A literal in the data segment is laid out as a string
on the heap is (the count, the length, the bytes and a 0 byte), padded to 4 bytes so that the next one is aligned.

A closure that captures nothing is the same block every time it is made: a closure over a top-level function passed
as a value (`list.map(xs, double)`), over a context member's function passed at a call (the six that `println`
takes), or over a lambda that captures nothing. So it is static data ([#743](https://github.com/nanyx-lang/nanyx/issues/743)):
one block per lifted function in the data segment, after the string literals, laid out as a closure on the heap is
(the count, the info word, the table slot), with an info word of 0, since nothing drops it. In the core IR it is an
atom, `AStaticClosure`, so reference counting gives it no dup or drop, and making one is an `i32.const`.

### Drop functions

`CoreIrDrop.fs` generates one for each type on the heap that the module's functions use
(`__nanyx_drop_list_string`, named by the type), together with the types of their fields. Each gives up a reference
and, when it was the last, drops the fields that are on the heap and frees the object:

- a list's walks along the list cell by cell, as long as each cell was held only by the one before it, so dropping
  a long list takes no stack;
- an array's drops each of its elements, from the first to the last. An array is a block made by `__nanyx_new_cell`,
  so its count is at `p - 4`, then its length, an `i32`, at `p`, then its elements one after another, each stored as
  a list cell's head is (`CoreIrLayout.arrayElementOffset`). A mutable array (`MArray`) is laid out the same way, so
  its drop is an array's; an `MRef` is a record of its region's links and its value (see [Regions](#regions)),
  so its drop is a record's, which takes it off its region's list first;
- a tag union's returns at once for a code (an odd value); for a cell, it reads the code to tell the payload's type,
  unless every tag with a payload has a payload of the same type;
- a function value's calls the function whose table slot is in the closure's info word, which drops what the
  closure captures. There is one such function per list of captured types (`__nanyx_drop_env__string`), and one,
  `__nanyx_drop_env`, for closures whose captures are none of them on the heap (a closure that captures nothing at
  all is static data, and is never dropped). It is called through the function table, so it has the type of a
  closure of one parameter, and returns 0;
- a recursive type's and a function value's, called while another drop is under way, leave the object they give up
  the last reference to waiting on the type's list, a global (`__nanyx_drop_pending_…`) linked through the objects'
  counts, which they no longer need, and return. Called when no drop is under way, they drop the object at once with
  the type's other drop function (`__nanyx_drop_dead_…`, which drops the fields of an object whose last reference is
  gone and frees it), then run `__nanyx_drop_pending`, which takes the objects waiting off their lists one at a time
  and drops each the same way, until every list is empty. Dropping one object may leave more waiting, but never
  calls itself, so a value nested 100,000 deep, such as a trie with a key that long
  ([#1670](https://github.com/nanyx-lang/nanyx/issues/1670)) or a chain of closures each capturing the one before
  ([#1706](https://github.com/nanyx-lang/nanyx/issues/1706)), takes no more stack to drop than a shallow one. Every
  chain of drop functions that calls itself passes through one of these: a function value's calls its environment's,
  which may drop a function value.

## Allocator

`__nanyx_alloc` and `__nanyx_free` (`WasmAllocator.fs`) keep segregated free lists by size class, falling back to
bumping `$heap_ptr` (and growing memory) when a class's list has nothing to give. Most objects are a handful of
words, so a few small classes cover them; larger objects (long strings, wide records) take a general free list.

- **Sizes.** `alloc(n)` rounds `n` up to a multiple of 4 (and at least 4), so blocks stay 4-byte aligned and
  pointers stay even.
- **8-byte aligned blocks.** A component's `cabi_realloc` must hand out blocks 8-byte aligned when the host asks it to
  ([Components](./components)). `__nanyx_alloc_aligned8` (`allocAligned8Helper`) asks `alloc` for 12 bytes more
  than it needs. When the block it gets is not 8-byte aligned, it makes the block's first 8 bytes a block of their
  own, which it frees, and gives the rest a size word of its own: that block starts 12 bytes on, at a multiple of 8,
  and `free` takes it back like any other. Under memory checks it counts the block it keeps as handed out.
- **Size word.** A block's rounded size is stored in the word just before the block `alloc` returns, so
  `free(b)` needs only the pointer. The object header is part of the block: a block holds the count, then the info word (if any), then the fields, so the size word sits just before the count. For a
  string, say, that puts the count at `p - 8`, the length at `p - 4`, and the size at `p - 12`; for a list cell,
  the count at `p - 4` and the size at `p - 8`.
  The word keeps `free` independent of types, and lets the large list hand out a bigger block than asked for and
  still take all of it back.
- **Classes.** Each size from 4 to 256 bytes (64 words) has its own list, so a freed block goes back to exactly the
  size Perceus will next ask for, without a search. Larger blocks share one list, searched first-fit. The classes
  reach 256 bytes so that the strings and records programs make most of never go on that list
  ([#1464](https://github.com/nanyx-lang/nanyx/issues/1464)).
- **Lists.** The heads are a table of 65 words at the start of the heap, and `$heap_ptr` starts after it. The
  string literals and static closures run from address 256, followed in a module that needs them by its host's block,
  the table of powers of five that reading a float uses (see
  [Standard library implementation](./standard-library-implementation#adding-to-it)) and the scratch block that writing
  one uses. The table of heads goes at 4096 or, when those end past that, after them,
  8-byte aligned (`heapBaseAfter`); the module starts with enough pages of memory to hold them. A free block keeps the next block of its list in its first word. On a small size's list, `free` pushes onto the
  front and `alloc` takes from the front, so the most recently freed block is reused first.
- **Large blocks.** `free` joins a large block with the free large blocks just before and after it, and a block that
  then ends at `$heap_ptr` goes back to the heap instead; otherwise it goes on the front of the large list. It finds
  those neighbours without walking the list, by boundary tags (`WasmAllocator.SizeWordBits`): a free large
  block has its size in its last word as well as in its size word, and two bits of a size word, which a size never
  uses, say whether the block is a free large block and whether the block just before it is one. The block after
  ends where this one does, so its size word is read directly; the block before is found from the size at its end,
  when this block's bit says it is free. The list is doubly linked (a free large block's second word links back), so
  a neighbour is taken off it at once. `alloc` keeps the bits: it clears the bit of the block after a large block it
  hands out whole, and a small block's size word keeps the bit through being freed and handed out again. A free large
  block never ends at `$heap_ptr` and never follows another, since `free` would have joined them. `alloc` splits a block it takes from the list
  when the rest would be a large block too; otherwise the block keeps its own size. This is what a string grown
  by `++` needs: each copy is freed after the next is made, the copies before it join into a block the one after
  fits in, so the heap holds about two copies of the string, not every copy there was.
- **Out of memory.** Past 4 GB, the end of a new block wraps around; `alloc` checks for that, and for `memory.grow`
  failing, and traps with `unreachable`. It writes no message itself, so a module that never prints imports nothing
  for it; `nanyx run` reports an `unreachable` in `__nanyx_alloc` as `Runtime error: out of memory`. A host other
  than the CLI does the same in its own runtime.

Small free blocks are never joined, with each other or with large ones, and the heap never shrinks: memory once
grown is not given back to the host. `alloc` still searches the large list first-fit, so many large free blocks too
small for a request are each passed over.

## Core IR

Perceus works per variable: it inserts a `drop x` after the last use of `x`, and a `dup x` before every use but the
last. That needs a program where every intermediate value has a name and a definite position, so the core IR sits
between the typed AST and WebAssembly text:

```
source → AST → typed AST → core IR → WAT
```

It has few constructs (`let`, call, `match`, allocate, read field, and the like), in A-normal form, with a
concrete type on every node: generic functions are specialised as the IR is built (see Monomorphisation). Pipes,
interpolation, `if ... is`, record updates, workflows and context calls are lowered into it, and code generation
works from it alone.

**Shape.** The IR is in `compiler/Nanyx.Compiler.Wasm/CoreIr.fs`. A function has typed parameters, a result type and
a body. Types are concrete (`int`, `bool`, `()`, `string`, `float`, a union of tags, a record, a list, a function),
with no type variables. A tuple is a record whose fields are its positions, and a nominal type is the type of its
shape. A body is an expression with a type on every node:

| Node | Meaning |
|------|---------|
| atom | a variable or a constant (an int, a bool, unit, a string literal, a tag without a payload, a closure that captures nothing) |
| `prim(op, atoms)` | an operator on ints or booleans |
| `call(f, atoms)` | a call to a top-level function, by its name in WebAssembly text |
| `makeTag(tag, atom)` | allocates the cell of a tag with a payload |
| `makeRecord(f1 = atom, ...)` | allocates a record, its fields in the order they are stored |
| `field(atom, slot)` | reads the field stored in a slot of a record |
| `updateRecord(atom, slot = atom, ...)` | a record with some fields replaced (`record with f = v`): the same one, updated in place, when it is held once, and a copy otherwise |
| `x := atom` | gives a local cell a new value, dropping the one it held (see [Memory](../advanced/memory)) |
| `x[slot] := atom` | writes a field of a box a lambda shares, dropping the value it held: how a captured cell changes |
| `makeList(atoms)` | builds a list |
| `callRuntime(helper, atoms)` | calls a runtime helper: an intrinsic, or what `++` and `==` on strings, lists and tags need |
| `makeClosure(f, atoms)` | allocates a closure block: the lifted function's table slot, then what it captures (one or more) |
| `callClosure(closure, atoms)` | calls through a function value, passing the closure block as the environment, to its borrowing or its owning entry (see Closures have two entries) |
| `floatConstant(value)` | a float constant: an `f64`, or an `f32` for an `F32` |
| `forEach(loop)` | a `for` loop over lists and ranges of `int`s in step, its body run for each step until one of them ends |
| `crash(messages)` | an error deferred to run time: writes the messages to stderr and ends the program |
| `interpolate(parts)` | builds an interpolated string from its text and its holes, each already stored in its hole local |
| `dbg(location, text)` | prints the string `text`, the text of a value its type's printer wrote, after the string `location`, where the `dbg` was called (`[main.nyx:12] `); a release build has none |
| `let x = value in body` | names a value; the value is never a `let` itself |
| `do value; rest` | evaluates a value for its effects and drops it |
| `if atom then e1 else e2` | |
| `match x1, ..., xn \| arms` | tests variables against each arm's patterns (`_`, a literal, a guard such as `> 5`, a tag with a payload pattern, a tuple, a list, a nominal type's shape, a binding) |

Every operand is an atom, so every intermediate value has a name. Each variable says what kind it is: a parameter, a
local the program declares, a scrutinee, a name a pattern binds, or a temporary (a value the source does not name).

**Lowering.** `CoreIrLower.fs` lowers each definition from the typed module: its expressions as the backend sees
them, with each node's type from `ExprTypes` and the type of the function at each call from `CalleeTypes`. Its
`FunctionLowering` hands parts of the work to files of their own through records of what they need from it, such as
`CoreIrLowerMatch.fs` for a `match` and its patterns, `CoreIrLowerLocals.fs` for local functions generic in variables of their own, `CoreIrLowerLifted.fs` for lifted lambdas, wrappers and adapters, `CoreIrLowerMembers.fs` for the context members a call passes and adapting a value to a type, and `CoreIrLowerBoxes.fs` for boxed values. A
definition whose type is concrete is lowered as it is. A call to a generic definition binds the definition's type
variables to the types at the call, and asks for the specialisation at those type arguments (`id__int`,
`id__string`), which is lowered in turn with the variables replaced. Each specialisation is lowered once, however
many calls ask for it. A definition has at most 64 at the types it is called at (`maxSpecialisations`), since
polymorphic recursion would otherwise never end: a call past them goes through one more, with each type argument's
values boxed (`IrBox`), by a function at the call's own types that converts its arguments and its result to and from
boxes (`CoreIrLowerModule.boxedInstead`, `CoreIrLowerLifted.boxedCalling`). A call that is specialised on known context
members or described types, or on what a context variable stands for, can't go that way, and past the cap is still
`NYX0301`.
A type variable that nothing constrains (the element of `[]`) is `()`: nothing can make or inspect a value of such a
type. The rest of an open union that no use closes adds no tags.
A definition that reads fields of any record that has them is specialised per record layout, so each specialisation
reads its fields from where they are stored. A generic definition exists only as its specialisations: it is neither
compiled nor exported as it is.

A definition inlined from an imported module (the prelude's `list.map`, say) is specialised the same way. Only the root
module's types by node are resolved for the editor's sake, so the types of the inlined definitions' nodes are resolved
apart, each by the module it came from, and only the IR reads them (`TypedModule.InlinedExprTypes`). An inlined
definition may require a context its module keeps to itself, which no import brings in, so the root also carries the
contexts of the modules it imports that it doesn't name, and the backend types such a context's members from them
(`TypedModule.InlinedTypeDefs`).

Lambda lifting runs on the syntax tree first (`LambdaLifting.fs`): a lambda becomes a top-level function that
takes a closure block as its environment, and the lambda is replaced by a closure over it, a call to the markers
`ClosureSyntax.fs` names. What a lambda captures is `LambdaCaptures.capturedFrom`, which the editor's hover reads too
(`LambdaCaptureHover`), so what hover says a lambda captures is what its closure holds. The IR lowers a lifted
function where a closure over it is made, since only there are the
types of what it captures known. A lambda written in a generic definition is specialised along with it
(`__lambda_0__int`), and the wrapper that lets a top-level function be passed as a value is lowered for each type it
is passed at. So a lifted function is always lowered at concrete types, and lifting stays on the syntax tree: moving it
into the IR was considered and set aside in [#425](https://github.com/nanyx-lang/nanyx/issues/425). A lifted lambda
captures only the context members it may call ([#934](https://github.com/nanyx-lang/nanyx/issues/934); see
[Context calls](./context-calls#function-values-that-require-contexts)).

How a function value is called follows from its type, not from how its lambda names its parameters or how the call
writes its argument, since `int, int -> int` and `(int, int) -> int` are one type. A function value whose input is a
tuple of two or more takes one argument for each element (`IrType.tupleElements` in `CoreIr.fs`): a lambda or wrapper of
one parameter (`{ t -> ... }`, `{ (a, b) -> ... }`) takes the elements and builds the tuple again, and a call that
passes a tuple whole (`f(t)`) reads the elements from it ([#441](https://github.com/nanyx-lang/nanyx/issues/441)),
from a local of its own when the tuple is not a name, `f(xs(i))`, since a temporary is read only once
([#2391](https://github.com/nanyx-lang/nanyx/issues/2391)).
One whose input is a record that is not a tuple, `(channel: string, n: int) -> int`, takes the record whole, as a call
passes it: a lambda or wrapper of several parameters (`{ channel, n -> ... }`) binds each to the field of its name
(`IrType.recordTakenWhole`; [#2466](https://github.com/nanyx-lang/nanyx/issues/2466)).

A function that requires contexts takes each of their members as a closure, in a parameter of its own after its
others, typed from the context's definition. A call finds each member the callee requires where the type checker
found its context (a `use`, the caller's own members, an instance built from attached definitions, or the ambient
`Console`). A member that is a top-level function (one of the ambient `Console`'s, or of an instance built from
attached definitions) or a top-level value is known where the call is compiled, and is not passed: the call goes to a
specialisation of the callee on it (`println__string__known`), which calls a known function directly and passes it on
to the functions it calls as known. Only a member that is not known is passed as a closure: a `use` of a lambda, or a
member a function that is not specialised on it passes on. A specialisation makes a closure over a known member only
where it uses the member as a value, as when it passes it to `list.map` (`CoreIrLowerKnown.fs`). [Context
calls](./context-calls) describes this, and what is still to do. A lambda of no parameters is lifted like any other, so `def block = { ... }` is a function to call. A workflow block that uses a keyword is not a lambda by then: desugaring leaves its body where it
is written.

The module's top-level expressions are lowered as one more function, the start function, which evaluates each in
turn and drops its value. A module with type or name errors is compiled all the same in a debug build: what failed to
check is a `crash` that reports the error when it is reached, and a value of no known type because of it has a type of
its own (still an `i32`). Matching on or interpolating such a value stops the program there, if the error has not
already. A definition (or top-level expression) that holds such errors and cannot be lowered because of them is
lowered to a trap that reports them as soon as it is called. The trap takes the parameters and results in the type the
definition would, so that a call through a function value, such as `memory`'s call of its block, reaches it at the
signature the value's type gives; only where even those cannot be lowered does it take values of no known type. A definition that is generic only because an error left
its type open, and is not specialised, is lowered with its type variables defaulted, so that it still exists.

**Top-level values.** A top-level value that is not a lambda is lowered to a function of no parameters, one for each
type it is used at when it is generic (`proc` once per instance). Each one that computes its value is built once, on
its first read (`CoreIrCachedValues.fs`, [#1257](https://github.com/nanyx-lang/nanyx/issues/1257),
[#1151](https://github.com/nanyx-lang/nanyx/issues/1151)): the function is emitted as `<name>__build`, and its own name
is a getter that calls it the first time, keeps the value in a global, and hands each read one more reference to it.
So a `dbg` in a value prints once, at the value's first read rather than where it is written, and a list or record a
value builds is allocated once. The inliner leaves the getters alone (it would put the building back at each read). A
value that is a constant (a literal, a closure that captures nothing) is not cached, and is inlined where it is read. A value
that takes a context's members as parameters is built at each read, as a function is. The start function ends by
dropping each value that was built and marking it unbuilt, so a program run with memory checks frees everything. A
script's values that need a context are not top-level values by then: they are local defs of its entry point
([Hosts](./hosts)).

**Generating code.** `CoreIrEmit.fs` writes WebAssembly text from the IR, with each value in the WebAssembly type of
its type (see Value representation above). A temporary is not stored: it is used exactly once, by the next instruction that needs
it, so its value is computed where it is used. An operand that stores to a local on its way to its value (a `match`
or an interpolated string among a call's arguments, say) is one temporary whose value is the whole sequence, stores
and all, so nothing waits on the stack across a store. A chain of temporaries, such as a sum of thousands of terms,
or an else-if chain, nests as deep as it is long, so the code of each value, sequence and remaining match arm is kept
aside, its parent holding a short reference to it, and the references are replaced by their code once the function is
written (`WatPieces.fs`): a chain is written in time that grows with its length, not its square. Record layouts and function table slots are numbered as
functions are emitted, in definition order, each definition followed by its specialisations. What the module needs
besides its functions comes from the IR of exactly the functions it emits (`CoreIrFacts.fs`): the tags it numbers
(sorted by name, so every case of a tag type a hole prints has one), the string literals in its data segment, and the
helpers `dbg` calls. The heap is set up when some function calls `$alloc`.

**What cannot be compiled.** Every function is compiled from the IR; there is no other way. What the IR cannot lower
is an error, `UnsupportedByBackend`, reported where it is written: `tryTranspileTypedModuleToWat` gives the first
one. `==` on a record, a tuple, a list or a tag with a payload calls an equality function generated for its type,
which compares the values by their structure (`CoreIrEquality.fs`,
[#424](https://github.com/nanyx-lang/nanyx/issues/424)). A broken invariant of the backend is reported apart from these, as
`BackendInternalError`, which asks for a bug report. Only a type-checked module is compiled.
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

Steps 1 and 2 are `CoreIrCounting.fs`, run on every function of the module after it is lowered and calls are inlined
into it ([Inlining](./inlining)), which adds three
nodes to the IR: `dup x1, ...; e`, `drop x1, ...; e`, and `e then drop x1, ...` (drop once `e` is done).
`CoreIrDrop.fs` generates the dup and drop function of each type.

- **Every variable owns its reference,** except a parameter its function borrows, a name a pattern binds that the
  arm does not use, and, inside a `for` loop's body or a guard, every variable from outside it. Each instruction
  either takes an operand over (a call that owns the parameter, a field of a new object, a value returned)
  or borrows it (a field read, an operator, a runtime helper, `dbg`): a variable is duplicated where it is taken over and
  still used after, and dropped after the instruction that used it last if that one only borrowed it. An
  instruction reads what it borrows when it runs, after all its operands are evaluated, so a variable it borrows
  counts as used until then: an operand after it that takes it over (`f(x, x)` with the first parameter borrowed)
  gets a reference of its own, a temporary after it (`f(x, x * x)`) does not drop it, and the variable is dropped
  once the instruction is done.
- **Temporaries are counted where they are used.** A temporary's value is computed where it is used (see Generating
  code), so its value is analysed there, in the order the operands are evaluated. A temporary an instruction only
  borrows is kept in a local as it is pushed (`local.tee`), and dropped after the instruction.
- **Drops happen before a store.** What dies in a `let`'s value is dropped as the value is computed, before it is
  stored, so a local the variable shares with another of the same name (see the IR's `VarKind`) still holds it.
- **Matching.** An arm takes a reference to each name its patterns bind that it uses, then drops the scrutinee if
  it does not use it either; in a scrutinee the function borrows, it does neither (see below). Reading a field takes a reference to the value read.
- **Ownership.** A function called through a closure (a lifted lambda, or the wrapper of a function passed as a
  value) borrows its environment and owns its other parameters: a call through a closure passes them on owned, or a
  new reference to one if it is used again, and keeps its own reference to the closure. Another function borrows a
  parameter on the heap it never takes over, and owns it otherwise; this is worked out over the whole module at
  once, until nothing changes. Calling a closure only borrows it, so a function that is given one and only calls it
  borrows that parameter too.
- **What a function only reads in a match is borrowed** ([#1468](https://github.com/nanyx-lang/nanyx/issues/1468)),
  inferred as Lean 4 does (`borrowedFrom` and `couldReuse` in `CoreIrCounting.fs`). Matching on a parameter does not
  take it over: the scrutinee that copies it, and the names its patterns bind, are borrowed with it, and so is the
  closure read as the record of what it captures, which is how the inliner gives an inlined lambda its environment
  (`isReadingCopy`, [#1877](https://github.com/nanyx-lang/nanyx/issues/1877)); the function
  borrows the parameter when none of them is taken over (a call that borrows them in turn, the function's own tail call
  included, does not take them over) and no arm could reuse its block (none allocates a block of that size, see
  `CoreIrReuseSites.fs`). So a sum or an evaluator over a shared list or tree counts nothing. The cost accepted: a fold
  over a list held once doesn't free each cell as it passes it, which doesn't change peak memory, since the list was
  built whole first.
- **Closures have two entries** ([#1298](https://github.com/nanyx-lang/nanyx/issues/1298), `CoreIrClosureEntries.fs`).
  Each function a closure is over is in the closure's table slot, borrowing its environment, and a copy of it that
  owns its environment, its owning entry (`__lambda_9__owning`), is in the slot after it. A tail call of a closure
  that dies there calls the owning entry (`CallClosure` with `Owning`, which reads the slot and adds 1), since the
  caller could not drop the closure after the call; so does a call by name in tail position that passes such a
  closure as the environment, as the simplifier makes of a call of a closure it knows. Every other call of a closure
  calls the borrowing entry, and a closure that dies there is dropped after the call. The borrowing entry borrows
  the context members the call passes after its arguments too, which the caller holds throughout, and the owning
  entry owns them ([#2559](https://github.com/nanyx-lang/nanyx/issues/2559)). A function whose closures capture
  nothing and are called with no members has no copy: they are static data, which a drop leaves alone, so it is its
  own owning entry. Once the module is counted, an owning entry nothing can call is left out: one no call names, and
  of a type no call through a closure that calls the owning entry has. A closure called in a loop, as `list.fold`
  calls the function it is given, is then counted neither up nor down at each call, and nor are the members of the
  contexts it requires.
- **Runtime helpers borrow.** Every helper, the ones that write to stdout and stderr among them, borrows its
  operands and returns a value of its own, except `list.cons` (which takes both over) and `++` on lists (which
  takes the second over, and copies the first's cells with a reference to each element). Strings handed to the
  host are borrowed for the call.
- **Tail calls.** An argument passed in a tail call that the caller owns makes the callee own that parameter, so
  nothing is left to drop after a tail call (see [Tail calls](./tail-calls)).
- **Recursive local closures** need nothing more: lambda lifting already makes a `rec` local refer to itself
  through the environment it is called with, never through what it captures, so it cannot capture itself.

Steps 3 and 4 are `CoreIrReuse.fs`, run on the counted IR, which adds two more nodes: `release x; e` and an
allocation `reusing` a token.

- **Drop specialisation.** An arm that takes apart one object (a list's first cell, a tag's cell, a record) and that
  does not use the object itself releases it instead of taking a reference to each name and dropping it. The release
  checks the count: when it is one, the names keep the fields' references, the fields no name the arm uses are
  dropped, and the block is freed; otherwise the names take references of their own and the count goes down. Each
  field's pattern binds or ignores it, tests it without binding anything (`[0, ...rest]`, `[a]`, whose rest must be
  `[]`), or takes apart an object in turn (`[b, a, ...rest]`, `[#some(n), ...rest]`): `CoreIrReuseSites.takenApart`
  binds that object to a name of its own (`__inner_1`), whose reference the outer release takes over, and the inner
  object is released after it, as a nested `match` would ([#1547](https://github.com/nanyx-lang/nanyx/issues/1547)).
- **Reuse.** Every object an arm releases, in each of its scrutinees and nested ones, outer before inner, goes to
  the first allocation the arm then makes of a block of its size that an earlier one has not taken (not in a loop or
  a guard): the release keeps the
  block in a token instead of freeing it, or sets the token to 0 when the object was shared, and the allocation
  writes into the block when the token holds one (`__nanyx_list_cons_at`, `__nanyx_tag_box_at`,
  `__nanyx_reuse_object`). Mapping over a list held once rewrites its cells in place. When the first such allocation
  is in some branches of an `if` or `match` and not others, each branch that has one writes into the block, and each
  that has none frees it first (`FreeToken`), so the block is used or freed once on every path, as in Perceus.
  Filtering a list held once keeps the cells of the elements it keeps.
- **`with` updates in place.** `updateRecord` takes its record over, counted after the new values, which may read
  its fields. Its code computes the new values into locals, then releases the record: when the count was one, it
  drops the old value of each field on the heap it replaces and stores the new ones into the same block; otherwise
  the release has taken the count down, and it copies the record, takes a reference to each field on the heap it
  keeps, and stores the new values into the copy (`UpdateRecord` in `CoreIrEmit.fs`).
- **Exclusivity checks.** Reuse covers what the compiler can see; the standard library does the rest at run time, as
  Lean 4's `Array` does ([#1671](https://github.com/nanyx-lang/nanyx/issues/1671)). The intrinsic
  `__nanyx_is_exclusive` takes over a reference to a value that is always on the heap, reads its count, gives the
  reference up, and says whether the count was 1 (an immortal object's is 0, so never). Taking the reference over is
  what makes the answer mean something: a caller that uses the value afterwards keeps a reference of its own, which
  counting adds before the check, inlined or not, and a function that checks a parameter owns it. `MList.toList`
  checks its record of references this way, and hands over the cells the `MList` built when it holds the only reference, or copies them.
  `__nanyx_list_set_tail` changes a cell's tail in place, giving up the old one and taking the new one over: what
  builds a list front to back, in cells nothing else sees yet (`MList.add`). `__nanyx_list_set_head` changes a
  cell's element the same way, which is how `MList.set` changes an element of its own cells without copying them.
  All three are written out where they are used (`CoreIrEmit.fs`), with no helper.
- **`[a, ...rest]`** is `[a] ++ rest` to the parser. It is lowered to a cell for `a` in front of `rest`, rather than
  a one-element list whose cell `++` copies, so that it allocates one cell, and can reuse one.

The tests in `Features/Memory` check that a loop over lists, records, closures and tags runs in constant memory,
that the examples that compile free every block they allocate, and, counting what the allocator hands out, that
mapping over a list, a tuple or a tag's cell held once allocates nothing, nor does a nested pattern or an arm over
several lists, and nor does `with` on a record held once.

**Memory checks.** `nanyx run --check-memory` (`WasmOptions.CheckMemory`) compiles the program with an allocator
and counts that check themselves (`checkedRuntimeHelpers` in `WasmRuntime.fs` and `MemoryChecks` in `WasmAllocator.fs`):

- `alloc` and `free` count the blocks still in use. The program's start function is exported rather than run on
  instantiation, so the CLI can read the count once the program has finished. A count above 0 fails the run.
- `free` sets the low bit of a block's size word (sizes are multiples of 4), and `alloc` clears it when it hands
  the block out again. Freeing a marked block, or a `retain` or `release` of an object on the heap whose block is
  marked, writes what went wrong to stderr and exits with 70. A check uses WASI `proc_exit`, not a trap. The Wasm test
  host and the CLI both run Wasmtime off the CET shadow stack (`CetCompat` in both projects), so a trap raised inside
  either doesn't crash it.
- `free` fills a block after its free-list link with `0xDEADBEEF`, so a field read after the block was freed is a
  pointer far outside memory, and following it traps.

Every Wasm test that runs a program runs it as `nanyx run --check-memory` does, through the CLI's own code in the
test process (`runScriptWith` in `TestHelpers.fs`), and the tests that read the counters compile with the checks too. So a leak, a double free or a use after free in any test program fails that test.

`nanyx run --memory` runs a program as `--check-memory` does and then writes what the counters say to stderr
(`MemoryReport.cs` in the CLI, [#1562](https://github.com/nanyx-lang/nanyx/issues/1562)). The checked allocator keeps
them beside the block counts: the bytes of every block it hands out (`bytes_allocated`, an `i64`), the bytes in use and
the most there have been at once (`live_bytes`, `peak_live_bytes`), and the highest `$heap_ptr` has been
(`peak_heap`, an address, so it counts the string literals and the free lists' table below the heap). A block's bytes
are its size word's, so without the word itself. `reused_in_place` counts the objects built in a block given up with a
count of one: `__nanyx_list_cons_at`, `__nanyx_tag_box_at` and `__nanyx_reuse_object` count it when the token holds a
block, and `UpdateRecord` when it updates the record in place. The runner reads the counters however the program
stopped (`WasmRunner.runCounting`), so the report follows a trap, a failed check or a leak too.

## Regions

Counting can't free a cycle, and a reference (`MRef`), a captured cell or an `MArray` can hold a value that holds it
back. So each `memory` block frees, when it ends, what is left of the mutable objects made while it was the innermost
block running, whatever their counts say ([#2232](https://github.com/nanyx-lang/nanyx/issues/2232), [#2376](https://github.com/nanyx-lang/nanyx/issues/2376)).

**The region stack.** Each `memory` block running has a region (`WasmRegions.fs`), which `memory` starts and ends with
the intrinsics `__nanyx_region_enter` and `__nanyx_region_exit`. A region is a block of five words: no drop function,
the first object on its list, the region it is inside (none for the outermost), the last object on its list, and its
depth, 1 for the outermost. The innermost region is in a global, so the regions make a stack linked through the third
word, and the outermost is in another global. The list is circular, with the region as its sentinel, so an object is
linked and unlinked without asking whether it is first or last.

**A block with nothing to free has no region.** Once inlining is done, a function that makes no box, calls no function
value, and calls only functions that can't make one either has its `__nanyx_region_enter`s and `__nanyx_region_exit`s
taken out (`CoreIrUnusedRegions`), so a block whose cells are all locals, as a loop is written, allocates nothing when it
runs ([#2418](https://github.com/nanyx-lang/nanyx/issues/2418)). A `__nanyx_region_exit` of a temporary, the block's
result read nowhere else, leaves the temporary in its place, so that it is still read once
([#2906](https://github.com/nanyx-lang/nanyx/issues/2906)). A block that keeps its region inside such a function has
the region around the function's caller as its outer one, which is where its result is promoted to.

**Boxes.** A mutable object is a box laid out as a record of five fields (`CoreIrRegions.boxType`): the table slot of the
function that drops what it holds (`__nanyx_region_drop_…`, one for each type of box, called through the table as an
environment's drop function is), the next object on its region's list, the region it is on, the previous object, then
the value. The link fields' names start with `-`, which no field of the program's can, so they are stored first and no
record of the program's is taken for a box. An `MRef` holds its value, a captured cell's box holds the cell's value, and
an `MArray` holds the array its elements are in, which is laid out as an `Array` is, so reading or writing an element
does what the array's intrinsic does, given the box, whose array it reads with no reference of its own: the box lives
until the intrinsic is done, and nothing puts another array in it (`CoreIrRegions.mutableArray`,
`CoreIrEmit.ArrayIntrinsic`, [#2417](https://github.com/nanyx-lang/nanyx/issues/2417)). A new box goes on
the innermost region (`__nanyx_region_track`). When
counting gives up a box's last reference, its drop function takes it off its region's list (`__nanyx_region_unlink`)
before it drops its value and frees it, as any record's does, so a box that is not in a cycle is freed as early as it
ever was.

**Promotion.** The invariant is that if a box can reach another, through what it holds or the immutable values in that,
the other's region is its own or an outer one. Two kinds of place keep it (`CoreIrPromotion.fs`):

- a store into a box (`MRef.set`, `:=` on a captured cell, `MArray.set`) first promotes the stored value onto the box's
  region;
- a block's result is promoted onto the region of the block around it, before the block's own region ends.

To promote a value is to move each box it holds that is on a region deeper than the target onto the target
(`__nanyx_region_move`), and to promote what that box holds in turn. A box already on the target or an outer region is
left as it is, which also ends a walk around a cycle. Each type that can hold a box has a promote function
(`__nanyx_promote_…`), generated as drop functions are, and a store of a value of any other type, a number, a string, a
list of them, promotes nothing. What a function value or a boxed value holds can't be followed, so promoting one merges
every region inside the target onto it (`__nanyx_region_merge`). A cell no lambda captures is a WebAssembly local, which
lives no longer than the block it is in, so `:=` on one promotes nothing. The cells of a list an `MList` changes aren't
on a region. A new cell that `MList.add` makes the last one's tail is stored into the `MList`'s reference to its last
cell too, which promotes it onto the `MList`'s region, so `__nanyx_list_set_tail` has no barrier of its own; a value
`MList.set` stores into a cell is promoted onto the outermost region. Since a box made in a
block can only stay reachable after the block ends through its result or a store into an outer block's box, both of
which move it out, nothing the program can still reach refers to what is left on a block's region when it ends. This
holds whatever the type checker allows, so a hole in the escape check
([Memory](../advanced/memory#values-cannot-escape-memory-scope)) would make an object live longer, not be freed while
it is used, except in the outermost block, whose result has no region to move to.

**Ending a block.** `__nanyx_region_exit` promotes the block's result, when it can hold a mutable object and there is
a block around this one, and a result that is a temporary is computed into a local first, since the promotion may not
read it ([#2906](https://github.com/nanyx-lang/nanyx/issues/2906)). Then it pops the region and `__nanyx_region_end` frees what is on it: it takes a
reference to every box on the list, so that none is freed or taken off it while it runs, then calls each box's drop
function, which gives up the references the boxes hold to each other, then frees each box, whatever its count says, and
the region. A block can't be left without ending it: a crash ends the program, and a workflow's `try` or `return` in a
block ends the block's lambda, after which `memory` ends the region as usual.

## Things that need care

- **Closures and contexts.** Capturing a value duplicates it, and freeing a closure drops what it captured. Context
  members that are not known where a function is called are passed as closures and follow the same rules.
- **Host calls.** Strings handed to the runtime (`dbg`, a deferred error) are borrowed for the call. The console host
  writes a string with WASI's `fd_write`, through an iovec that holds its address, and keeps it alive past the call with
  `keepAlive`.
- **Addresses of strings.** A host takes a string's address with `nanyx/host.memory`'s `addressOf`, to pass to a
  foreign function, and nothing else may read the string after that: inlined, its last use can come before the call
  the address is passed to. So counting treats a value made from an address (by arithmetic, a copy, or a call of a
  function that returns one made from its arguments, `CoreIrCounting.inferAddressResults`) as a use of the strings it
  is an address in (`addressFlow`): an instruction that reads it reads them last, and they live until it is done. A
  string made in the argument of `addressOf` is a temporary with no name to follow, and is dropped at once.
