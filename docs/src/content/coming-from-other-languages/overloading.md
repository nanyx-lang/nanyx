---
title: "Overloading"
description: "Why Nanyx avoids overloaded function names"
order: 3
---

Many languages let multiple functions share the same name as long as their type signatures differ, a feature called _overloading_. Nanyx intentionally does not support it.

# Why Nanyx avoids overloading

Overloading looks convenient, but it adds costs that show up in everyday use:

- It reduces clarity at the call site. A single name can hide multiple meanings, so readers have to infer which one was chosen.
- It weakens error messages. When a call fails, the compiler has to explain a failed overload resolution rather than a direct mismatch.
- It makes refactors riskier. Adding a new overload can change which implementation existing code picks, even if nothing else changed.
- It complicates tooling. Jump-to-definition, auto-complete, and documentation become less precise because the name is ambiguous.
- It hinders or even blocks future language changes. Adding new features becomes harder when the compiler must resolve ambiguities across an expanding set of overloads. Language evolution slows as a result.

Nanyx prefers a single, explicit meaning for each function name. That keeps call sites readable and makes the language easier to reason about.

We find that Nanyx's optional/named parameters, tag unions, and pattern matching provide enough flexibility to express different behaviors without needing overloading. If you need different behavior based on types, you can use pattern matching or tag unions to achieve that without overloading. See [Option-like types](../advanced/option-like-types) for an example of how to abstract across different unions with a shared convention.

# Alternatives

Where definitions would normally be ambigous and result in a compiler error (such as when implementing a commonly-understood function for your type, like `map`), Nanyx encourages [attaching the function to the type](../advanced/attached-definitions).

This allows for disambiguation when needed while keeping the function name consistent with the domain concept. For example, instead of overloading a global `map` function, you can define `MyType.map` as an attached function on your type. As a convenience, the type name does not need to be specified when piping an instance of that type to the function in question:

```nanyx
type MyType = (label: string)

-- Create a function that is attached to the type MyType
def MyType.name = { t -> t.label }

def o = MyType(label = "o")

-- Use the attached function in a qualified way
def result1 = MyType.name(o)

-- Use the function in a pipeline without needing to specify the type name
def result2 = o \name
```