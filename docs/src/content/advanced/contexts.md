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

You can also load a context for a single expression with `use ... in`. The context is loaded for that expression only, which can start on the next line:

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
context Combine(a) = (
  empty: a
  combine: (a, a) -> a
)

export def combineAll: [Combine(a)] list(a) -> a
  = { items -> items \fold(empty, combine) }
```

Inside `combineAll`, `empty` and `combine` are the members of the instance it is given, so it combines whatever type it is called with, as long as that type has a `Combine`.

The operators read through contexts of the prelude in the same way: `x + y` on values of a type variable calls the `` `+` `` of [`Add`](#adda), and `x == y` the `` `==` `` of [`Eq`](#eqa). On an `int`, a `float`, a `string` or a `bool`, an operator is always the built-in one, so a counter's `n + 1` in a generic function still adds ints.

Contexts with associated types, members whose value is a type, are a work in progress; see [Type members and reflection](../work-in-progress/type-members-and-reflection#associated-types).

# Common contexts

In practice, a small set of capability contexts appears frequently in generic APIs.

## Eq(a)

`Eq(a)` is the standard library's context for equality. `x == y` is read through it, and `x != y` is `not (x == y)`.

```nanyx
context Eq(a) = (
  `==`: (a, a) -> bool
)
```

Every type that holds no function has an `Eq`: the compiler compares records field by field, lists element by element, and tags by name and then payload. Functions have no equality, so comparing two functions, or two values that hold one, is a type error.

A function that compares values of one of its type variables requires `Eq` of it:

```nanyx
def contains: [Eq(a)] (list(a), a) -> bool = { xs, target ->
  xs \any { == target }
}
```

A function that does not write its type has the requirement inferred: `def same = { x, y -> x == y }` is `[Eq(a)] a, a -> bool`. Comparing a type built from a variable requires `Eq` of the variable, so `==` on two `list(a)` needs `Eq(a)`.

A nominal type can attach its own `==`, which `a == b` on it, and a function that requires `Eq`, then use for it:

```nanyx
def $Email.`==`: $Email, $Email -> bool = { a, b -> lower(a) == lower(b) }
```

Only a nominal or protected type can: a structural type, such as `type Point = (x: int, y: int)`, and a built-in type always compare by their structure, so attaching `==` to one is an error. `!=` cannot be attached to any type, because it is always `not (a == b)`.

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

## Add(a)

`Add(a)` is the standard library's context for values that add and subtract. `x + y` and `x - y` are read through it, and so is `-x`, which is its `negate`.

```nanyx
context Add(a) = (
  zero: a
  `+`: (a, a) -> a
  `-`: (a, a) -> a
  negate: a -> a
)
```

The compiler has its own for `int` and `float`. A function that adds values of one of its type variables requires `Add` of it, and in such a function a `0` added to one of those values is `zero`:

```nanyx
def total: [Add(a)] list(a) -> a = { items -> items \fold(zero) { + } }

def orZero: [Add(a)] a -> a = { x -> x + 0 }
```

`List.sum` is `total`. A function that writes its type and adds without `[Add(a)]` is an error that says to add it. Outside generic code nothing changes: `def inc = { x -> x + 1 }` is still `int -> int`. Any other type gets an `Add` by attaching the four members (see [Context synthesis](#context-synthesis)).

## Num(a)

`Num(a)` is the context for numbers: it has every member of `Add(a)`, and `x * y`, `x / y` and `x % y` are read through it too.

```nanyx
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

In a function that requires `Num(a)`, an integer literal beside a value of type `a` is `fromInt` of it, so `2` in `x * 2` is an `a`. A decimal literal there is an error, since `a` may be an integer type.

```nanyx
def square: [Num(a)] a -> a = { x -> x * x }

def mean: [Num(a)] list(a) -> a = { xs -> List.sum(xs) / fromInt(List.length(xs)) }
```

`mean` calls `List.sum`, which requires `Add(a)`: contexts are structural, and a `Num` has every member an `Add` has, so the `Num` it is given serves. On `int`, `/` truncates and `%` takes the sign of the left operand, so `mean([1, 2, 4])` is `2`; on `float` they follow IEEE 754. A measured type, such as a length, is to have an `Add` but no `Num`, because multiplying two lengths gives an area ([units and dimensions](../work-in-progress/units-and-dimensions)).

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

