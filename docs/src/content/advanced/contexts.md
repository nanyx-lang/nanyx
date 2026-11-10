---
title: "Contexts"
description: "Implicit parameters for effects and typeclasses"
order: 2
---

Contexts are implicit parameters in Nanyx. A function can declare the contexts it needs, and the members of those contexts become available inside its body. This is how Nanyx models effects, state, and typeclass-style constraints.

# Declaring a context

A context definition looks like a record type, but uses the `context` keyword:

```nanyx
context Console = (
  println: string -> ()
)
```

Contexts are structural by default: any record whose members match the declared shape can be loaded as an instance. Prefixing the name with `$` (for example `$Memory`) makes the context nominal, so that only its home module can create instances; see [Nominal and protected types](./nominal-and-protected-types).

# Requiring a context

A function can specify required contexts in its type signature. Here, `sayHello` can call `println` because it runs in `Console`:

```nanyx
def sayHello: [Console] () -> () = {
  println("Hello, world!")
}
```

# Loading a context

Use the `use` keyword to load an instance of a context into scope. Once loaded, any functions that require it can be called:

```nanyx
def main = {
  use Console(
    println = { message -> hostPrint(message) }
  )

  sayHello()
}
```

You can also load a context for a single expression or block with `use ... in`:

```nanyx
def result = use Console(println = hostPrint) in
  println("Hello from a scoped context")

```

`use` loads the value of any expression. An instance of a parameterised context is a value whose type is the context applied to a type, so define it with `def` and load it by name:

```nanyx
context Show(a) = (show: a -> string)

def describe: [Show(a)] a -> string = { v -> Show(a).show(v) }

def numbered: Show(int) = (show = { n -> "#{n}" })

def main = {
  use numbered
  describe(42)   // "#42"
}
```

A parameter of the context's type, or a call that returns an instance, can be loaded the same way: `use instance`, `use numberedFrom(100)`.

# Contexts are part of the type

A value's context requirement is part of its type. You can annotate just the contexts and let the rest infer:

```nanyx
def greet: [Console] = {
  println("Hi")
}
```

A function that writes its type runs only in the contexts the type declares, so calling `println` from a `def f: int -> int` is an error. A function that does not write its type needs whichever of the runtime's contexts its body uses, and so does any function that calls it. The top level of a module runs in the runtime's contexts.

The same goes for a parameterised context. A function that does not write its type, and calls a function that requires `Show(a)` with a value of one of its own type variables, requires `Show` at that variable itself:

```nanyx
def describe: [Show(a)] a -> string = { v -> Show(a).show(v) }
def twice = { v -> describe(v) ++ describe(v) }   // [Show(α)] α -> string
```

Each call to `twice` then finds a `Show` for the type it is called with, as a call to `describe` does. A generic local `def` cannot have one inferred yet: write its type.

