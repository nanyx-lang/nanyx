---
title: "Closed and open records"
description: "Whether a record type allows fields it does not name"
order: 23
---

> **Status: in progress.** This page decides whether a record type allows fields it does not name. Built: closed
> records and record patterns have exactly their fields, and a pattern may end in `...` (stage 1 of the
> [plan](#plan)). Not built: writing `...` in a type. It is part
> of the [compilation](./compilation) workstream, and its issues carry the `compilation` and `type checker` labels.

# Summary

A record type is **closed**: `(x: int, y: int)` is a record with exactly the fields `x` and `y`, no more and no
fewer. A type opts in to more fields with `...`: `(x: int, y: int, ...)` is any record with *at least* `x` and `y`.

```nanyx
def f: (x: int, y: int) -> int = { x, y -> x + y }
def g: (x: int, y: int, ...) -> int = { x, y -> x + y }

def data = (x = 1, y = 2, z = 3)

f(data)   -- error: `f` takes no field `z`
g(data)   -- fine: `z` is one of the other fields
```

Inference gives open types where it learns only some of a record's fields. A function that reads `p.x` and `p.y`
from an unannotated parameter takes any record with those fields, so most code that is not annotated never needs
`...`. What you write is what you get: `...` only has to be written in a type annotation.

# Why closed

- **Records are argument lists.** A function's parameters are one record, so a call's arguments are a record
  literal. If record types were open, `f(x = 1, y = 2, verbos = true)` would compile, and the misspelled argument
  would be dropped without a word. Named arguments are only safe if an argument the function does not take is an
  error.
- **A closed record has one layout.** A record's fields are stored in a fixed order ([Memory management](./memory-management)), so
  reading a field of a closed record is a load from an offset known at compile time. A function over an open
  record has to be specialised for each record it is given, or be passed the offsets at run time. Making records
  closed by default means paying that only where `...` asks for it.
- **Simpler types and better errors.** With open records everywhere, every inferred signature carries a row
  variable, and a mistake is reported as "cannot unify `(x: int, ...a)` with `(y: int, ...b)`" instead of "missing
  field `y`". Equality, `with`, pattern matching and serialisation are all simpler on a record whose fields are
  known.
- **Closed can be relaxed later; open cannot be tightened.** Making records open later only makes more programs
  compile. Making them closed later would break programs that compile now.

The alternative TypeScript takes, checking for extra fields only on a record literal written at the call, was
rejected: `f((x = 1, y = 2, z = 3))` and `f(data)` would then mean different things, though `data` is that literal.

# Design

## Writing an open record type

`...` at the end of a record type's named fields stands for the record's other fields:

```nanyx
type HasName = (name: string, ...)

def greet: (name: string, ...) -> string = { p -> "Hello, {p.name}" }
```

`...r` names the other fields, so that a type can say two records have the same ones. It is the row variable the
compiler already prints for an inferred open record:

```nanyx
def moveRight: (x: int, ...r) -> (x: int, ...r) = { p -> p with x = p.x + 1 }

def moved = moveRight((x = 1, y = 2, label = "a"))   -- (label: string, x: int, y: int)
```

A type variable named in a row is generic like any other type variable in an annotation.

## Positional fields stay exact

`...` stands for named fields only. A record's positional fields are its arguments when it is passed to a
function, so `(int, int, ...)` would let a two-argument function be called with three. A record's positional
fields are always exactly the ones its type lists.

## Patterns

A record pattern by member name is closed in the same way. It names every field of the record, or ends in `...` to
leave the others out, as Rust's `..` does:

```nanyx
def describe: (name: string, age: int) -> string = { r ->
  match r
    | (name = "Alice", ...) -> "Found Alice"
    | (name = n, age = a) -> "{n} is {a}"
}
```

Reading fewer fields than a record has cannot go wrong, but writing `...` says that leaving them out was on purpose,
and a field added to the type later is then an error in each pattern that did not expect it. Matched against a value
of no known type yet, `(x = 1)` makes it a `(x: int)` and `(x = 1, ...)` a `(x: int, …)`.

## Inference

- **Reading a field** of a value whose type is not otherwise known gives it an open record type: `{ p -> p.x }` is
  `(x: α, …) -> α` in an editor. This is what the type checker does today (`TyOpenRecord`).
- **A record literal** has a closed type: `(x = 1, y = 2)` is `(x: int, y: int)`.
- **An annotation** is taken as written: `(x: int, y: int)` is closed, however the value it annotates was
  inferred.

## Checking

Where a record of type `A` is given and type `B` is expected:

| `A` (given) | `B` (expected) | Rule |
|-------------|----------------|------|
| closed | closed | the same fields, each of the same type |
| closed | open | `A` has each of `B`'s fields; `B`'s row is `A`'s other fields |
| open | closed | each of `A`'s fields is one of `B`'s; `A`'s row is `B`'s other fields |
| open | open | the fields both name agree; each row is the other's extra fields and a row they share |

There is no width subtyping between closed records: a `(x: int, y: int, z: int)` is not a `(x: int, y: int)`. To
pass a record with more fields, either the function says `...` or the caller builds the record it takes.

## Code generation

A function whose parameter is an open record is generic in its row, and the backend specialises it for each record
type it is called with, as it does for any other generic function. Each specialisation reads fields at fixed
offsets, so open records cost code size, not speed.

# Where we are today

- **Closed records are closed.** `unifyRecordFields` ([Unifier.fs](https://github.com/nanyx-lang/nanyx/blob/main/compiler/Nanyx.Compiler/Unifier.fs), in `unify`)
  accepts two closed records only when they have the same fields, in `Equal` and `Assignable` constraints alike.
  The error names the fields only one of them has:

  ```text
  Record field mismatch: {a, x, y} vs {x, y}: only the first has a. Type mismatch: (a: int, x: int, y: int) vs (x: int, y: int).
  ```

  Until [#470](https://github.com/nanyx-lang/nanyx/issues/470), it accepted them when either's fields were a subset
  of the other's, so a call with a field more or a field fewer type-checked and then produced WebAssembly that
  Wasmtime rejected.
- **A record pattern names every field, or ends in `...`.** A pattern by member name is closed like a record
  type: `(name = n, age = a)` matches a `(name: string, age: int)`, and `(name = n)` is an error that names `age`.
  `(name = n, ...)` leaves the other fields out, and tells inference the value is an open record with at least
  those fields (`openRecordWith` in
  [InferState.fs](https://github.com/nanyx-lang/nanyx/blob/main/compiler/Nanyx.Compiler/InferState.fs)). A tuple
  pattern's names, `(name, age)`, are closed too, and have no `...` form.
- **Open records exist in inference only.** `TyOpenRecord` ([TypedAst.fs](https://github.com/nanyx-lang/nanyx/blob/main/compiler/Nanyx.Compiler/TypedAst.fs)) is what
  reading a field of an unknown value gives, and is printed `(x: int, ...a)`. Unification solves its row as the
  table above says. There is no syntax for it: the parser's `typeRecord` ([Types.fs](https://github.com/nanyx-lang/nanyx/blob/main/compiler/Nanyx.Parser/Types.fs))
  does not accept `...`.
- **The backend specialises open records.** `bindScheme` in [CoreIrLowerTypes.fs](https://github.com/nanyx-lang/nanyx/blob/main/compiler/Nanyx.Compiler.Wasm/CoreIrLowerTypes.fs)
  binds an open record's row to the fields of the record it is used at. An open record still open at code generation
  is reported as not supported.

# Plan

1. **Closed means closed.** _Done._ Two closed records agree only when they have the same fields, with an error
   that names the extra or missing ones ([#470](https://github.com/nanyx-lang/nanyx/issues/470)). A record pattern
   names every field or ends in `...` ([#459](https://github.com/nanyx-lang/nanyx/issues/459)).
2. **`...` and `...r` in record types.** Parse them, lower them to `TyOpenRecord`, and have the formatter, hover
   and completion show them. ([#471](https://github.com/nanyx-lang/nanyx/issues/471))
3. **Docs.** The [records tour](../tour/records#closed-and-open-records) describes the
   design; once stages 1 and 2 land, its examples get tests.

# Open questions

- **Spread of an open record.** Record spread works today on records whose fields are all known where the spread is
  checked (`RecordSpread.fs`); a spread of an open record, or of a value whose type is not known yet, is `NYX0231`.
  `(...p, z = 1)` where `p` is `(x: int, ...r)` could be `(x: int, z: int, ...r)`, unless `r` has a `z`. Whether a
  spread may override a field that is only in a row needs deciding.
