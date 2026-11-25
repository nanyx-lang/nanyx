---
title: "Units of measure"
description: "How the compiler reads and checks dimensions, units and numbers with units"
order: 22
---

[Units and dimensions](../work-in-progress/units-and-dimensions) in Work in progress has the design. This page says what
the compiler does with it today: it reads all of it, checks the declarations of dimensions and units, reads dimensions
in types, types numbers with units, types arithmetic on measured values, of one dimension each, and writes them as text:
derived dimensions, `Length/Time`, are a later step.

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
(`Units.unitNamed`), unless the module declares a unit of that name itself.

The prelude declares `Time` and its units (`stdlib/nanyx/prelude-time.nyx`), so every module imports them. A module's
exported units are those of its `Units` that it declares with `export unit`, in any of its files if it is split across
files: an import marks the units it brings in as not exported (`ImportMerging`), so a module re-exports none. An
import's unit replaces the prelude's of its name rather than making it ambiguous.

# Numbers with units

`Infer` types `500ms` as a measured value of `ms`'s dimension, `TyMeasure([Time, 1], s)`, whose storage `s` is recorded as
a number literal's type is (`NumericLiterals.typeOf`), so it is the number the code around it expects, as a plain
literal's is. A constrained dimension's literal is of the dimension's storage. One nothing settles is of the number it is
written as: `int` for `500ms`, `float` for `0.5s` (`NumericLiterals.defaultType`).

Once every type is known, `NumericLiterals.check` works out each one's number of base units exactly, the written number
times its unit's `Scale`, and reads the literal as that plain number from then on (`ReadAs`), so the backend sees
`500000000` where `500ms` is written and lays the measured value out as its storage. In an integer storage the number
must be whole, so `1.5ns`, and `1ps` where `ns` is the base, are errors that say how to store it as a `float`, and it
must fit the storage, as a plain literal must. A plain literal where a measured value is expected is an error: a number
with no unit is never a measured value. `dbg` writes a measured value as its dimension around its number of base units,
`Time(500000000)`.

# Arithmetic

`MeasuredArithmetic.read` types an arithmetic operator with a measured operand as the operator on the storages, which is
what it is at run time, and gives the result its dimension as the Arithmetic table on the design page says: `Infer`
types the operator on operands of the storages' types, through the storage's own `+` (built in for a number type, the
instance's member for a generic storage), and wraps the result. So `t / 2` on a `Time(a)` is a `Time` of what `/`
gives on an `a`, its `fraction`, and `t / u` that `fraction` itself; the backend compiles the storage's operator on the
measured values, which are their storages. Prefix `-` is read the same way (`OperatorCalls.inferNegation`). Two
dimensions, a number with no unit beside a measured value in a sum, a difference or a remainder, and a product,
quotient or power that would be of a derived dimension are errors that say so.

Beside a value of a generic storage, a number with a unit is its number of base units as the storage's `fromInt`, or
`zero`, and a plain number that scales a measured value is read as beside its storage
(`ArithmeticOperators.readLiterals`), so `t + 5ms` and `t * 2` hold in a `[Num(a)] Time(a)` function.

Every dimension has `Add` and `Sub`, `Units.contexts`, and a call finds a dimension's instance of them at its storage
(`Units.storageArguments`), so `[Add(a)] a, a -> a` sums times and `zero` is the zero of a dimension. It has no `Num`.
Equality and ordering compare a measured value as its storage (see `WellKnownContexts`); at a generic storage they
need the storage's `Eq` or `Ord`, which a signature writes.

# Printing

A measured value has no `toString` attached, so a hole of one, `"{t}"`, or a call that requires `ToString` of it, finds
one derived from its structure, as a record's is (`StructuralInstances`): a definition the compiler writes for the
type, with the dimension's units the module can write in it (`Units.seenOf`), the largest first. Its body reads the
value's number of base units with the intrinsic `__nanyx_measured_storage`, which `Infer` types as its argument's
storage, since no scheme can name every dimension, and which the backend compiles as its argument, as a measured value
is its storage.

For an `int` storage, or a sized integer that converts to one, the definition calls the prelude's
`__measuredIntToString` with the number, the units' names and their scales, and the text to write around a number of
base units: it writes the number in the first unit it is a whole number of, `2min` for `120s`, and zero, and a number
no unit fits, in the base unit. A `float` or `F32` storage calls `__measuredFloatToString`, which writes a whole number
of a unit without its decimal point, `1500ms` for `1.5s`, and a number of base units as a float is, `1.5ns`. Any other
storage, a type variable's among them, is written in the base unit through its own `ToString`, so the definition for a
`Time(a)` requires `ToString(a)`. A module that can't write its dimension's base unit writes a number of base units as
`dbg` does, `Time(1500000000)`.
