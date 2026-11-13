---
title: "Classes"
description: "How class-based designs map to modern Nanyx"
order: 2
---

Nanyx does not use classes or inheritance. Instead, it separates data, behavior, and encapsulation into explicit language features.

For people coming from C#, Java, TypeScript, or Kotlin, the mental model is:

- **class fields** → record fields,
- **instance/static methods** → regular functions or [attached definitions](../advanced/attached-definitions),
- **inheritance hierarchies** → composition and tagged unions,
- **private internals** → module boundaries and nominal (protected and private) types.

# Data: records instead of class instances

Use records to model object-like data:

```nanyx
type Point = (x: int, y: int)

def p = Point(x = 10, y = 20)
```

Records are structural and immutable by default. To "update an object," create a new value using `with`:

```nanyx
def moved = p with (
  x = p.x + 5
  y = p.y + 3
)
```

See [Records and tuples](../tour/records) for the full model.

# Behavior: functions and attached definitions

Behavior is defined separately from data:

```nanyx
type Point = (x: int, y: int)

def Point.translate = { point, dx, dy ->
  point with (
    x = point.x + dx
    y = point.y + dy
  )
}

def moved = Point.translate(p, 5, 3)
def moved2 = p \translate(5, 3)

def Point.origin = Point(x = 0, y = 0)
```

This gives method-like ergonomics without classes.

# Encapsulation: modules and type boundaries

Encapsulation is done with module exports and type boundaries, not class visibility modifiers.

- Keep construction/validation logic in a module.
- Expose smart constructors and helper functions.
- Use protected or private types when representation must be controlled.

See [Nominal Types](../advanced/nominal-and-protected-types).

# State and mutation

Nanyx supports mutation, but it is explicit and scoped through contexts (for example `memory { ... }`).

This keeps mutation local rather than making every instance implicitly mutable.

See [Memory](../advanced/memory) for details.

# Inheritance alternatives

Most inheritance use-cases map to one of these patterns:

- **Shared structure** → composition with records.
- **Shared behavior** → attached definitions and reusable functions.
- **Variant families / polymorphism** → tagged unions plus pattern matching.

This usually leads to APIs that are easier to read and evolve because behavior is explicit at call sites.

# Quick mapping

| Class-based concept | Nanyx approach |
|---|---|
| Class instance | Record value |
| Instance method | Attached definition or function |
| Static method | Namespaced function / attached definition |
| Constructor | Record literal or validated factory function |
| Inheritance | Composition + tagged unions |
| Private fields | Module boundary + private type |

If you are modeling OOP-style APIs, start with records + attached definitions first; add protected or private types when you need stronger boundaries.
