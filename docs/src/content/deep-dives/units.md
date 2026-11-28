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
`@baseUnit` and `@storageUnit` are attribute items before the unit they mark (`UnitSyntax.unitsWithAttributes`).

# Dimensions are types

A dimension is registered as the private nominal type it is (`Units.withDimensionsAsTypes`): `dimension Time` as
`type Time(a) = private a`, generic in the number that stores it, and `dimension Bytes(int)` as `type Bytes = private
int`. Its `TypeDefInfo` is marked `IsDimension`, so it is exported, imported, aliased and selected as any type is.

A value of a dimension is a measured value, `TyMeasure`: its dimension, a list of base dimensions each with its
exponent, `Length / Time` as `[Length, 1; Time, -1]` (see [Derived dimensions](#derived-dimensions)), and its storage. `Time(int)` in a type is
`TyMeasure([Time, 1], int)`, `Bytes` is of the storage it is constrained to, and two measured values are of one type when
their dimensions are one and their storages are. The backend lays one out as its storage.

`DimensionSignatures` reads each def's signature, top-level or local, before a context's associated types are written
out (see `AssociatedTypes`). Where a dimension written without its storage, `Time`, is in the signature says what its
storage is (`Position`): those in the function's parameters, or in its contexts, share one storage variable, the
signature's implicit storage, named by the first letter the signature doesn't write; those in its result, or in a
value's type, are given a `_`, which the def's value settles as it does any `_` (see `DefAnnotations.settleHoles`); and
in the result of a def with no value, an `@extern` or a component's import, each is given a storage variable of its own.
Each storage variable requires `Num`, so `def overlap: Time, Time -> bool` is read as `[Num(a)] Time(a), Time(a) ->
bool`, and `def total: list(Time) -> Time` as `[Num(a)] list(Time(a)) -> Time(_)`. A storage variable that is not the
type of a parameter is [`NYX0293`](../standard-tools/errors#nyx0293), and a storage written for a constrained dimension,
`Bytes(a)`, is [`NYX0294`](../standard-tools/errors#nyx0294).

# Units

A unit is a name of a kind of its own, in `InferState.Units`, apart from values and types, so `500ms` means the unit `ms`
whatever value `ms` names. Each unit's `UnitInfo` has its dimension, a `Dimension` (see [Derived
dimensions](#derived-dimensions)), its module, whether it is exported, the attributes that mark it (`Marks`), whether it
is its dimension's default display unit (`IsDisplay`), and its `Scale`: how many of its dimension's default storage units
it is, as an exact fraction (`Rational`), so that whether a literal is a whole number of default storage units can be
decided.

`Units.declare` works out the scales in two steps. `Units.scales` measures each unit's definition, the multiple times the
scale of the unit it is a multiple of: a unit of one of the module's own dimensions to the one unit of it that is no
multiple, and any other to its dimension's default storage unit, as an imported unit is. Then `Units.storageUnits` picks
each own dimension's **default storage unit**: the unit `@storageUnit` marks, or else the largest unit that every unit
declared with the dimension is a whole number of, the greatest common divisor of their scales
(`Rational.greatestCommonDivisor`). If no declared unit is that one, it is made up: it has no name and no `UnitInfo`, and
messages write it as an exact decimal of the smallest unit it is one of, `0.2mm` for `mm` and `inch = 25.4mm`
(`Rational.smallestDecimalOf`, `UnitInfo.storageText`). There always is one, as every scale is a written decimal times
another unit's, so a decimal multiple of the unit the others are defined from, and so is their greatest common divisor.
Every scale is divided by it (`Units.inStorageUnits`), a
derived dimension's by each part's raised to the part's power, so a declared default storage unit's scale is one
(`UnitInfo.isStorage`), and where it is made up no unit's is. Only the units declared with a dimension count: a unit another module declares of it,
`unit tick: Time = 10ns`, is checked against the dimension's default storage unit and never changes it, as adding a
unit in one module must not change what every `Time(int)` holds, an `Instant`'s nanoseconds among them. The **default
display unit** is the unit `@baseUnit` marks, or else the default storage unit, or, where that is made up, the smallest
unit it is a decimal of. `Units.declare` reports:

- a dimension of the module's with no units, a second unit of one that is no multiple of another, and a unit of a
  dimension another module declares that is no multiple ([`NYX0286`](../standard-tools/errors#nyx0286));
- a second `@baseUnit` or `@storageUnit` of a dimension, and either on a unit of a dimension another module declares
  ([`NYX0287`](../standard-tools/errors#nyx0287));
- units defined as multiples of themselves, through others or not ([`NYX0288`](../standard-tools/errors#nyx0288));
- a unit defined in another dimension's units ([`NYX0289`](../standard-tools/errors#nyx0289));
- a constrained dimension stored in a type with no `Num` instance, which `ContextInstances.hasInstance` looks for as a
  call does ([`NYX0290`](../standard-tools/errors#nyx0290));
- a unit nothing declares, in a definition or in a literal ([`NYX0291`](../standard-tools/errors#nyx0291));
- a unit of a derived dimension with no definition, or marked `@baseUnit` or `@storageUnit`
  ([`NYX0297`](../standard-tools/errors#nyx0297));
- a unit defined as nothing, `0ms`, or divided by nothing ([`NYX0298`](../standard-tools/errors#nyx0298));
- a unit that is no whole number of the unit `@storageUnit` marks, and a unit declared outside its dimension's module
  that is no whole number of the dimension's default storage unit ([`NYX0300`](../standard-tools/errors#nyx0300));
- a unit of a name that is no dimension (`NYX0109`), and a unit declared twice (`NYX0113`).

An import brings in the units its module declares with `export unit` (`ModuleLinking.exportedUnits`), unqualified, since
a literal can't be written with a qualifier, or only those it selects. Two imports that bring in different units of one
name make it ambiguous (`InferState.AmbiguousUnits`), which is reported as `NYX0105` only where the name is written
(`Units.unitNamed`), unless the module declares a unit of that name itself.

The prelude declares `Time` and its units (`stdlib/nanyx/prelude-time.nyx`), so every module imports them. It marks
`ns` `@storageUnit`, as an `Instant` counts nanoseconds, and `s` `@baseUnit`. A module's
exported units are those of its `Units` that it declares with `export unit`, in any of its files if it is split across
files: an import marks the units it brings in as not exported (`ImportMerging`), so a module re-exports none. An
import's unit replaces the prelude's of its name rather than making it ambiguous.

# Numbers with units

`Infer` types `500ms` as a measured value of `ms`'s dimension, `TyMeasure([Time, 1], s)`, and `90kmh` one of
`[Length, 1; Time, -1]`, whose storage `s` is recorded as
a number literal's type is (`NumericLiterals.typeOf`), so it is the number the code around it expects, as a plain
literal's is. A constrained dimension's literal is of the dimension's storage. One nothing settles is an `int` when its
number of default storage units (`NumericLiterals.inStorageUnits`) is whole and fits one, however it is written, so
`500ms` and `0.5s` both are, and a `float` when it isn't, as `0.25ns` and `90kmh` are (`NumericLiterals.defaultType`).
Literals with units that are of one storage are a `float` together if one of them is, or a plain `float` literal is of
it, so that `2.0 * 1.5s` and `[0.5s, 0.25ns]` agree (`NumericLiterals.sharedDefaults`). A literal of the storage a `/`
gives, as the last `1s` of `10m / 2s / 1s` is, is defaulted after the others and the fractions they make
(`Fractions.settleLiterals`), so it takes the quotient's `fraction`: a `float` once `10m` and `2s` are `int`.

Once every type is known, `NumericLiterals.check` works out each one's number of default storage units exactly, the
written number
times its unit's `Scale`, and reads the literal as that plain number from then on (`ReadAs`), so the backend sees
`500000000` where `500ms` is written and lays the measured value out as its storage. In an integer storage the number
must be whole, so `1.5ns` is an error that says how to store it as a `float`, and it must fit the storage, as a plain
literal must. A plain literal where a measured value is expected is an error: a number with no unit is never a measured
value. `dbg` writes a measured value as its dimension around its number of default storage units, `Time(500000000)`,
and a derived dimension in brackets, `(Length / Time)(1.0)`, so that it doesn't read as `Length` over `Time(1.0)`
(`Dimension.applied`).

# Arithmetic

An operator attached to the left operand's type, a type of its own that is no number, is called whatever its operands
are, as any attached operator is (see `OperatorCalls`): `` Stamp.`+`: Stamp, Time(int) -> Stamp `` is what `s + 3s` calls,
and `Infer` asks for it before the units rule. Otherwise, `MeasuredArithmetic.read` types an arithmetic operator with a
measured operand as the operator on the storages, which is
what it is at run time, and gives the result its dimension as the table in [Arithmetic](../advanced/units-of-measure#arithmetic) says: `Infer`
types the operator on operands of the storages' types, through the storage's own `+` (built in for a number type, the
instance's member for a generic storage), and wraps the result. So `t / 2` on a `Time(a)` is a `Time` of what `/`
gives on an `a`, its `fraction`, and `t / u` that `fraction` itself; the backend compiles the storage's operator on the
measured values, which are their storages. Prefix `-` is read the same way (`OperatorCalls.inferNegation`). A product
or quotient of two measured values, or a number divided by one, is of their derived dimension (see [Derived
dimensions](#derived-dimensions)). Two dimensions, a number with no unit beside a measured value in a sum, a difference
or a remainder, a `//` that would round a derived dimension, and a power, which has no syntax yet, are errors that say
so.

Beside a value of a generic storage, a number with a unit is its number of default storage units as the storage's
`fromInt`, or
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
value's number of default storage units with the intrinsic `__nanyx_measured_storage`, which `Infer` types as its argument's
storage, since no scheme can name every dimension, and which the backend compiles as its argument, as a measured value
is its storage.

For an `int` storage, or a sized integer that converts to one, the definition calls the prelude's
`__measuredIntToString` with the number, the units' names and their scales, the default display unit's name, the units
again from the smallest where the default storage unit is made up, and the text to write around a number of default
storage units: it writes the number in the first unit it is a whole number of, `2min` for `120s`, zero in the default
display unit, `0s`, and a number no unit fits in the default storage unit. Where that is made up, a number no unit fits
is written as an exact decimal of the smallest unit it is one of instead: 13 of 0.2mm is `2.6mm`. A
`float` or `F32` storage calls `__measuredFloatToString`, which writes the number in the first unit its magnitude is at
least 1 of, `1.5s`, `2.0min` for `120s`, and in the last, the smallest, when it is under 1 of every unit, `0.25ns`,
as a float is written, and zero as `0.0s`. A number within a millionth of a millionth of a whole one, relatively,
counts as that whole one, as float arithmetic rounds: `90kmh + 10m / 1s` is `3.4999999999999996e-8` of a metre a
nanosecond, and is written `35.0m/s`. Any other storage, a type variable's among them, is written in the default storage unit through its own `ToString`, so the
definition for a `Time(a)` requires `ToString(a)`. A module that can't write its dimension's default storage unit writes
a number of default storage units as `dbg` does, `Time(1500000000)`.

The prelude's `Format` is derived for a measured value the same way, but only for one whose storage converts to a
`float` without loss: an `int`, a sized integer narrower than 64 bits, a `float` or an `F32` (`StructuralInstances.formattedShapeOf`).
The derived `tryFormat` calls the prelude's `__measuredTryFormat` with the number as a `float`, the spec, the names
and scales of the units a spec may name (the dimension's own, a derived dimension's own declared units among them), the
units to pick from when the spec names none (the same, or for a derived dimension its parts' display units multiplied
and divided), the default display unit's name, the text around a number of default storage units, and the dimension's
name for a `BadFormat`. The helper takes the unit named after the spec's last space, writes the number in it with
`float.tryFormat`'s reader, and appends the unit's name. A name no unit has is `#badFormat` at the name.

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
the left, as `BinaryOp`s (`DescentTopLevel`). It has no default storage or display unit of its own, as its values are
stored in its parts' default storage units, so a unit of one that is marked `@baseUnit` or `@storageUnit`, or not
defined, is [`NYX0297`](../standard-tools/errors#nyx0297).
`Units.scales` measures the definition, giving each literal its unit's dimension and the written number times its scale,
and multiplying and dividing them as the definition does: `1km / 1hr` is of `Length / Time`, and is 1_000 metres over
3_600_000_000_000 nanoseconds, so a kilometre an hour is `1/3600000000` of a metre a nanosecond. A definition of
another dimension than the unit's is [`NYX0289`](../standard-tools/errors#nyx0289). A unit of a derived dimension is
a literal's unit as another is, so `90kmh` is `Length(float) / Time(float)`, stored as its number of the parts'
default storage units, which is no whole number; where an `int` is expected it is an error that names them, `m/ns`.

`MeasuredArithmetic.read` types `*` on two measured values as the storage's `*`, of the product of their dimensions, and
`/` as the storage's `/`, of their quotient, at the storage's fraction, so `100m / 3s` is not rounded. `//` rounds only
within one dimension or by a number.

A value of a derived dimension is written by its derived `toString` in its parts' default display units, and never in
its own units, which only make values: `derive` gives `measuredText` the parts' default storage and display units the
module sees, and the names of the display units are joined as `Dimension.writtenWith` joins them, with `*` and `/`, no
spaces, and a divisor of more than one part in brackets. Their scale is the product of the parts' display units'
scales, each raised to its part's power. A `float` is multiplied by how many display units a storage unit is, which
rounds less than dividing, and written by `__measuredFloatToString` in that one unit: `100.0m / 9.58s` is
`10.438413361169102m/s`, and `90kmh` is `25.0m/s`. An `int` is written by `__measuredRatioToString`, given the scale
as a numerator and a denominator: in the display units if it is a whole number of them, `2000mm * 1000mm` is `2m*m`,
and otherwise in the parts' default storage units, the only ones it is exact in, `1500mm*mm`. A module that can't write
a part's default display unit writes the value as `dbg` does, `(Length / Time)(1.04e-8)`.

# Hover

`DimensionHover` writes what an editor shows on a dimension or a unit, and `Hover.unitAt` finds one at a position: the
unit of a number with a unit, a dimension's name in a type, and a `dimension` or `unit` declaration. A dimension, plain or
derived, shows its declaration and the units of it the module can write, `typed.Units`, from the smallest to the
largest, each as it is declared, `ms = 1_000us` or `kmh = 1km / 1hr`, with the multiple `UnitInfo.Multiple` keeps, and a
value written in one of them, marking the default storage and display units, `ns (storage)` and
`s = 1_000ms (display)`; a unit shows its declaration, with the attributes that mark it, and how many default storage
units it is. A measured value's own hover, `def timeout: Time(int)`, ends with its
dimension's units too (`Hover.hoverOf`).

# Definitions, references and renames

A dimension and a unit have symbols, so an editor's go to definition, find references and rename find them as they do any
name (`Definition`, `Refactorings.Rename`). The parser declares the name a `dimension` or `unit` line writes by the line's
item (`MeasureDeclaration`), and `Units.markDimensions` and `Units.declare` make the symbols from it: a dimension's is a
`TypeDefinition`, kept in its `TypeDefInfo.Symbol` as a type's is, and a unit's a `UnitOfMeasure`, kept in
`UnitInfo.Symbol`. Neither has a scope. An import brings in the symbols of the units it brings in, as it does a type's.

A type that names a dimension refers to it, and so do the dimension a `unit` line names, which the parser reads as a
`TypeName`, and each dimension a derived one names, which keeps its node when it is read as a type
(`Units.storedAs`). A number written with a unit refers to the unit (`recordUnitReference`), the unit's own part of it,
`km` in `2km`, being where the name is written (`Definition.nameSpan`), and so does each number in another unit's
definition. A unit is renamed in every number written with it, to a name a number can be written with: one that starts
with a letter, but not `e` or `E` and a digit, which write an exponent, and that is no unit in scope already.
