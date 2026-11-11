---
title: "Higher-order functions"
description: "Passing and returning functions"
order: 5
---

A higher-order function takes a function as an argument or returns one. Nanyx treats functions as values, so higher-order programming is idiomatic.

# Passing functions

The simplest higher-order pattern is passing a named function to another function. This keeps reusable logic (`add1`) separate from traversal logic (`map`) and makes call sites easier to scan.

```nanyx
def add1 = { x -> x + 1 }

def numbers = [1, 2, 3]

def biggerNumbers = numbers \map(add1)
```

# Trailing Lambdas

When the final argument is a function, Nanyx supports a trailing-lambda style. Use the full form when you want explicit types (see [Annotating lambda parameters](./functions#annotating-lambda-parameters)), and the inline form for short local transformations.

```nanyx
-- Full lambda

def f = { (x: int): int -> x * 2 }

-- Inline lambda

def biggerNumbers = numbers \map { x -> x + 1 }
```

# Shorthand lambdas

The shorthand form omits the parameter and lifts members into scope:

```nanyx
def names = people \map { .name }

def increment = numbers \map { + 1 }
```

# Pattern matching in lambdas

Lambdas can use pattern-matching syntax directly, which is useful when transforming tagged unions. This keeps branching logic close to the point where values are mapped.

```nanyx
things \map {
  | #none -> 0
  | #some(_) -> 1
} \sum
```

A parameter can also be a pattern, which takes the argument apart as it is passed in:

```nanyx
def sums = pairs \map { (a, b) -> a + b }

def address: $Email -> string = { $Email(s) -> s }
```

As with a [destructuring binding](./pattern-matching#destructuring-bindings), a pattern written as a parameter must be _total_: it must match every value the lambda can be given. To handle several cases, use the `| pattern -> ...` form above instead.

# Contextual lambdas

Higher-order functions can accept lambdas that require a context. This lets APIs parameterize the capability available inside the callback.

```nanyx
list.select: (list(a), [Yield(b)] (a -> ())) -> list(b)
```

In the type above, the callback `(a -> ())` runs with a `Yield(b)` context, so it can call `yield` while processing each element.

```nanyx
def names: list(Person) -> list(string) = { people ->
  people \list.select { person ->
    if person.isActive then yield(person.name)
  }
}
```

This pattern is called a **contextual lambda**: the higher-order function decides which context to provide, while the lambda body uses that context implicitly.

## Parametric contexts

Sometimes, a higher-order function wants to be flexible about what kinds of contexts.

For example, what context does a `list.map` function run in? It could be a pure function, but it could also be a stateful computation or an effectful operation. To support all of these, `list.map` can be defined with a parametric context:

```nanyx
list.map: [c] (list(a), [c] a -> b) -> list(b)
```

This function takes a context parameter, written as a single lowercase character (just like an automatically introduced type variable, which must also be one character). This means that the caller can choose any context `c` for the callback, and `list.map` will work with it. The callback can be a pure function, a stateful computation, or an effectful operation, depending on the caller's needs.

```nanyx
def f: list(int) -> list(int) = { numbers ->
  -- Error: list.map inherits the [Console] context requirement from `println`, but `f` does not declare it
  numbers \list.map { n ->
    println(n)
    n + 1
  }
}
```

If we remove the context parameter and write `list.map` with a concrete context, like `[Console]`, then any lambda passed to `list.map` would require the `Console` context. This would make it impossible to use `list.map` with pure functions or functions that require different contexts, which is why parametric contexts are important for higher-order functions.

```nanyx
def f: list(int) -> list(int) = { numbers ->
  -- Ok, because the lambda passed to `list.map` is pure (i.e. has no context requirements)
  numbers \list.map { n ->
    n + 1
  }
}
```

```nanyx
def f: [Console] list(int) -> list(int) = { numbers ->
  -- Also okay, because `f` declares the [Console] context requirement that `list.map` inherits from `println`
  numbers \list.map { n ->
    println(n)
    n + 1
  }
}
```