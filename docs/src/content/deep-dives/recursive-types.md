---
title: "Recursive types"
description: "How the compiler checks and compiles types that name themselves or each other"
order: 24
---

A type alias can name itself in the payload of one of its tags, a nominal type can name itself anywhere, and types can
name each other in the same way ([Recursive types](../tour/types#recursive-types)). This page says how the type checker
holds such a type, which would otherwise never end, and how the WebAssembly backend lays it out.

# Checking

- **A type that names itself is closed.** An alias's type holds what it stands for (`TyAlias`), and a nominal type's
  what it is made from (`TyNominal`), so one that held itself would be infinite. A recursive one holds its definition
  closed instead, `TyRecursive(body, args)`. In `body` each of its parameters is a `TyParam` of its position and each
  place it names itself a `TySelf` of the arguments written there, so `body` has no type variables
  (`RecursiveAliases.close`, `RecursiveNominals.close`). A pass that walks a type (substitution, free variables, the
  occurs check) finds the variables in `args` and ends.
- **It is unfolded where it is looked through.** `RecursiveAliases.unfold` and `RecursiveNominals.underlying` give what
  a reference stands for, one level deep: `body` with each `TyParam` its argument and each `TySelf` a reference closed
  again.
- **A definition is read with a marker.** While its body is read, the type's name stands for a marker
  (`RecursiveAliases.selfMarker`, `RecursiveNominals.selfMarker`), which closing turns into `TySelf`. An alias that names
  itself outside a tag's payload is `NYX0228` (`RecursiveAliases.unguardedReferences`): a value of it would hold itself
  with no way to end. A nominal type can name itself anywhere, since only its constructor makes a value of it.
- **Types that name each other are closed as a group.** `TypeResolution.inDependencyGroups` finds the module's type
  definitions that name each other, its strongly connected groups, each after the groups it names. A group of aliases
  and nominal types, none of them a context or a name already in scope, is read with a marker for each member
  (`RecursiveAliases.memberMarker`), then closed together (`registerTypeGroup`): each member holds
  `TyRecursive(TyGroup(selected, members), args)`, where `members` are the closed definitions of all of them, each name
  of a member in them a `TyMember` of its position, and `selected` is which member this one is. Unfolding a member
  (`RecursiveAliases.unfoldMember`) turns each `TyMember` into a reference to that member, closed over the same group
  (`RecursiveAliases.memberReference`). A group whose aliases name each other with no tag in between is `NYX0228` at
  each name on the cycle (`RecursiveAliases.unguardedCycles`), and its definitions are read one at a time, as any other
  group that can't be closed is.
- **Two references agree by what they stand for.** Two references to one recursive type, the same body at the same
  arguments, are the same type. Otherwise the unifier compares what they stand for, unfolding both, and takes the pair to
  agree while it does, so a pair met again inside them agrees if nothing else differs (`unfoldBoth` in `Unifier.fs`).
  That is why two aliases of the same shape, `Expr` and a `Term` with the same tags, are the same type.

# Compiling

- **The backend unfolds a recursive type into `IrRecursive`.** `CoreIrLowerTypes.toIrTypeIn` lowers a reference to a
  recursive type to `IrRecursive` of what it stands for, and a reference met again inside it, the same body at the same
  arguments, to `IrSelf` of how many recursive types out it is: `IrSelf 0` the innermost. A member of a group lowers in
  the same way, so `Expr` in a group with `Statement` is `IrRecursive` of a record that holds a list of
  `IrRecursive` of `Statement`'s record, in which `Expr` is `IrSelf 1`.
- **Code that reads what a value is made of reads `IrType.unfold`.** It replaces each `IrSelf` that stands for the type
  with the type itself, so the parts of a value are typed without free `IrSelf`s, and a value of a recursive type is
  laid out as its body is.
- **A type that holds itself at other arguments isn't compiled.** `#node(T(list(a)))` inside `T(a)` would need ever
  more types, so `toIrTypeIn` reports `NYX0301` after eight levels of it.
