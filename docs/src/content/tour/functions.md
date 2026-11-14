---
title: "Functions"
description: "Defining and calling functions"
order: 4
---

Functions are first-class values in Nanyx, meaning they can be passed as arguments, returned from other functions, and stored in data structures.

Functions are written with braces, `{ ... }`, and optionally define parameters: `{ x -> ... }`. The parameter list is a comma-separated list of names, and the body is an expression that computes the result.

# Function basics

A function that takes no parameters (or, more specifically, take `unit` as input) is simply `{ ... }`:

```nanyx
def main = {
  println("Hello, world!")
}
```

You could also write it as 

```nanyx
def main = { () ->
  println("Hello, world!")
}
```

But this is almost never preferred, since the unit parameter list is implied when no parameters are specified.

The two differ in one way. A lambda written without parameters, `{ ... }`, that is checked against a function type
taking something other than unit, takes that argument and ignores it:

```nanyx
def apply: (int -> string) -> string = { f -> f(1) }

apply { "ignored" }        -- OK: the lambda is an int -> string that ignores its argument
apply { () -> "ignored" }  -- error NYX0201: Type mismatch: int vs ()
```

Written `{ () -> ... }`, the lambda says it takes unit, and it is an error to pass it where an `int` is given. With no
type to check it against, `def f = { 0 }` takes unit, as `def f = { () -> 0 }` does: both are `() -> int`. When the
input is a type variable that nothing has settled yet, `{ ... }` takes unit too.

All functions in Nanyx take exactly one argument and return exactly one result. The function type signature is `a -> b`, where `a` is the input type and `b` is the output type.

```nanyx
-- Simple function
def double: int -> int = { x -> x * 2 }

-- Calling the function
def result = double(21)  -- 42
```

# Multi-parameter functions

Since all functions take one argument, multi-parameter functions actually take a record (or tuple, see [Records and tuples](./records)) as their argument:

```nanyx
def add: (int, int) -> int = { x, y -> x + y }
-- The type (int, int) -> int means: takes a record of two ints, returns an int

-- Calling with multiple arguments
def sum = add(5, 10)  -- 15
```

# Unit type

The `()` type (unit) represents the absence of a meaningful value. It's used for functions that don't take input or don't return output:

```nanyx
-- No meaningful return value
def printHello: [Console] () -> () = {
  println("Hello")
  -- Don't need to write `return ()`
}

-- No parameters
def getMessage: () -> string = {
  "Hello, World!"
}
```

# Lambda expressions

Lambda expressions (also called anonymous functions) are created with braces:

```nanyx fragment
-- Explicit lambda
def print1 = { print("1") }

-- Lambda with parameter
def increment = { x -> x + 1 }

-- Lambda in higher-order function
def names = data \map { item -> item.name }
```

## Shorthand lambdas

Nanyx provides convenient shorthand syntax for common lambda patterns, including member access and binary operators:

```nanyx fragment
-- Property access
def names = users \map { .name }
-- Equivalent to: users \map { user -> user.name }

-- Binary operators
def doubled = numbers \map { * 2 }
-- Equivalent to: numbers \map { x -> x * 2 }

def incremented = numbers \fold(0) { + }
-- Equivalent to: numbers \fold(0) { x, y -> x + y }

-- Comparison operators
def adults = users \filter { .age > 18 }
-- Equivalent to: users \filter { user -> user.age > 18 }

-- The body goes on after the first operation
def evens = numbers \filter { % 2 == 0 }
-- Equivalent to: numbers \filter { x -> x % 2 == 0 }
```

A body that starts with an operator (`+`, `-`, `**`, `*`, `/`, `%`, `++`, `<`, `<=`, `>`, `>=`, `==` or `!=`) or with a `.` and a field name has left out its first operand, and that operand is the lambda's argument. The rest of the body follows as it would after any operand, with the usual precedence: `{ * 2 + 1 }` is `{ x -> x * 2 + 1 }`. Only the first operand can be left out. An operator alone, `{ + }`, takes two arguments, one for each side.

A leading `-` is always subtraction from the argument, so `{ - 1 }` is `{ x -> x - 1 }`. A lambda that takes no argument and returns a negative number writes the arrow: `{ -> -1 }`.

The argument has no name, so a name written in the body always means what it means outside the lambda: in `{ * x }`, `x` is the `x` around it.

# Parameters

## Named parameters

If you name parameters in the signature, callers _must_ pass those fields by name, and the function body must destructure them by name too. When the local name is the same as the field name, `field = field` can be shortened to just `field`:

