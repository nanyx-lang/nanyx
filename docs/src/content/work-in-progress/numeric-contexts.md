---
title: "Numeric contexts"
description: "Add, Num, the arithmetic operators and number literals in generic code"
order: 9
---

> **Status: agreed, steps 1 to 5 of the [order of work](#order-of-work) done.** It answers the questions in
> [#606](https://github.com/nanyx-lang/nanyx/issues/606) and picks up where
> [Equality and well-known contexts](./equality-and-well-known-contexts#numeric-operators) stops. The decisions are
> listed under [Decided](#decided), and the work is filed under [Order of work](#order-of-work).
> [What is implemented](#what-is-implemented) says where the code is now; the references under
> [What existed before](#what-existed-before) are at commit `af48c0a`, before step 1.

# What is implemented

`Nanyx.Compiler/ArithmeticOperators.fs` holds the checks and the names; `OperatorContexts.fs` finds the prelude's
context an operator is read through; `OperatorCalls.fs` decides which operators call a definition; `ContextShapes.fs` lets an instance of one context serve another whose members it has; the
prelude declares `Add(a)` and `Num(a)`.

- **`%` is checked** with the other arithmetic operators, and an operand already known not to be a number is
  reported naming the operator: `'string' cannot be used with %: it is not a number`. Floats have a remainder, the
  runtime helper `__nanyx_float_rem` ([#607](https://github.com/nanyx-lang/nanyx/issues/607)).
- **An operator on a type that attaches it calls the attached definition**: `v + w` on a `$Vector` calls
  `` $Vector.`+` `` ([#527](https://github.com/nanyx-lang/nanyx/issues/527)).
- **`Add(a)` is a prelude context**, with `zero`, `` `+` ``, `` `-` `` and `negate`. On a type variable of a
  function that requires it, `+` and `-` call its members, as an operator whose name is bound does. A function that
  writes its type and adds, subtracts or negates values of one of its variables without `[Add(a)]` is `NYX0205`:
  `'total' adds values of type 'a' with +, so its type must require Add(a): add [Add(a)] to the type of 'total'`.
- **The compiler's own `Add(int)` and `Add(float)`** are the members the prelude attaches to `int` and `float`
  (`int.zero`, `` int.`+` ``, ...), found by the usual synthesis by name. A `+` on an `int` is still the built-in
  operator, not a call; only generic code is passed the members.
- **Prefix `-x`** parses, as a call to the reserved `__negate` (`NegateSyntax`). On `int` it is `0 - x`, and on
  `float` `-0.0 - x`, which is IEEE 754's negation. On any other type it calls that type's attached `negate`, or the
  `negate` of the `Add` the function requires. In a shorthand lambda's body a leading `-` stays the infix operator,
  so `{ -x }` is `{ n -> n - x }`.
- **`0` reads as `zero`** where it is an operand of an arithmetic operator and the other operand is of a variable of
  the type the function writes, in a function that requires `Add` of it: `x + 0` (`InferState.LiteralMembers`).
- **`list.sum: [Add(a)] list(a) -> a`** ([#475](https://github.com/nanyx-lang/nanyx/issues/475)).
- **`Num(a)` is a prelude context**, with `Add`'s members and `` `*` ``, `` `/` ``, `` `%` `` and `fromInt`; `*`, `/`
  and `%` on a type variable call its members, and without `[Num(a)]` are `NYX0205`, as `+` is without `Add`. The
  compiler's own `Num(int)` and `Num(float)` are again the members the prelude attaches to `int` and `float`
  ([#610](https://github.com/nanyx-lang/nanyx/issues/610)).
- **Integer literals are `fromInt`** where `0` would be `zero` and the function requires `Num`: `x * 2` is
  `` `*`(x, fromInt(2)) ``. In a function that requires only `Add`, such a literal is `NYX0205`, `'1' is an 'a' here,
  which needs Num(a): add [Num(a)] to the type of 'inc'`, and a decimal literal there is `NYX0201`, `'0.5' cannot be
  an 'a': 'a' may be an integer type. Take the value as a parameter`. A `fromInt` is a call at run time, not folded
  to a constant.
- **A `Num` serves as an `Add`.** An instance in scope of a context that has every member of the one a call needs,
  at the same types, is found for it (`ContextShapes.wider`), so a `[Num(a)]` function can call `list.sum`.
- **`**` parses**, binding tighter than `*` and grouping to the right, and `x ** n` is read as a call to the
  prelude's reserved `__pow` (`ArithmeticOperators.inferPower`, `InferState.ReadAs`), which no module's own `pow`
  can shadow. The prelude's `pow` and `math.pow` are `{ b, n -> b ** n }`. `__pow` squares repeatedly, so it takes
  as many steps as the exponent has bits ([#611](https://github.com/nanyx-lang/nanyx/issues/611)).
- **`float.pow(x, y)`** takes any `y`: a whole one is `x ** y`, and any other `e ** (y * ln(x))`, with `exp` and `ln`
  written in the prelude from their series, since WebAssembly has neither. The prelude exports them as `__floatExp`
  and `__floatLn` for `nanyx/math`, which gives them as `math.exp` and `math.ln`
  ([#822](https://github.com/nanyx-lang/nanyx/issues/822)).
- **`math.abs: [Num(a) & Ord(a)] a -> a`**, and `math.min`, `max` and `clamp` require `[Ord(a)]`, so they take an
  `int`, a `float` or any other type with an order ([#186](https://github.com/nanyx-lang/nanyx/issues/186)).
- **An operator finds its context in the prelude**, by the member it reads, rather than in a table the compiler
  keeps, and **a function that does not write its type requires it**: `def add = { x, y -> x + y }` is
  `[Add(a)] a, a -> a`. A type can also require an operator by its member, `` [`+`: a, a -> a] ``. See
  [Operator contexts](./operator-contexts).
- **An instance built for a call in the prelude is only compiled when that call is**: the definitions it is made of
  are reached from the definition the call is in, not from every module (`Library.reachedItems`).

Not done yet:

- **A negative exponent on an integer type** is `1 / x ** -n`, which truncates, rather than stopping the program as
  the design says: `__pow` is generic, and cannot tell an integer type from a float without a member to ask.
- **`0` elsewhere in generic code.** A `0` that is not an operand of an arithmetic operator beside a value of the
  type variable, such as the start of a `fold`, a function's result or the other side of a comparison, is still an
  `int`; write `zero` there, as `math.abs` does in `compare(n, zero)`. A literal beside a comparison is
  [#678](https://github.com/nanyx-lang/nanyx/issues/678). Literals are typed before the type they are expected to
  have is known, so reading them by their expected type needs a pass after the def is inferred.
- **`use` does not change concrete arithmetic** (decision 6): an `Add(int)` loaded with `use` reaches generic code
  called from its block, but a concrete `1 + 2` there is still the built-in `+`.
- **A user context can still give `+` another meaning**: an operator calls whatever its name is bound to on a type
  variable, so a user's `context Sum(a) = (`` `+` ``: ...)` still works.

# What existed before

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
  [#607](https://github.com/nanyx-lang/nanyx/issues/607)). *Fixed in step 1:* `%` is checked with the other four
  (`ArithmeticOperators.fs`), and an operand already known not to be a number is reported naming the operator,
  `'string' cannot be used with %: it is not a number`. Floats have a remainder, the runtime helper
  `__nanyx_float_rem` (`WasmRuntime.fs`).
- **Generic code.** Where an operator's name is bound, as `` `+` `` is in a function that requires a user's
  `[Sum(a)]`, and its left operand is not of a built-in type, the operator calls that binding
  (`OperatorCalls.fs:37-43`). That is how [contexts.md](../advanced/contexts#contexts-as-typeclasses)'s `Sum(a)` and
  `Add(a)` work. Context members named in backticks parse (`prelude.nyx:61`), and so do attached definitions
  named in backticks. *Since step 2:* an operator on a type that attaches it, `` def $Vector.`+` = ... ``, calls
  that definition ([#527](https://github.com/nanyx-lang/nanyx/issues/527)).
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

def mean: [Num(a)] list(a) -> a = { xs -> sum(xs) / fromInt(list.length(xs)) }

def square: [Num(a)] a -> a = { x -> x * x }
```

`min`, `max` and `clamp` only compare, so they need [`Ord(a)`](./equality-and-well-known-contexts#ordering), not a
numeric context, and work on strings too. `abs` needs both: `[Num(a) & Ord(a)] a -> a`.

## Why two contexts

`Add` is what a sum needs, and it is all a **measured** value has: `3m + 4m` is a length, but `3m * 4m` is an area,
not a length, so a measured type cannot have a `` `*`: (a, a) -> a ``. Splitting the additive part off means
`list.sum` works on a list of durations, and `Num` stays a context with one type in and the same type out.

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
fractional exponent is `float.pow(x, y)`, a separate function on `float`.

**Alternative.** *A `` `**` `` member*, so each type supplies its own. Every instance would then have to write it,
including the user types that `Num` synthesis is meant to make cheap, for a function the prelude can write once.

# Literals

A literal already takes the numeric type its context expects ([sized numbers](./sized-numbers#literals)):
`def small: U8 = 200`. This proposal adds one case, a type variable:

- **Only in a function that writes its type.** Where the expected type is a variable `a` of the signature, `0` is
  `Add(a).zero` and any other integer literal `n` is `Num(a).fromInt(n)`. The literal adds that requirement, so the
  signature must have it.
- **Elsewhere nothing changes.** In a function without a signature, a literal is `int` (or `float`), as today, so
  `def inc = { x -> x + 1 }` stays `int -> int` and not `[Num(a)] a -> a`. (An operator with no literal beside it
  is another matter: see [Operator contexts](./operator-contexts#functions-that-do-not-write-their-type).)
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
| Mixed numeric types | `cannot add 'int' and 'float': convert one side, for example int.toFloat(n)` |
| User type missing a member | the existing `MissingContextMembers` message, naming `Vector.negate` |
| Literal that cannot fit, known at compile time | `'300' does not fit in U8 (0 to 255)` |

The language server's code action for the second and fourth adds the requirement to the signature, as it does for
`Eq`.

# What changes for existing code

- **Unannotated arithmetic with a literal is unchanged**: it still infers `int` or `float`. *Since changed for
  arithmetic on parameters alone:* `{ x, y -> x + y }` now requires `Add` of its parameters' type
  ([Operator contexts](./operator-contexts)).
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
- **`list.sum: [Add(a)] list(a) -> a`** (#475).
- **`OperatorCalls.fs`'s rule** (an operator calls whatever its name is bound to) is replaced by the lookup; a user
  context may no longer give `+` a meaning other than `Add`'s.

# Order of work

1. **Done.** Check `%` as the other arithmetic operators are, and give floats a remainder: a bug fix that needs none
   of the rest ([#607](https://github.com/nanyx-lang/nanyx/issues/607)).
2. **Done.** Attached operator definitions ([#527](https://github.com/nanyx-lang/nanyx/issues/527)), shared with
   `Eq`.
3. **Done**, but for `0` outside an operand and `use` on concrete arithmetic (see [What is
   implemented](#what-is-implemented)). `Add` in the prelude; `+` and `-` read through it; the compiler's own
   instances for `int` and `float`; prefix `-`; `0` as `zero` in annotated generic code. Adds `list.sum` (#475)
   ([#609](https://github.com/nanyx-lang/nanyx/issues/609)).
4. **Done.** `Num`; `*`, `/` and `%` read through it; `fromInt` literals. Makes `math.abs` generic, and `min`, `max`
   and `clamp` with `Ord` (#186) ([#610](https://github.com/nanyx-lang/nanyx/issues/610)).
5. **Done.** `**` and the prelude's `pow` ([#611](https://github.com/nanyx-lang/nanyx/issues/611)).
6. Instances for the sized numbers as they land ([#343](https://github.com/nanyx-lang/nanyx/issues/343)), and the
   dimension rules for `*` and `/` with units.

# Decided

1. **Two contexts, `Add` and `Num`.** `Add` has `zero`, `+`, `-` and `negate`; `Num` has those and `*`, `/`, `%` and
   `fromInt`; a `Num` satisfies an `Add`. (Rejected: one `Num`, which measured types cannot have; one context per
   operator, which needs multi-parameter contexts.)
2. **The names are `Add` and `Num`.** (Rejected: `Sum`, the docs' earlier example, which reads like the result of
   `list.sum`; `Additive` and `Numeric`, as Swift.)
3. **`/` and `%` are in `Num`**, truncating on integers and IEEE 754 on floats. (Rejected: separate integer and
   fractional division, which either breaks `7 / 2` or makes `/` mean two things.)
4. **Negation is a member of `Add`**; on unsigned types it stops the program for anything but zero. (Rejected: a
   separate `Negate` context that unsigned types lack.)
5. **Literals are generic only where the signature is written**: `0` is `zero`, other integers `fromInt(n)`, and a
   decimal literal at a type variable is an error. (Rejected: literal polymorphism everywhere; no literals in
   generic code.)
6. **`use` changes concrete arithmetic**, as it does `==`, so there is one rule across well-known contexts.
   operators.md is updated when the work lands. (Rejected: `use` reaches only generic code, which makes arithmetic
   the exception.)
7. **`**` takes an `int` exponent** and is the prelude's `pow`, not a member.
8. **Measured types have `Add` but not `Num`**, and `*` and `/` on them are typed by the dimension rules. (Rejected:
   contexts with output types.)
9. **No structural derivation** of `Add` or `Num` for records and tuples.
