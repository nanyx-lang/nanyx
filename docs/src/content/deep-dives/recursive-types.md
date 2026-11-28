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

# Variance

Each parameter of a nominal type has a variance, which `Variances` works out from the type's definition once its module
has read it (`TypeResolution.settleVariances`), and which the definition keeps (`TypeDefInfo.Variances`) and each
`TyNominal` of the type carries (`NominalFacts`). A parameter named only where a value of the type gives it out, in a
field, an element or a function's result, is covariant; one named only in a function's input is contravariant; one named
in both, in an argument of `marray`, `box` or `Type`, or in another nominal type's invariant argument, is invariant; and
one never named is unused. A type that names itself is read from its closed definition, its `TyParam`s and `TySelf`s, and
a group from its members' bodies, their `TyParam`s and `TyMember`s: each parameter starts unused and the definitions are
read again until no variance changes, which takes a few passes, as a variance only ever moves towards invariant.

The type checker reads them where one nominal type is given for another (`Unifier`'s assignability): a covariant or
unused argument is assignable one way, a contravariant one the other, and an invariant one must be the same, with no
`undefined` on one side only (`bottomClash`). `holdsBottom` looks through a covariant argument, so a `Stack(undefined)`
given for a `Stack(a)` is only a lower bound, as a `list(undefined)` is. A `TyNominal` made while its own definition is
read holds no variances yet, and reads as invariant; `NominalFacts` compare by privacy only, so it is still the same type.
A member of a group unfolds to `TyNominal`s with the facts its group holds, which are brought up to date with the
variances (`Variances.withGroupVariances`). A module that imports the type reads the variances from its definition, from
the checked package when it comes from one, and never works them out again: a private type's body is hidden from it.

# Compiling

- **The backend lowers a recursive type to a group, `IrRecursive(selected, bodies)`.** `CoreIrLowerTypes.toIrTypeIn`
  lowers a reference to a recursive type to the IR types of its group at the arguments its definitions name each other
  with (`recursiveGroup`): `Expr` in a group with `Statement` is a group of `Expr`'s record and `Statement`'s, with
  `selected` saying which is `Expr`. A type that names only itself is a group of one. A reference met again inside a
  body, the same definition at the same arguments, is `IrSelf(depth, groupMember)`: how many groups out it is, 0 the
  innermost, and where it is in that group. The group's types are in the order of their positions in the checker's
  group and then of their arguments, so a group is the same IR type whichever of its types it is reached from, and
  each type is lowered once, however many paths through the group lead to it (#3209). What it finds is kept for the
  module (`CoreIrLowerTypes`' `Converted`, by the module's contexts' layouts): each type's IR type, each group, and a
  group's types where no other group is around them, shared by every reference into it. Lowering asks for the IR type
  of every expression, so a module of a large group, as a syntax tree's, unfolded the group thousands of times:
  assembling `nanyx.lint`'s lint build took 37 seconds, and takes 3.6 with them kept, to the same code
  ([#3261](https://github.com/nanyx-lang/nanyx/issues/3261)).
- **Code that reads what a value is made of reads `IrType.unfold`.** It gives the selected body, with each `IrSelf` that
  stands for a type of its group that type, so the parts of a value are typed without free `IrSelf`s, and a value of a
  recursive type is laid out as its body is. A group's name in WebAssembly is a hash of its bodies, with which of them
  it is (`IrType.mangle`), so that each of its types' names isn't as long as all of them.
- **A type that holds itself at other arguments isn't compiled.** `#node(T(list(a)))` inside `T(a)` would need ever
  more types, so `toIrTypeIn` reports `NYX0301` once a group reaches 64 types, or eight levels of groups of one.
