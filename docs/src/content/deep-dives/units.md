---
title: "Units of measure"
description: "How the compiler reads and checks dimensions, units and numbers with units"
order: 22
---

[Units and dimensions](../work-in-progress/units-and-dimensions) in Work in progress has the design. This page says what
the compiler does with it today: it reads all of it, checks the declarations of dimensions and units, and reads
dimensions in types, but it does not type a number with a unit yet.

# Reading

The lexer reads a number with a unit straight after it, `500ms` or `1.5e3ms`, as one `Number` token (see
[the parser](./parser)), and the parser reads it as a `UnitLit`: the number exactly as written, digits and a power of ten
(`WrittenNumber`), and the unit's name. `dimension` and `unit` are not reserved: they begin a declaration only at the
start of a top-level item with a name after them, as `UnitSyntax` says, and are read as `DimensionDecl` and `UnitDecl`.
`@baseUnit` is the attribute item before the unit it marks.

# Dimensions are types

A dimension is registered as the private nominal type it is (`Units.withDimensionsAsTypes`): `dimension Time` as
`type Time(a) = private a`, generic in the number that stores it, and `dimension Bytes(int)` as `type Bytes = private
int`. Its `TypeDefInfo` is marked `IsDimension`, so it is exported, imported, aliased and selected as any type is.

A value of a dimension is a measured value, `TyMeasure`: its dimension, a list of base dimensions each with its
exponent (step 2's `Length/Time` is one of the same kind), and its storage. `Time(int)` in a type is
`TyMeasure([Time, 1], int)`, `Bytes` is of the storage it is constrained to, and two measured values are of one type when
their dimensions are one and their storages are. The backend lays one out as its storage.

`DimensionSignatures` reads each def's signature, top-level or local, before a context's associated types are written
out (see `AssociatedTypes`): a dimension written without its storage, `Time`, is given a storage variable of its own,
named by the first letters the signature doesn't write, and each storage variable requires `Num`, so
`def overlap: Time, Time -> bool` is read as `[Num(a), Num(b)] Time(a), Time(b) -> bool`. A storage that is not the type
of a parameter is [`NYX0293`](../standard-tools/errors#nyx0293), and a storage written for a constrained dimension,
`Bytes(a)`, is [`NYX0294`](../standard-tools/errors#nyx0294).

# Units

A unit is a name of a kind of its own, in `InferState.Units`, apart from values and types, so `500ms` means the unit `ms`
whatever value `ms` names. Each unit's `UnitInfo` has its dimension's identity, its module, whether it is exported, and its
`Scale`: how many of its dimension's base units it is, as an exact fraction (`Rational`), so that `mm` is `1/1000` of an
`m` and whether a literal is a whole number of base units can be decided. `Units.declare` works out each scale from the
unit's definition, the multiple times the scale of the unit it is a multiple of, which is one of the module's own or an
imported one, and reports:

- a dimension of the module's with no `@baseUnit` unit, and a unit that is neither a base unit nor a multiple
  ([`NYX0286`](../standard-tools/errors#nyx0286));
- a second base unit, a base unit that is a multiple, and a base unit of a dimension another module declares
  ([`NYX0287`](../standard-tools/errors#nyx0287));
- units defined as multiples of themselves, through others or not ([`NYX0288`](../standard-tools/errors#nyx0288));
- a unit defined in another dimension's units ([`NYX0289`](../standard-tools/errors#nyx0289));
- a constrained dimension stored in a type with no `Num` instance, which `ContextInstances.hasInstance` looks for as a
  call does ([`NYX0290`](../standard-tools/errors#nyx0290));
- a unit nothing declares, in a definition or in a literal ([`NYX0291`](../standard-tools/errors#nyx0291));
- a unit of a name that is no dimension (`NYX0109`), and a unit declared twice (`NYX0113`).

An import brings in the units its module declares with `export unit` (`ModuleLinking.exportedUnits`), unqualified, since
a literal can't be written with a qualifier, or only those it selects. Two imports that bring in different units of one
name make it ambiguous (`InferState.AmbiguousUnits`), which is reported as `NYX0105` only where the name is written
(`Units.unitNamed`), unless the module declares a unit of that name itself. A unit of a literal the module knows is reported as
[`NYX0285`](../standard-tools/errors#nyx0285), until numbers with units are typed.