Such a function can be called with any type that has a `ToString`, the compiler's own included: `angled(3)` is `"<3>"`, and `angled(#red)` is `"<#red>"`. A tag with a payload has none, so `angled(#some(1))` is an error.

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

For example, given the prelude's `Add` context:

```nanyx
context Add(a) = (
  zero: a
  `+`: (a, a) -> a
  `-`: (a, a) -> a
  negate: a -> a
)
```

And a type with matching attached definitions:

```nanyx
type Point = (x: int, y: int)

def Point.zero = Point(x = 0, y = 0)

def Point.`+` = { a, b -> Point(x = a.x + b.x, y = a.y + b.y) }

def Point.`-` = { a, b -> Point(x = a.x - b.x, y = a.y - b.y) }

def Point.negate = { a -> Point(x = -a.x, y = -a.y) }
```

You can call `List.sum` on a `list(Point)` without explicitly loading an `Add(Point)` context -- the compiler sees that `Point.zero`, ``Point.`+` ``, ``Point.`-` `` and `Point.negate` satisfy the context and synthesizes it. The attached `+` is also what `p + q` on two points calls:

```nanyx
def points = [Point(x = 1, y = 2), Point(x = 3, y = 4), Point(x = 5, y = 6)]

def total = points \List.sum
-- total = Point(x = 9, y = 12)
```

This is analogous to how Haskell derives typeclass instances or how Rust resolves trait implementations. The key difference is that in Nanyx the "instance" is just a collection of attached definitions -- there is no separate declaration required.

This is also where the compiler's own `Add(int)` and `Add(float)` come from: the prelude attaches `zero`, `` `+` ``, `` `-` `` and `negate` to `Int` and `Float`, and a generic function called at `int` is passed those. A concrete expression like `1 + 2` is the built-in operator, not a call.

## When synthesis applies

Context synthesis occurs when:

- A function requires a context parameterized by a concrete type
- That type has attached definitions matching every member of the context
- The types of those definitions are compatible with the context's signatures

If any member is missing or has an incompatible type, synthesis fails and the compiler requires you to load the context explicitly. The error names the definition that is missing, such as `Point.+`, or the one whose type does not fit.

A context with more than one parameter is synthesized from the definitions attached to its first parameter's type. Its other parameters need not be known at the call: the definitions' types settle them. The prelude's [`FromString(a, e)`](../stdlib/standard-library#reading-from-text) is built for `int` from `Int.fromString`, whose type makes `e` the tag `#invalidInt`.

Each call looks for its instance in this order, and takes the first it finds:

1. An instance for that type loaded with `use` around the call, the nearest first.
2. An instance the type of the function around the call declares: a generic `def total: [Add(a)] list(a) -> a` passes its own `Add(a)` on to the functions it calls.
3. An instance synthesized from the type's attached definitions.

An instance is for one type: an `Add(int)` loaded with `use` does not stand in for an `Add(Point)`.

Since contexts are structural, a `use` of a record value loads it as an instance of each context it has the shape of, at the types its members settle. With `def addInt: Add(int) = (...)`, `use addInt` loads an `Add(int)`, as `use Add(int)(...)` would.

## Explicit loading takes priority

If you manually load a context with `use`, that instance is always used, even if attached definitions exist. This lets you override the default behavior when needed:

```nanyx
def reverseSum = {
  use Add(Point)(
    zero = Point(x = 0, y = 0)
    `+` = { a, b -> Point(x = a.x - b.x, y = a.y - b.y) }
    `-` = { a, b -> Point(x = a.x + b.x, y = a.y + b.y) }
    negate = { a -> a }
  )

  points \List.sum
}
```

A `use` of an `Add` reaches generic code called from its block, such as `List.sum`; it does not change a concrete `p + q` there yet ([numeric contexts](../work-in-progress/numeric-contexts#what-is-implemented)).

# Why contexts matter

- Effects are explicit: if a function touches IO or state, it must declare a context.
- You can swap implementations by loading different context instances.
- They enable typeclass-style generic programming without extra syntax.
