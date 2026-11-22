---
title: "Operator contexts"
description: "What is still open about operators read through the prelude's contexts"
order: 9
---

An operator on a value of a type variable reads one member of a context, and its context is the prelude's context with
the fewest members that has that member. The language docs describe this, and `Add(a)`, `Sub(a)`, `Num(a)` and
`Append(a)`, in
[Operators find their context](../advanced/contexts#operators-find-their-context), [Add(a)](../advanced/contexts#adda),
[Sub(a)](../advanced/contexts#suba), [Num(a)](../advanced/contexts#numa), [Append(a)](../advanced/contexts#appenda) and
[Operators](../tour/operators#arithmetic-and-comparison). This page keeps what is still open. How operators on measured
values are to be typed is on [Units and dimensions](./units-and-dimensions#arithmetic-on-measured-values).

# Not done yet

- **`==`, `<` and the other comparisons are found by name** (`WellKnownContexts`), not through
  `OperatorContexts`. They need more than the member: `<` reads `compare`, and both have instances the compiler
  derives from structure. They are inferred for a function that does not write its type.
- **A local function that is generic in its arithmetic without writing its type.** A local def that writes a
  requirement, such as `def f: [Add(a)] a, a -> a`, is passed the instance at each type it is called with, and one
  that calls a function that requires a parameterised context is inferred to require it, but an operator in a local
  def that does not write its type is not inferred to need its context: the def is not generic in it, and its
  arithmetic is on ints or floats.
- **`0` elsewhere in generic code.** A `0` that is not an operand of an arithmetic operator or a comparison beside a
  value of the type variable, such as the start of a `fold`, a function's result or an argument, is an `int`;
  write `zero` there, as `math.abs` does for its result. So `{ xs -> xs \list.fold(0, { a, b -> a + b }) }` is
  `list(int) -> int`. Literals are typed before the type they are expected to have is known, so reading them by their
  expected type needs a pass after the def is inferred.
- **A user context can give `+` another meaning.** An operator calls whatever its name is bound to on a type
  variable, so a user's `context Sum(a) = (`` `+` ``: ...)` works for `x + y` in a function that requires
  `Sum(a)`. The design is that `+` always reads `Add`.