```nanyx fragment
def replace: (string, pattern: string, replacement: string) -> string
  = { s, pattern, replacement -> ??? }

replace("Hello world", pattern = "world", replacement = "Nanyx")
```

Passing a named parameter by position is an error that says which names to write:

```nanyx fragment
replace("Hello world", "world", "Nanyx")
-- error NYX0218: 'replace' takes 'pattern' and 'replacement' by name, not by position: write 'pattern = ...' and 'replacement = ...'
```

This is one rule for every call and every record construction: a positional field is passed positionally, a named field is passed by name. See [Positional vs named inference](./type-inference-and-annotations#positional-vs-named-inference) for how this interacts with unannotated functions.

## Annotating lambda parameters

A lambda's parameters and result can be annotated inline when inference needs a nudge or when the types document intent:

```nanyx
def double = { (x: int): int -> x * 2 }
```

Without brackets, a parameter's annotation ends at the `->`, so `{ q: Point -> q }` takes a `Point` and returns it. A parameter of a function type has brackets around the type, `{ g: (int -> int) -> g(1) }`, or around the whole parameter, `{ (g: int -> int) -> g(1) }`.

Named fields can be destructured to a different local name with `field = localName`:

```nanyx skip=#443
def magnitude: (x: int, y: int) -> float = { x = innerX, y = innerY ->
  Math.sqrt(innerX * innerX + innerY * innerY)
}
```

# Callable values

Any value with a function type can be called like a function. A [map](../stdlib/prelude-maps) can be called too, and calling it is a lookup, which gives `#some(v) | #notFound`:

```nanyx
def dict: map(string, string) =
  ["Hello" => "Bonjour", "Goodbye" => "Au revoir"]

dict("Hello") -- #some("Bonjour")
dict("Hola")  -- #notFound
```

A map is not a function value yet: where a function is wanted, pass `{ word -> dict(word) }` (see [Maps and sets](../work-in-progress/maps-and-sets#open-questions)).

# Higher-order functions

Functions that take other functions as arguments or return functions are called higher-order functions:

```nanyx
-- Takes a function as an argument
def apply: ((a -> b), a) -> b = { f, x -> f(x) }

-- Returns a function
def makeAdder: int -> (int -> int) = { x ->
  { y -> x + y }
}

def add5 = makeAdder(5)
def result = add5(10)  -- 15
```

# Pattern-matching functions

Since a function that consists only of a pattern match on its arguments is so common, a shorthand syntax is available where functions can pattern match directly on their arguments:

```nanyx
-- Simple pattern matching function
def sumList: list(int) -> int = {
  | [] -> 0
  | [head, ...tail] -> head + sumList(tail)
}

-- Multiple arguments with patterns
def divide: (int, int) -> Result(int, #divideByZero) = { 
  | _, 0 -> #error(#divideByZero)
  | x, y -> #some(x / y)
}

-- Each arm has a pattern for each argument, except a lone `else`, which matches whatever they all are
def quadrant: (int, int) -> string = {
  | 0, 0 -> "origin"
  | 0, _ -> "on the y axis"
  | else -> "elsewhere"
}

-- Pattern matching with guards
def classify: int -> string = {
  | 0 -> "zero"
  | 1 -> "one"
  | n & { < 0 } -> "negative: {n}"
  | _ -> "other"
}
```

# Recursive functions

Top-level definitions in a module all share one scope (see [Declaration order](./modules#declaration-order)), so a top-level function can call itself without any extra syntax:

```nanyx
def factorial: int -> int = { n ->
  if n <= 1 then 1
  else n * factorial(n - 1)
}

def length: list(a) -> int = {
  | [] -> 0
  | [_, ...tail] -> 1 + length(tail)
}
```

A definition inside a function body is different: it is only in scope _after_ it has been defined, so its own body cannot see it. Use the `rec` keyword to make a local definition visible to itself:

```nanyx
def countdown = { n ->
  rec loop = { i ->
    if i > 0 then
      println("{i}")
      loop(i - 1)
  }

  loop(n)
}
```

## Tail recursion

A call is a _tail call_ when it is the **last** thing its function does: the function returns whatever the call returns, with nothing left to do afterwards. Nanyx guarantees that a tail call does not use up stack, so a function that recurses through tail calls runs in constant stack space, however many times it calls itself. It is a loop written as a function.

The guarantee covers every kind of call: a function calling itself, two functions calling each other, and calls through a closure or a context member. It is part of the language, not an optimisation, and holds whether or not a given compiler version optimises anything else.

Recursion that is _not_ in tail position gets no such promise. `factorial` and `length` above multiply or add after the recursive call returns, so each call keeps a stack frame, and a deep enough input runs out of stack. The [official linter](../standard-tools/official-linter#recursion-not-in-tail-position) points such calls out.

A common pattern is to nest a helper function that carries an accumulator:

```nanyx
def sum: list(int) -> int = { xs ->
  rec loop = {
    | [], acc -> acc
    | [head, ...tail], acc -> loop(tail, acc + head)
  }

  loop(xs, 0)
}
```

Here `loop` is internal to `sum`, and `acc` stores the running result. Each recursive step passes the updated accumulator forward, and the base case returns it directly.

## Tail position

A call is a tail call when it is in _tail position_:

- The body of a function is in tail position.
- When an `if` is in tail position, so are its branches.
- When a `match` is in tail position, so is the body of each arm.
- When a block is in tail position, so is its last expression.
- When `use C(...) in body` is in tail position, so is `body`.

A pipe `x \f(y)` is the call `f(x, y)`, so the same rules apply to it. Nothing else is in tail position: not the arguments of a call, the operands of an operator (`1 + f(n)`), an interpolated value (`"{f(n)}"`), the scrutinee of a `match`, an element of a list or field of a record (`[x, ...f(xs)]`), or the body of a `for` loop.

A [workflow](../advanced/workflows) block is a call to its builder, with the rest of the block passed to each keyword as a closure, and the same rules apply to that code.

# Function composition

Functions can be composed to create new functions:

```nanyx
def compose: ((b -> c), (a -> b)) -> (a -> c) = { f, g ->
  { x -> f(g(x)) }
}

def addOne = { + 1 }
def double = { * 2 }

def addOneThenDouble = compose(double, addOne)
def result = addOneThenDouble(5)  -- 12
```

# Currying

While Nanyx functions naturally take one argument, you can create curried-style functions:

```nanyx
def add: int -> (int -> int) = { x ->
  { y -> x + y }
}

def add5 = add(5)
def result = add5(10)  -- 15
```

The function a call gives back can be called straight away, without naming it first:

```nanyx
def add: int -> (int -> int) = { x ->
  { y -> x + y }
}

def result = add(5)(10)  -- 15
```

# Partial application

With records, you can simulate partial application:

```nanyx fragment
def process: (config: Config, data: Data) -> Result = { config, data ->
  -- processing logic
}

-- Create a partially applied version
def processWithConfig = { data -> process(myConfig, data) }
```

# Type Annotations for clarity

While type inference works well, annotating function signatures is recommended for exported functions:

```nanyx fragment
-- Without annotation (inferred)
def add = { x, y -> x + y }

-- With annotation (clearer, better errors)
export def add: (int, int) -> int = { x, y -> x + y }
```

Type annotations serve as documentation and help catch errors early.

# Pure functions

Functions without effects (no contexts) are pure - they always return the same output for the same input:

```nanyx
-- Pure function
def add: (int, int) -> int = { x, y -> x + y }

-- Effectful function (requires context)
def greet: [Console] string -> () = { name ->
  println("Hello, {name}!")
}
```

# Function examples

## Map Implementation

```nanyx
def map: (list(a), (a -> b)) -> list(b) = { xs, f ->
  match xs
    | [] -> []
    | [head, ...tail] -> [f(head), ...map(tail, f)]
}
```

## Filter implementation

```nanyx
def filter: (list(a), (a -> bool)) -> list(a) = { xs, predicate ->
  match xs
    | [] -> []
    | [head, ...tail] ->
        if predicate(head) then [head, ...filter(tail, predicate)]
        else filter(tail, predicate)
}
```

## Fold implementation

```nanyx
def fold: (list(a), b, ((b, a) -> b)) -> b = { xs, acc, f ->
  match xs
    | [] -> acc
    | [head, ...tail] -> fold(tail, f(acc, head), f)
}
```

# Polymorphic functions

In Nanyx a polymorphic function is simply one whose return type depends on 
the types of its argument(s)

The simplest polymorphic function is probably the identity function, i.e.
a function that does nothing other than return its input

```nanyx
def id = { x -> x }
```

Thanks to type inference we don't have to write type annotations for this
function or give it a specification, but if we wanted to it would look like
this:

```nanyx
def id: a -> a = { x -> x }
```

The lowercase `a`, when used in a type position, is a type variable (equivalent
to the concept of 'generics' in other languages). 

```nanyx
def head: list(a) -> #some(a) | #emptyList = {
  | []       -> #emptyList
  | [x, ...] -> #some(x)
}
```