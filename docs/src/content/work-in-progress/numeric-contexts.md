---
title: "Numeric contexts"
description: "Add, Num, the arithmetic operators and number literals in generic code: what is decided and what is still open"
order: 9
---

`Add(a)` and `Num(a)`, the arithmetic operators read through them, `0` and integer literals in generic code, prefix
`-` and `**` are built ([#606](https://github.com/nanyx-lang/nanyx/issues/606)). The language docs describe them in
[Add(a)](../advanced/contexts#adda), [Num(a)](../advanced/contexts#numa) and
[Arithmetic and comparison](../tour/operators#arithmetic-and-comparison). The compiler's side is in
`ArithmeticOperators.fs`, `OperatorContexts.fs`, `OperatorCalls.fs` and `ContextShapes.fs`. This page keeps what is
still open, the design for units, and the decisions.

# What is still open

- **A negative exponent on an integer type** is `1 / x ** -n`, which truncates, rather than stopping the program as
  the design says: `__pow` is generic, and cannot tell an integer type from a float without a member to ask.
- **`0` elsewhere in generic code.** A `0` that is not an operand of an arithmetic operator or a comparison beside a
  value of the type variable, such as the start of a `fold`, a function's result or an argument, is still an `int`;
  write `zero` there, as `math.abs` does for its result. Literals are typed before the type they are expected to
  have is known, so reading them by their expected type needs a pass after the def is inferred.
- **`use` does not change concrete arithmetic** (decision 6): an `Add(int)` loaded with `use` reaches generic code
  called from its block, but a concrete `1 + 2` there is still the built-in `+`.
- **A user context can still give `+` another meaning**: an operator calls whatever its name is bound to on a type
  variable, so a user's `context Sum(a) = (`` `+` ``: ...)` still works. The design is that `+` always reads `Add`.
- **Units.** The sized numbers, `I8` to `U64` and `F32`, have their instances
  ([#343](https://github.com/nanyx-lang/nanyx/issues/343)). The dimension rules for `*` and `/` on measured values,
  below, are not built.

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

# Decided

1. **Two contexts, `Add` and `Num`.** `Add` has `zero`, `+`, `-` and `negate`; `Num` has those and `*`, `/`, `%` and
   `fromInt`; a `Num` satisfies an `Add`. `Add` is what a sum needs, and all a measured value has: `3m + 4m` is a
   length, but `3m * 4m` is an area. (Rejected: one `Num`, which measured types cannot have; one context per
   operator, which needs multi-parameter contexts.)
2. **The names are `Add` and `Num`.** (Rejected: `Sum`, the docs' earlier example, which reads like the result of
   `list.sum`; `Additive` and `Numeric`, as Swift.)
3. **`/` and `%` are in `Num`**, truncating on integers and IEEE 754 on floats. (Rejected: separate integer and
   fractional division, which either breaks `7 / 2` or makes `/` mean two things.)
4. **Negation is a member of `Add`**, not `zero - x`, because the two differ on floats: `-0.0` is negative zero, and
   `0.0 - 0.0` positive zero. On unsigned types it stops the program for anything but zero. (Rejected: a
   separate `Negate` context that unsigned types lack.)
5. **Literals are generic only where the signature is written**: `0` is `zero`, other integers `fromInt(n)`, and a
   decimal literal at a type variable is an error. (Rejected: literal polymorphism everywhere; no literals in
   generic code.)
6. **`use` changes concrete arithmetic**, as it does `==`, so there is one rule across well-known contexts.
   operators.md is updated when the work lands. (Rejected: `use` reaches only generic code, which makes arithmetic
   the exception.)
7. **`**` takes an `int` exponent** and is the prelude's `pow`, not a member. A negative exponent on an integer
   type stops the program; on a float it is `1.0 / (a ** -n)`.
8. **Measured types have `Add` but not `Num`**, and `*` and `/` on them are typed by the dimension rules. (Rejected:
   contexts with output types.)
9. **No structural derivation** of `Add` or `Num` for records and tuples.