This is also how scoped mutation is enforced: values that still require `memory` cannot escape into code that does not provide that context. See [Memory: Values cannot escape memory scope](../advanced/memory#values-cannot-escape-memory-scope).

For a formal proposal of type-level context requirements, see [Work in progress: Context-qualified types](../work-in-progress/context-qualified-types).

# Combining contexts

You can require multiple contexts by combining them:

```nanyx
def readAndPrintFile
  : [Console & FileSystem] string -> ()
  = { path ->
    def contents = openFile(path).contents
    println(contents)
  }
```

# Console by default

The entry function (the one marked `@entry`, see [Attributes](../tour/attributes)) runs in `Console`, which is why `println` is available in `main` without extra setup.

# Contexts as typeclasses

Contexts can also express typeclass-style constraints. Define a context with the required operations and use it as a constraint:

```nanyx
context Sum(a) = (
  `0`: a
  `+`: (a, a) -> a
)

export def sum: [Sum(a)] list(a) -> a
  = { items -> items \fold(`0`) { + } }
```

Inside `sum`, the operator `+` calls the context's `` `+` ``, so `sum` adds whatever type it is given an instance for. An operator calls whatever its name is bound to where it is written, such as a member of a context the function requires, whenever its left operand is not of a built-in type: on an `int`, a `float`, a `string` or a `bool`, an operator is always the built-in one, so a counter's `n + 1` in the same function still adds ints.

```nanyx
context Add(a) = (
  `+`: (a, a) -> a
)

def add: [Add(a)] a, a -> a = { x, y -> x + y }
```

Contexts with associated types, members whose value is a type, are a work in progress; see [Type members and reflection](../work-in-progress/type-members-and-reflection#associated-types).

# Common contexts

In practice, a small set of capability contexts appears frequently in generic APIs.

## Eq(a)

Use `Eq(a)` when a function needs equality checks but should stay generic over the concrete type.

```nanyx
context Eq(a) = (
  `==`: (a, a) -> bool
)

def contains: [Eq(a)] (list(a), a) -> bool = { xs, target ->
  xs \any { == target }
}
```

## Default(a)

Use `Default(a)` when a function needs a fallback value for a type.

```nanyx
context Default(a) = (
  default: () -> a
)

def valueOrDefault: [Default(a)] #some(a) | #none -> a = {
  | #some(v) -> v
  | #none -> default()
}
```

## Sum(a)

`Sum(a)` is a common algebraic context for additive combination.

```nanyx
context Sum(a) = (
  `0`: a
  `+`: (a, a) -> a
)

def sum: [Sum(a)] list(a) -> a
  = { items -> items \fold(`0`) { + } }
```

These contexts compose naturally. For example, a generic algorithm can require both `Eq(a)` and `Default(a)` by declaring `[Eq(a) & Default(a)]`.

## ToString(a)

`ToString(a)` says how a value of type `a` is written as text. Unlike the contexts above, you don't declare it: the prelude does, and the compiler knows it, because every [interpolation hole](../getting-started/basics#strings) goes through it. `"Total: {total}"` writes `total` with the `ToString` of its type.

```nanyx
context ToString(a) = (
  toString: a -> string
)
```

The compiler has its own `ToString` for `int`, `float`, `bool`, `string` and tags without a payload. Any other type gets one by attaching a `toString`, which is [synthesized](#context-synthesis) into the context:

```nanyx
type Point = (x: int, y: int)

def Point.toString = { p -> "({p.x}, {p.y})" }

def message = "Moved to {Point(x = 1, y = 2)}"   // "Moved to (1, 2)"
```

A function that interpolates a value of one of its type variables requires `ToString` of it, as it would by calling any function that does. A function that writes its type says so; one that does not has it inferred:

```nanyx
def label: [ToString(a)] (string, a) -> string = { name, value -> "{name}: {value}" }
def angled = { value -> "<{value}>" }   // [ToString(α)] α -> string
```

A `use` changes how its block writes a type, generic code called from the block included:

```nanyx
def receipt = {
  def pounds: ToString(int) = (toString = { pence -> "£{pence / 100}.{pence % 100}" })
  use pounds
  "Total: {250}"   // "Total: £2.50"
}
```

The holes in the instance itself are checked before it is loaded, so they write their ints as usual.

A value whose type has no `ToString`, such as a list, a record, a function or a tag with a payload, is an error at its hole. Turn it into a string first, or print it with [`dbg`](../stdlib/io#dbg).

# Context synthesis

When a function requires a context, you don't always have to load or create one manually. If the type in question has [attached definitions](./attached-definitions) that match the context's members, the compiler can **synthesize** the context automatically.

For example, given a `Sum` context:

```nanyx
context Sum(a) = (
  `0`: a
  `+`: (a, a) -> a
)
```

And a type with matching attached definitions:

```nanyx
type Point = (x: int, y: int)

def Point.`0` = Point(x = 0, y = 0)

def Point.`+` = { a, b ->
  Point(x = a.x + b.x, y = a.y + b.y)
}
```

You can call `sum` on a `list(Point)` without explicitly loading a `Sum(Point)` context -- the compiler sees that `Point.``0``` and `Point.``+``` satisfy the context and synthesizes it:

```nanyx
def points = [Point(x = 1, y = 2), Point(x = 3, y = 4), Point(x = 5, y = 6)]

def total = sum(points)
-- total = Point(x = 9, y = 12)
```

This is analogous to how Haskell derives typeclass instances or how Rust resolves trait implementations. The key difference is that in Nanyx the "instance" is just a collection of attached definitions -- there is no separate declaration required.

This is also how arithmetic on the built-in types works. The operators on `int`, `float` and friends are ordinary attached definitions (see [Operators](../tour/operators)), so a concrete expression like `1 + 2` involves no context at all, and generic contexts such as `Sum(a)` or `Num(a)` are synthesized from those same definitions whenever a generic function is called at a concrete type.

## When synthesis applies

Context synthesis occurs when:

- A function requires a context parameterized by a concrete type
- That type has attached definitions matching every member of the context
- The types of those definitions are compatible with the context's signatures

If any member is missing or has an incompatible type, synthesis fails and the compiler requires you to load the context explicitly. The error names the definition that is missing, such as `Point.+`, or the one whose type does not fit.

Each call looks for its instance in this order, and takes the first it finds:

1. An instance for that type loaded with `use` around the call, the nearest first.
2. An instance the type of the function around the call declares: a generic `def total: [Sum(a)] list(a) -> a` passes its own `Sum(a)` on to the functions it calls.
3. An instance synthesized from the type's attached definitions.

An instance is for one type: a `Sum(int)` loaded with `use` does not stand in for a `Sum(Point)`.

Since contexts are structural, a `use` of a record value loads it as an instance of each context it has the shape of, at the types its members settle. With `def sumInt: Sum(int) = (...)`, `use sumInt` loads a `Sum(int)`, as `use Sum(int)(...)` would.

## Explicit loading takes priority

If you manually load a context with `use`, that instance is always used, even if attached definitions exist. This lets you override the default behavior when needed:

```nanyx
def reverseSum = {
  use Sum(Point)(
    `0` = Point(x = 0, y = 0)
    `+` = { a, b -> Point(x = a.x - b.x, y = a.y - b.y) }
  )

  sum(points)
}
```

# Why contexts matter

- Effects are explicit: if a function touches IO or state, it must declare a context.
- You can swap implementations by loading different context instances.
- They enable typeclass-style generic programming without extra syntax.
