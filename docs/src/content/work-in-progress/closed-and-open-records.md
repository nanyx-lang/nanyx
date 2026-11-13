---
title: "Closed and open records"
description: "How closed and open record types are checked and built, and what is still open"
order: 23
---

Closed and open records are built. The language docs describe them in
[Closed and open records](../tour/records#closed-and-open-records). This page keeps the unifier's rules, where the
compiler builds them, and the [open question](#open-questions). It is part of the [compilation](./compilation)
workstream, and its issues carry the `compilation` and `type checker` labels.

## Checking

Where a record of type `A` is given and type `B` is expected:

| `A` (given) | `B` (expected) | Rule |
|-------------|----------------|------|
| closed | closed | the same fields, each of the same type |
| closed | open | `A` has each of `B`'s fields; `B`'s row is `A`'s other fields |
| open | closed | each of `A`'s fields is one of `B`'s; `A`'s row is `B`'s other fields |
| open | open | the fields both name agree; each row is the other's extra fields and a row they share |

There is no width subtyping between closed records: a `(x: int, y: int, z: int)` is not a `(x: int, y: int)`.

## How it is built

- **Unification.** `unifyRecordFields` ([Unifier.fs](https://github.com/nanyx-lang/nanyx/blob/main/compiler/Nanyx.Compiler/Unifier.fs), in `unify`)
  applies the table above, in `Equal` and `Assignable` constraints alike. When two closed records differ, the error
  names the fields only one of them has.
- **Patterns.** A record pattern by member name names every field, or ends in `...`. `(name = n, ...)` tells
  inference the value is an open record with at least those fields (`openRecordWith` in
  [InferState.fs](https://github.com/nanyx-lang/nanyx/blob/main/compiler/Nanyx.Compiler/InferState.fs)). A tuple
  pattern's names, `(name, age)`, are closed too, and have no `...` form.
- **Types.** `TyOpenRecord` ([TypedAst.fs](https://github.com/nanyx-lang/nanyx/blob/main/compiler/Nanyx.Compiler/TypedAst.fs))
  is what reading a field of an unknown value gives, and is printed `(x: int, ...a)`. The parser's `typeRecord`
  ([Types.fs](https://github.com/nanyx-lang/nanyx/blob/main/compiler/Nanyx.Parser/Types.fs)) reads `...` or `...r`
  after a record type's named fields as a `TypeOpenRecord`, and `typeExprToTy`
  ([TypeResolution.fs](https://github.com/nanyx-lang/nanyx/blob/main/compiler/Nanyx.Compiler/TypeResolution.fs))
  makes it a `TyOpenRecord`: `...r` is the annotation's type variable `r`, and `...` a variable of its own. The
  formatter keeps both, a hover writes them `…` and `…r`, and completion offers the fields an open record lists.
- **Code generation.** A function whose parameter is an open record is generic in its row. `bindScheme` in
  [CoreIrLowerTypes.fs](https://github.com/nanyx-lang/nanyx/blob/main/compiler/Nanyx.Compiler.Wasm/CoreIrLowerTypes.fs)
  binds the row to the fields of the record it is used at, so each specialisation reads fields at fixed offsets. A
  function of several parameters given a record with more fields takes the field of each parameter's name
  (`parameterTypes`, and `fieldsAsArguments` in `CoreIrLowerArguments.fs`). An open record still open at code
  generation is reported as not supported.

## Open questions

- **Spread of an open record.** Record spread works on records whose fields are all known where the spread is
  checked (`RecordSpread.fs`); a spread of an open record, or of a value whose type is not known yet, is `NYX0231`.
  `(...p, z = 1)` where `p` is `(x: int, ...r)` could be `(x: int, z: int, ...r)`, unless `r` has a `z`. Whether a
  spread may override a field that is only in a row needs deciding.
