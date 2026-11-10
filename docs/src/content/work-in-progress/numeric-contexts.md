---
title: "Numeric contexts"
description: "Add, Num, the arithmetic operators and number literals in generic code"
order: 9
---

> **Status: proposal.** Nothing here is implemented. It answers the questions in
> [#606](https://github.com/nanyx-lang/nanyx/issues/606) and picks up where
> [Equality and well-known contexts](./equality-and-well-known-contexts#numeric-operators) stops. The decisions are
> listed under [Open questions](#open-questions). The code references under [What exists today](#what-exists-today)
> are at commit `af48c0a`.

# What exists today

- **Parsing.** `*`, `/` and `%` bind tighter than `+` and `-` (`Nanyx.Parser/Program.fs:714-722`). There is no `**`,
  although [operators.md](../tour/operators) lists it, and no prefix `-`: `-5` is one literal token
  (`Lexical.fs:26`), but `-x` does not parse, so `math.abs` writes `0 - n` (`stdlib/nanyx/math.nyx:15`).
- **Literals** have one type each, whatever is expected: an integer literal is `int` and a decimal one `float`
  (`Infer.fs:27-33`).
- **`+`, `-`, `*` and `/`** unify their operands and constrain them to a number: `float` when either side is already
  known to be one, `int` otherwise (`Infer.fs:1073-1080`, `1499-1503`). So `{ x, y -> x + y }` is
  `int, int -> int`, and a generic `sum: list(a) -> a` is rejected with "`a` would have to be `int`"
  ([#475](https://github.com/nanyx-lang/nanyx/issues/475)). `<` and the other orderings do the same (`1504-1508`).
- **`%`** has no rule of its own. It falls through to the last arm (`Infer.fs:1516`), which only unifies the
  operands, so `"a" % "b"` type-checks as a `string`; and `7.5 % 2.0` type-checks and then fails in the backend
  with `NYX0301`, because there is no float remainder (`Nanyx.Compiler.Wasm/Wat.fs:28-40`;
  [#607](https://github.com/nanyx-lang/nanyx/issues/607)).
- **Generic code.** Where an operator's name is bound, as `` `+` `` is in a function that requires a user's
  `[Sum(a)]`, and its left operand is not of a built-in type, the operator calls that binding
  (`OperatorCalls.fs:37-43`). That is how [contexts.md](../advanced/contexts#contexts-as-typeclasses)'s `Sum(a)` and
  `Add(a)` work. Context members named in backticks parse (`prelude.nyx:61`); attached definitions named in
  backticks do not ([#527](https://github.com/nanyx-lang/nanyx/issues/527)).
- **`Num(a)` does not exist.** The docs use it: [math.md](../stdlib/math)'s `abs`, `min`, `max`, `clamp` and `pow`
  are `[Num(a)]`, but `math.nyx` takes `int` only ([#186](https://github.com/nanyx-lang/nanyx/issues/186)), and
  [types.md](../tour/types) says numeric operations are generic over it.

# Proposal

Two well-known contexts in the prelude, in the sense the equality page sets: each operator is sugar for a member,
and the compiler provides the instances for the built-in numbers.

```nanyx
context Add(a) = (
  zero: a
  `+`: (a, a) -> a
  `-`: (a, a) -> a
  negate: a -> a
)

context Num(a) = (
  zero: a
  `+`: (a, a) -> a
  `-`: (a, a) -> a
  negate: a -> a
  `*`: (a, a) -> a
  `/`: (a, a) -> a
  `%`: (a, a) -> a
  fromInt: int -> a
)
```

| Syntax | Reads as |
|---|---|
| `a + b`, `a - b` | `` Add(T).`+`(a, b) ``, `` Add(T).`-`(a, b) `` |
| `-a` | `Add(T).negate(a)` |
| `a * b`, `a / b`, `a % b` | `` Num(T).`*`(a, b) ``, and so on |
| `0` where an `a` is expected, in generic code | `Add(a).zero` |
| any other integer literal there | `Num(a).fromInt(n)` |
| `a ** n` | `pow(a, n)`, a prelude function, `[Num(a)] (a, int) -> a` |

`Num(a)`'s members include all of `Add(a)`'s, and contexts are structural, so a function that requires `[Num(a)]`
can call one that requires `[Add(a)]`: the `Num` it was given has the shape of an `Add`. This is how `use` already
treats a record value; the proposal extends it to instances a signature declares. There is no hierarchy to declare.

```nanyx
def sum: [Add(a)] list(a) -> a = { xs -> xs \fold(0) { + } }

def mean: [Num(a)] list(a) -> a = { xs -> sum(xs) / fromInt(List.length(xs)) }

def square: [Num(a)] a -> a = { x -> x * x }
```

`min`, `max` and `clamp` only compare, so they need [`Ord(a)`](./equality-and-well-known-contexts#ordering), not a
numeric context, and work on strings too. `abs` needs both: `[Num(a) & Ord(a)] a -> a`.

## Why two contexts

`Add` is what a sum needs, and it is all a **measured** value has: `3m + 4m` is a length, but `3m * 4m` is an area,
not a length, so a measured type cannot have a `` `*`: (a, a) -> a ``. Splitting the additive part off means
`List.sum` works on a list of durations, and `Num` stays a context with one type in and the same type out.

**Alternatives.**

- *One `Num(a)` with every operator* (Haskell's `Num`, Scala 3's `Numeric`). One name to learn and write. But a
  measured type then has no instance at all, so `sum` of a list of lengths is impossible, and a user type such as a
  vector or money, which adds but does not multiply, must define a `*` it does not have.
- *One context per operator* (Rust's `Add`, `Sub`, `Mul`, `Div`, `Neg`, with an output type each). The most
  precise: `Mul(Length, Length)` can give `Area`. But it needs multi-parameter contexts and associated types, which
  Nanyx does not have, and signatures grow to `[Add(a) & Mul(a) & Div(a)]` for an average. Swift settled between the
  two, with `AdditiveArithmetic` and `Numeric`, which is this proposal.

## Division and remainder

`/` and `%` are members of `Num`, and mean what the type's own instance says:

- on integer types, `/` truncates towards zero and `%` takes the sign of the left operand, as today (`i32.div_s`,
  `i32.rem_s`). Division by zero stops the program, and so does the one overflowing case, the most negative value
  divided by `-1`, as [sized numbers](./sized-numbers#overflow) requires;
- on floating-point types they follow IEEE 754, and `%` is the truncated remainder (C's `fmod`): `7.5 % 2.0` is
  `1.5`.

So `mean` above truncates when called with ints, as `sum / count` does in C, Rust, Swift and F#.

**Alternative.** *Split division by kind* (Haskell): `Integral(a)` with `div` and `mod`, and `Fractional(a)` with `/`.
Generic code then cannot divide without saying which division it means, which prevents the truncating average. The
cost is two more contexts, and either `7 / 2` stops being valid on ints, breaking much existing code (the printing
docs' `pence / 100`), or `/` means one thing at a concrete type and another in generic code.

## Negation

Prefix `-x` is new syntax, read as `Add(T).negate(x)`. It binds tighter than every infix operator, as `not` does,
so `-x * y` is `(-x) * y`. A literal `-5` stays a literal.

`negate` is a member, not `zero - x`, because the two differ on floats: `-0.0` is negative zero, and `0.0 - 0.0` is
positive zero. On an unsigned type, `negate` of anything but zero is an overflow and stops the program, exactly as
`0 - x` does.

**Alternative.** *A separate `Negate(a)` context* (Rust's `Neg`, Swift's `SignedNumeric`) that unsigned types do not
have, so `-u` is a type error rather than a runtime one. More precise, but `0 - u` is still only a runtime error, so
the gain is small, and `abs` would need a third context.

## Powers

`**` is not a member. `a ** n` is the prelude's `pow: [Num(a)] (a, int) -> a`, written once with repeated squaring
over `*`. A negative exponent on an integer type stops the program; on a float it is `1.0 / (a ** -n)`. A
fractional exponent is `Float.pow(x, y)`, a separate function on `float`.

**Alternative.** *A `` `**` `` member*, so each type supplies its own. Every instance would then have to write it,
including the user types that `Num` synthesis is meant to make cheap, for a function the prelude can write once.

# Literals

A literal already takes the numeric type its context expects ([sized numbers](./sized-numbers#literals)):
`def small: U8 = 200`. This proposal adds one case, a type variable:

- **Only in a function that writes its type.** Where the expected type is a variable `a` of the signature, `0` is
  `Add(a).zero` and any other integer literal `n` is `Num(a).fromInt(n)`. The literal adds that requirement, so the
  signature must have it.
- **Elsewhere nothing changes.** In a function without a signature, a literal is `int` (or `float`), as today, so
  `def inc = { x -> x + 1 }` stays `int -> int` and not `[Num(a)] a -> a`.
- **A decimal literal** at a type variable is an error: `a` may be an integer type. Generic code that needs one
  takes a value as a parameter, or is written for `float`.

After monomorphisation every `fromInt(n)` is at a concrete type and is folded to a constant, so a generic `x + 1`
costs what `x + 1` on an int costs. A literal that does not fit the concrete type, `fromInt(300)` at `U8`, is an
overflow and stops the program, as a conversion that does not fit would.

`0` reads as `zero` rather than `fromInt(0)` so that additive code, which has no `fromInt`, can still write it: a
zero of any dimension is the same zero, so `sum` of lengths starting from `0` is right.

**Alternatives.**

- *Literal polymorphism everywhere* (Haskell's `fromInteger`). `x + 1` is generic even without a signature. But
  every unannotated arithmetic function becomes `[Num(a)]`, its errors move to its callers, and a default is needed
  for code that never fixes the type (Haskell's defaulting rules, F#'s `inline`). It changes the inferred type of
  most existing code.
- *No literals in generic code*: write `zero` and `fromInt(1)` (Rust's `T::zero()`, F#'s
  `LanguagePrimitives.GenericZero`, Swift's `.zero`). Nothing new for the type checker, but generic arithmetic reads
  badly, and Swift itself makes literals generic through `ExpressibleByIntegerLiteral`.
- *`sum` takes its zero*: `sum(xs, 0)`. Works with no literal rule at all, but every caller passes the same value, and
  `fold` already does this.

# Instances

Like `Eq` and `Ord`, `Add` and `Num` are found by the usual lookup: a `use`, then the enclosing signature, then
synthesis from attached definitions, then the compiler's own.

- **The compiler's own instances** cover `int`, `float` and each sized type (`I8` to `U64`, `F32`, `F64`; `int` is
  `I64`). They are not calls: after monomorphisation a `+` at `I32` is the checked `i32` addition the backend emits
  today. Each type's overflow rule is its instance's, so generic code inherits it.
- **Measured types** (`float(Length)`) get the compiler's own `Add`, and `Eq` and `Ord`, from the type they measure.
  They have no `Num`.
- **User types** get `Add` by attaching `zero`, `` `+` ``, `` `-` `` and `negate`, and `Num` by attaching the rest,
  through the existing synthesis by name. This waits on attached operator definitions
  ([#527](https://github.com/nanyx-lang/nanyx/issues/527)).
- **Nothing is derived from structure.** Records and tuples do not add field by field: a pair of ints is not
  obviously a vector, and a type that is one says so by attaching `+`.
- **`use` changes concrete arithmetic too**, as equality's decision 2 settled for `==`: `+` is always a lookup of
  `Add(T)`, so an `Add(int)` loaded with `use` changes `1 + 2` in its block. With monomorphisation this costs
  nothing at run time.

Mixing types is still a type error: `x + 1.5` with `x: int` does not convert either side.

# Units and dimensions

[Units and dimensions](./units-and-dimensions) needs `*` and `/` to change the type: `float(Length) / float(Time)` is
`float(Length/Time)`. No context of the form `(a, a) -> a` can say that, so:

- **`+`, `-` and `negate` on measured values** go through `Add`, which requires identical dimensions, as the units
  page already says.
- **`*` and `/` on measured values** are typed by the units page's dimension rules, by the compiler, not through a
  context: when either operand is measured, `a * b` is the underlying type's multiplication at the product of the
  dimensions. Scaling, `2.0 * 3m`, is the case where one dimension is `1`.
- **Generic code over dimensions** uses dimension variables, as the units page's `float(d) -> float(d^2)` does, not
  `Num`. A `[Num(a)] square` cannot be called with a length; a dimension-generic `square` can.
- **`%` on measured values** keeps the dimension (`7m % 2m` is `1m`); `**` with a literal exponent multiplies it, as
  the units page's `pow` does.

This answers the units page's question 3: the dimension is a parameter of the concrete numeric type, and `Num` is
the context of dimensionless numbers.

**Alternative.** *Contexts with an output type* (Rust's `Mul<Rhs, Output = ...>`), so `*` stays a context member and
units are ordinary instances. That needs multi-parameter contexts, associated types and instances that compute
dimensions, all to describe something the compiler has to know about anyway to unify `Length/Time` with
`Length*Time^-1`.

# Diagnostics

Each is reported by the type checker, with a span.

| Situation | Message (draft) |
|---|---|
| Arithmetic on a type with no instance | `'string' cannot be added with +: it has no Add. Use ++ to join strings` |
| Annotated generic function adds without a requirement | `'total' adds values of type 'a' with +, so its type must require Add(a): write [Add(a)] list(a) -> a` |
| `*` where only `Add` is required | `'scale' multiplies values of type 'a' with *, which needs Num(a), but its type requires only Add(a)` |
| Literal at a type variable without the requirement | `'1' is an 'a' here, which needs Num(a): write [Num(a)] in the type of 'inc'` |
| Decimal literal at a type variable | `'0.5' cannot be an 'a': 'a' may be an integer type. Take the value as a parameter` |
| A `[Num(a)]` function called with a measured type | `'square' requires Num(a), and 'float(Length)' has none: multiplying lengths gives an area. Write square over a dimension instead` |
| Mixed numeric types | `cannot add 'int' and 'float': convert one side, for example Int.toFloat(n)` |
| User type missing a member | the existing `MissingContextMembers` message, naming `Vector.negate` |
| Literal that cannot fit, known at compile time | `'300' does not fit in U8 (0 to 255)` |

The language server's code action for the second and fourth adds the requirement to the signature, as it does for
`Eq`.

# What changes for existing code

- **Unannotated arithmetic is unchanged**: it still infers `int` or `float`.
- **`%` is checked**, so `"a" % "b"` becomes a type error, and `%` on floats compiles.
- **[math.md](../stdlib/math)**: `abs: [Num(a) & Ord(a)] a -> a`; `min`, `max` and `clamp` become `[Ord(a)]`;
  `pow: [Num(a)] (a, int) -> a`. `math.nyx` follows (#186).
- **[contexts.md](../advanced/contexts)**: the `Sum(a)` sections become the prelude's `Add(a)`, used rather than
  declared; the synthesis example attaches `Point.zero`, `` Point.`+` ``, `` Point.`-` `` and `Point.negate`. The
  "Contexts as typeclasses" example, which declares its own `` `+` `` member, uses a context without an operator
  instead, since `+` now always reads `Add`. The paragraph saying built-in operators are attached definitions is
  replaced by the compiler's own instances.
- **[operators.md](../tour/operators)** lines 45 and 53: built-in operators are sugar for `Add` and `Num`, and `use`
  does change `1 + 2`. It gains prefix `-` and `**`.
- **[types.md](../tour/types)**, [contexts-vs-interfaces](../coming-from-other-languages/contexts-vs-interfaces) and
  [sized numbers](./sized-numbers#arithmetic) name `Add` and `Num` instead of a single `Num`.
- **`List.sum: [Add(a)] list(a) -> a`** (#475).
- **`OperatorCalls.fs`'s rule** (an operator calls whatever its name is bound to) is replaced by the lookup; a user
  context may no longer give `+` a meaning other than `Add`'s.

# Order of work

1. Check `%` as the other arithmetic operators are, and give floats a remainder: a bug fix that needs none of the
   rest ([#607](https://github.com/nanyx-lang/nanyx/issues/607)).
2. Attached operator definitions ([#527](https://github.com/nanyx-lang/nanyx/issues/527)), shared with `Eq`.
3. `Add` in the prelude; `+` and `-` read through it; the compiler's own instances for `int` and `float`; prefix `-`;
   `0` as `zero` in annotated generic code. Adds `List.sum` (#475).
4. `Num`; `*`, `/` and `%` read through it; `fromInt` literals. Makes `math.abs` and `math.pow` generic (#186);
   `min`, `max` and `clamp` wait on `Ord` ([#476](https://github.com/nanyx-lang/nanyx/issues/476)).
5. `**` and the prelude's `pow`.
6. Instances for the sized numbers as they land ([#343](https://github.com/nanyx-lang/nanyx/issues/343)), and the
   dimension rules for `*` and `/` with units.

# Open questions

Each is for the maintainer to decide.

1. **Two contexts, `Add` and `Num`.** `Add` has `zero`, `+`, `-` and `negate`; `Num` has those and `*`, `/`, `%` and
   `fromInt`; a `Num` satisfies an `Add`. [Recommended. Rejected: one `Num`, which measured types cannot have; one
   context per operator, which needs multi-parameter contexts.]
2. **The names.** [Recommended: `Add` and `Num`. Alternatives: `Sum`, the docs' current example, which reads like
   the result of `List.sum`; `Additive` and `Numeric`, as Swift.]
3. **`/` and `%` are in `Num`**, truncating on integers and IEEE 754 on floats. [Recommended. Rejected: separate
   integer and fractional division, which either breaks `7 / 2` or makes `/` mean two things.]
4. **Negation is a member of `Add`**; on unsigned types it stops the program for anything but zero.
   [Recommended. Alternative: a separate `Negate` context that unsigned types lack.]
5. **Literals are generic only where the signature is written**: `0` is `zero`, other integers `fromInt(n)`, and a
   decimal literal at a type variable is an error. [Recommended. Rejected: literal polymorphism everywhere; no
   literals in generic code.]
6. **`use` changes concrete arithmetic**, as it does `==`. [Recommended, for one rule across well-known contexts.
   Alternative: `use` reaches only generic code, as operators.md says today, which makes arithmetic the exception.]
7. **`**` takes an `int` exponent** and is the prelude's `pow`, not a member. [Recommended.]
8. **Measured types have `Add` but not `Num`**, and `*` and `/` on them are typed by the dimension rules.
   [Recommended. Alternative: contexts with output types.]
9. **No structural derivation** of `Add` or `Num` for records and tuples. [Recommended.]
