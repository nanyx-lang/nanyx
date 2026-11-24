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

```nanyx fragment
-- Full lambda

def f = { (x: int): int -> x * 2 }

-- Inline lambda

def biggerNumbers = numbers \map { x -> x + 1 }
```

A trailing lambda is the call's last positional argument, however many arguments come before it in brackets. These two calls are the same:

```nanyx
def total = list.fold([1, 2, 3], 0) { acc, x -> acc + x }

def sameTotal = list.fold([1, 2, 3], 0, { acc, x -> acc + x })
```

# Shorthand lambdas

The shorthand form omits the parameter and lifts members into scope:

```nanyx fragment
def names = people \map { .name }

def increment = numbers \map { + 1 }
```

A lambda written with no parameters, `{ ... }`, ignores its argument where a function of one parameter is expected, so it can stand for a function that doesn't need its input:

```nanyx
def zeros = [1, 2, 3] \map { 0 }

println(zeros)
```

This prints `[0, 0, 0]`. Where nothing says how many parameters it takes, `{ ... }` is a function of none, `() -> ...`. To say it takes unit, write `{ () -> ... }`.

# Pattern matching in lambdas

Lambdas can use pattern-matching syntax directly, which is useful when transforming tagged unions. This keeps branching logic close to the point where values are mapped.

```nanyx fragment
things \map {
  | #none -> 0
  | #some(_) -> 1
} \sum
```

With several parameters, each arm gives a pattern for each of them, in order. The parameters can also be named before the arms, which keeps them in scope in every arm (unless an arm binds the same name itself):

```nanyx
def orElse = { o, fallback ->
  | #some(v), _ -> v
  | #none, _ -> fallback
}

def first = orElse(#some(1), 0)
def second = orElse(#none, 0)
```

An arm that gives the wrong number of patterns is an error.

A parameter can also be a pattern, which takes the argument apart as it is passed in:

```nanyx fragment
def sums = pairs \map { (a, b) -> a + b }

def address: Email -> string = { Email(s) -> s }
```

As with a [destructuring binding](./pattern-matching#destructuring-bindings), a pattern written as a parameter must be _total_: it must match every value the lambda can be given. To handle several cases, use the `| pattern -> ...` form above instead.

# Contextual lambdas

Higher-order functions can accept lambdas that require a context. This lets APIs parameterize the capability available inside the callback.

```nanyx fragment
memory: [c] ([Memory, c] () -> a) -> a
```

In the type above, the callback runs with a `Memory` context, so it can make and change [`mut` cells](../advanced/memory), and `memory` gives back what it returns. (`c` is a context variable, explained [below](#parametric-contexts): the callback may use the caller's contexts too.)

```nanyx fragment
def total: list(int) -> int = { xs ->
  memory {
    mut sum = 0
    for xs | x -> sum := sum + x
    sum
  }
}
```

This pattern is called a **contextual lambda**: the higher-order function decides which context to provide, while the lambda body uses that context implicitly.

A lambda with `yield` in it is a lazy sequence of the values it yields (see [Workflows](../advanced/workflows#yield-and-sequences)), so `list.select` takes one, and gives back every value it yields for each element:

```nanyx
def names = ["Ann", "", "Bo"] \list.select { name ->
  if name != "" then yield name
}

println(names)
```

## Parametric contexts

A function's type says which contexts it requires where it is called, and a lambda's type says which ones it uses: `{ n -> println(n) }` is a `[Console] int -> ()`. A function that requires no contexts is pure, and a pure function can be given wherever one that requires contexts is expected, but not the other way round.

So what context does a function such as `list.map` run its callback in? It could be pure, but it could also print or change memory. To take all of these, `list.map` has a parametric context:

```nanyx fragment
list.map: [c] list(a), ([c] a -> b) -> list(b)
```

`c` is a context variable, written as a single lowercase letter (just like an automatically introduced type variable, which must also be one character). It stands for whatever contexts the callback requires, and a call to `list.map` requires them too. The callback can be pure, or require `Console`, or anything else, depending on the caller's needs. The prelude's higher-order functions all take one.

```nanyx
def f: list(int) -> list(int) = { numbers ->
  -- Error NYX0211: the lambda calls `println`, which requires [Console], and `f` does not declare it
  numbers \list.map { n ->
    println(n)
    n + 1
  }
}
```

If `list.map` were written with a concrete context instead, like `[Console]`, every lambda passed to it would have to be allowed `Console`, and a call to it would require `Console` even with a pure lambda. Written with no context at all, it couldn't be given a lambda that prints. That's why parametric contexts matter for higher-order functions.

```nanyx
def f: list(int) -> list(int) = { numbers ->
  -- Ok, because the lambda passed to `list.map` is pure (it has no context requirements)
  numbers \list.map { n ->
    n + 1
  }
}
```

```nanyx
def f: [Console] list(int) -> list(int) = { numbers ->
  -- Also okay, because `f` declares the [Console] that `list.map` requires of its lambda here
  numbers \list.map { n ->
    println(n)
    n + 1
  }
}
```

A function that returns a function can leave a context variable to the function it returns, which binds it where it is called. Composition works this way: `f >> g` is a call to a function of type `([c] a -> b), ([c] b -> d) -> [c] a -> d`, so making the composition requires nothing, and calling it requires whatever `f` and `g` require, there. Only the returned function may call `f` and `g`; the function that makes it would need `[c]` in its own contexts to call them itself.

A higher-order function that doesn't write its type gets a context variable for each function it calls or passes on, so `def apply = { f, x -> f(x) }` is `[c] ([c] a -> b), a -> b`. One that writes its type without one only takes pure functions:

```nanyx
def runPure: (int -> int) -> int = { g ->
  g(5)
}

def main: [Console] () -> int = {
  -- Error NYX0227: this lambda requires [Console], and `runPure` only takes a pure function
  runPure { n ->
    println(n)
    n
  }
}
```