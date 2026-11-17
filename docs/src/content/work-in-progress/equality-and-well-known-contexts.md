---
title: "Equality and well-known contexts"
description: "What is still open about Eq, Ord, Hash and the contexts the compiler knows by name"
order: 8
---

Equality, ordering, hashing and printing through the well-known contexts `Eq`, `Ord`, `Hash` and `ToString` are
decided and built ([#532](https://github.com/nanyx-lang/nanyx/issues/532),
[#476](https://github.com/nanyx-lang/nanyx/issues/476), [#533](https://github.com/nanyx-lang/nanyx/issues/533),
[#1501](https://github.com/nanyx-lang/nanyx/issues/1501)). The language docs describe them:
[Common contexts](../advanced/contexts#common-contexts), [Eq(a)](../advanced/contexts#eqa),
[Ord(a)](../advanced/contexts#orda), [Hash(a)](../advanced/contexts#hasha),
[ToString(a)](../advanced/contexts#tostringa), [A type's own instance wins](../advanced/contexts#a-types-own-instance-wins)
and [Extending a type](../advanced/attached-definitions#extending-a-type). Whether maps and sets use the hash is on
[Maps and sets](./maps-and-sets). This page keeps what is still open.

# Not done yet

- **Ordering through each part, beyond one list.** `==` compares a tag, a tuple, a record or a list part by part, each
  part with its own `Eq` ([#631](https://github.com/nanyx-lang/nanyx/issues/631)), and `<` on a list of a type that
  attaches `compare` calls it. But a tuple holding a `Version`, or a list of lists of them, is still `NYX0205`, as if
  `Version` had no order, and `<` on a `list(a)` whose type is only known later orders by structure.
- **`Eq` of an open union passed to a function.** `==` on `#some(Email) | r` is derived through each tag it names, but
  a call that requires `Eq` of an open union, as `same(v, w)` with `v: #x(a) | r` does, is still passed the compiler's
  own.
- **The order of instances for a type variable.** A type's own instance comes first, then the one the enclosing
  signature declares, then one loaded with `use`. For a type variable the code still looks at a `use` before the
  signature's instance, the opposite of that order.
- **Attached definitions through an import's alias.** A type's own definitions are in scope wherever its values are,
  but a module the importer imports itself is reached as the import says, so after `import nanyx/text as t` an owned
  `string.trim` is `t.string.trim`, not in scope unqualified; it is reached through the alias as a value too
  ([#1833](https://github.com/nanyx-lang/nanyx/issues/1833)). Two visible types of one name (two modules' `Point`s)
  have their owned definitions left out, as an attached name says only the type's name.

# Still open

- Should a private type's module be able to withhold equality from other modules? [Recommended: no, not until
  someone needs it.]
