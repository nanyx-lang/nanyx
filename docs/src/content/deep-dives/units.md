---
title: "Units of measure"
description: "How the compiler reads and checks dimensions, units and numbers with units"
order: 22
---

[Units of measure](../advanced/units-of-measure) describes the language, and [Units and
dimensions](../work-in-progress/units-and-dimensions) in Work in progress what is still open. This page says what
the compiler does with it today: it reads all of it, checks the declarations of dimensions and units, reads dimensions
in types, types numbers with units, types arithmetic on measured values, writes them as text, and reads derived
dimensions, `Length / Time`, written with `*` and `/`. Dimension variables and a spelling for exponents are a later
step.

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
exponent, `Length / Time` as `[Length, 1; Time, -1]` (see [Derived dimensions](#derived-dimensions)), and its storage. `Time(int)` in a type is
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
whatever value `ms` names. Each unit's `UnitInfo` has its dimension, a `Dimension` (see [Derived
dimensions](#derived-dimensions)), its module, whether it is exported, and its `Scale`: how many of its dimension's base
units it is, as an exact fraction (`Rational`), so that `mm` is `1/1000` of an `m` and whether a literal is a whole number
of base units can be decided. `Units.declare` works out each scale from the unit's definition, the multiple times the
scale of the unit it is a multiple of, which is one of the module's own or an imported one, and reports:

- a dimension of the module's with no `@baseUnit` unit, and a unit that is neither a base unit nor a multiple
  ([`NYX0286`](../standard-tools/errors#nyx0286));
- a second base unit, a base unit that is a multiple, and a base unit of a dimension another module declares
  ([`NYX0287`](../standard-tools/errors#nyx0287));
- units defined as multiples of themselves, through others or not ([`NYX0288`](../standard-tools/errors#nyx0288));
- a unit defined in another dimension's units ([`NYX0289`](../standard-tools/errors#nyx0289));
- a constrained dimension stored in a type with no `Num` instance, which `ContextInstances.hasInstance` looks for as a
  call does ([`NYX0290`](../standard-tools/errors#nyx0290));
- a unit nothing declares, in a definition or in a literal ([`NYX0291`](../standard-tools/errors#nyx0291));
- a unit of a derived dimension with no definition, or marked `@baseUnit` ([`NYX0297`](../standard-tools/errors#nyx0297));
- a unit defined as nothing, `0ms`, or divided by nothing ([`NYX0298`](../standard-tools/errors#nyx0298));
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

`Infer` types `500ms` as a measured value of `ms`'s dimension, `TyMeasure([Time, 1], s)`, and `90.0kmh` one of
`[Length, 1; Time, -1]`, whose storage `s` is recorded as
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
what it is at run time, and gives the result its dimension as the table in [Arithmetic](../advanced/units-of-measure#arithmetic) says: `Infer`
types the operator on operands of the storages' types, through the storage's own `+` (built in for a number type, the
instance's member for a generic storage), and wraps the result. So `t / 2` on a `Time(a)` is a `Time` of what `/`
gives on an `a`, its `fraction`, and `t / u` that `fraction` itself; the backend compiles the storage's operator on the
measured values, which are their storages. Prefix `-` is read the same way (`OperatorCalls.inferNegation`). A product
or quotient of two measured values, or a number divided by one, is of their derived dimension (see [Derived
dimensions](#derived-dimensions)). Two dimensions, a number with no unit beside a measured value in a sum, a difference
or a remainder, a `//` that would round a derived dimension, and a power, which has no syntax yet, are errors that say
so.

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
of a unit without its decimal point, `1500ms` for `1.5s`, and a number of base units as a float is, `1.5ns`. A number
within a millionth of a millionth of a whole one, relatively, counts as that whole one, as float arithmetic rounds: a unit
of a derived dimension is seldom a whole number of its base units, so `90.0kmh + 10.0mps` is `3.4999999999999996e-8` of
a metre a nanosecond, and is written `35mps`. Any other
storage, a type variable's among them, is written in the base unit through its own `ToString`, so the definition for a
`Time(a)` requires `ToString(a)`. A module that can't write its dimension's base unit writes a number of base units as
`dbg` does, `Time(1500000000)`.

# Derived dimensions

A derived dimension is written in a type as dimensions joined by `*` and `/`, with brackets: `Length / Time`,
`Time * Time`, `Length / (Time * Time)`. The parser reads them between a type and an intersection (`typeDimension` in
`DescentTypes`), from the left, on one line, as `TypeProduct` and `TypeQuotient`, so `Length / Time / Time` divides by
`Time` twice. A storage is written on each part, `Length(float) / Time(float)`, as a part is any type, so nothing new is
read for it; the type printer writes one so (`TypePrinting`), and `ContextInference` writes one in an annotation the same
way.

`TypeResolution` reads a product or quotient as the measured value of the product or quotient of its sides'
dimensions (`Dimension.multiply`, `Dimension.divide`), which add or take off exponents and keep the base dimensions in
order, each once, with none whose exponent is zero: `Length * Time / Time` is `Length`, and a dimension of nothing is a
plain number (`Dimension.measured`). Two measured values are of one type when their dimension lists and storages are one,
so `Speed(float)` and `Length(float) / Time(float)` are one type.

The parts share one storage. `DimensionSignatures` gives the bare parts of a derived dimension in a signature the
storage written on another part, or one variable of their own, so `Length / Time` is `Length(a) / Time(a)`. Two
storages written on its parts are [`NYX0295`](../standard-tools/errors#nyx0295), and a part that is no dimension,
`Length / int`, is [`NYX0296`](../standard-tools/errors#nyx0296).

`dimension Speed = Length / Time` names a derived dimension. `Units.withDimensionsAsTypes` registers it as the alias it
is, `type Speed(a) = Length(a) / Time(a)`, generic in its storage as its parts are, and `InferModule` registers it again
once the module's dimensions are marked, so its parts are read as dimensions. `TypeResolution` reads `Speed(float)` as
what it names, with no alias around it, and a storage written on a part of its declaration is `NYX0295`.

A derived dimension has units of its own, each defined from its parts' units, multiplied and divided:
`unit kmh: Speed = 1km / 1hr`. The parser reads the definition as unit literals joined by `*` and `/` on one line, from
the left, as `BinaryOp`s (`DescentTopLevel`). It has no base unit of its own, as its values are stored in its parts'
base units, so a unit of one that is marked `@baseUnit`, or not defined, is [`NYX0297`](../standard-tools/errors#nyx0297).
`Units.scales` measures the definition, giving each literal its unit's dimension and the written number times its scale,
and multiplying and dividing them as the definition does: `1km / 1hr` is of `Length / Time`, and is 1_000 metres over
3_600_000_000_000 nanoseconds, so a kilometre an hour is `1/3600000000` of a metre a nanosecond. A definition of
another dimension than the unit's is [`NYX0289`](../standard-tools/errors#nyx0289). A unit of a derived dimension is
a literal's unit as another is, so `90.0kmh` is `Length(float) / Time(float)`, stored as its number of the parts' base
units; `90kmh` in an `int` is no whole number of them, an error that names them, `m/ns`.

`MeasuredArithmetic.read` types `*` on two measured values as the storage's `*`, of the product of their dimensions, and
`/` as the storage's `/`, of their quotient, at the storage's fraction, so `100m / 3s` is not rounded. `//` rounds only
within one dimension or by a number.

A value of a derived dimension is written by its derived `toString` as one of a dimension is (see [Printing](#printing)),
in the largest of the dimension's units the module sees that it is a whole number of (`Units.seenOf`), imported ones
among them: `90.0kmh` is `90kmh`, or `25mps` where `unit mps: Speed = 1m / 1s` is seen too. A value no unit fits, and one
of a derived dimension with no units, is written in its parts' base units, joined by `*` and `/` with no spaces:
`100.0m / 9.58s` is `1.0438413361169103e-8m/ns`. A module that can't write a part's base unit writes it as `dbg` does,
`Length / Time(1.04e-8)`.

# Hover

`DimensionHover` writes what an editor shows on a dimension or a unit, and `Hover.unitAt` finds one at a position: the
unit of a number with a unit, a dimension's name in a type, and a `dimension` or `unit` declaration. A dimension, plain or
derived, shows its declaration and the units of it the module can write, `typed.Units`, from the smallest to the
largest, each as it is declared, `ms = 1_000us` or `kmh = 1km / 1hr`, with the multiple `UnitInfo.Multiple` keeps, and a
value written in one of them; a unit shows its declaration and how many base units it is. A measured value's own hover, `def timeout: Time(int)`, ends with its
dimension's units too (`Hover.hoverOf`).
