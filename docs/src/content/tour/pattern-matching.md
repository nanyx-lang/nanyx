---
title: Pattern matching
description: Powerful control flow with match expressions
order: 6
---

While most languages have some kind of multi-way branching construct like `switch` statements, not all have pattern matching like Nanyx's `match`, which is far more powerful. Pattern matching allows you to extract nested values, check conditions, and handle different data shapes all in a single expression, making code more concise and safer by ensuring all possible cases are handled.

# Overview

The simplest form of pattern matching is matching against literal values, which is directly equivalent to a C-style `switch` statement:

```nanyx
match someValue
  | 0 -> "zero"
  | 1 -> "one"
  | _ -> "something else"
```

Note the use of `_` as a wildcard pattern that matches anything not previously matched. This may be required to ensure exhaustiveness (depending on the type being matched), and all match expressions must be exhaustive in Nanyx.

# Pattern order

Unlike a C-style `switch`, Nanyx's `match` patterns are evaluated in the order they are written. This allows for more flexible patterns and the ability to handle overlapping cases:

```nanyx
match value
  | 0 -> "zero"
  | 1 -> "one"
  | _ -> "something else"
```

A match that does not cover every possible value is a compiler error. An arm that can never be reached, because an earlier arm already matches everything it would, is not an error (the program is still correct), so it is reported by the [linter](../advanced/linters) as a warning instead.

# Introducing values

Patterns can also introduce new variables that are bound to parts of the matched value:

```nanyx
match someFunction()
  | 0 -> "zero"
  | 1 -> "one"
  | n -> "something else: {n}"
```

# Destructuring

Destructuring is a syntax that breaks a data structure into its components and binds them to variables. This is especially useful for working with tuples, lists, and custom data types.

## Lists

```nanyx
-- Match an empty list
match myList
  | [] -> "empty!"
  | [head, ...tail] -> "head is {head}"
```

In this example, the pattern `[head, ...tail]` matches a non-empty list, binding the first element to `head` and the rest of the list to `tail`. The `...` syntax (called the spread operator) is used to indicate that `tail` should capture all remaining elements of the list.

## Tuples

```nanyx
-- Destructure a tuple
def (x, y) = (10, 20)
```

Destructuring is available in pattern matching, allowing for simultaneous testing of data and extracting values at the same time:

```nanyx
match p
  | (0, y) -> "x is zero, y is {y}"
  | (x, 0) -> "y is zero, x is {x}"
  | (x, y) -> "x is {x}, y is {y}"
```

Note how the patterns `(0, y)` and `(x, 0)` use a combination of literal matching and variable binding to match specific shapes of the tuple. 

The final pattern `(x, y)` matches any tuple and binds both elements to variables.

## Records

Records can also be destructured in patterns, allowing you to match on specific fields while ignoring others:

```nanyx
match r
  | (name = "Alice", ...) -> "Found Alice"
  | (name, age) -> "{name} is {age} years old"
```

Here, the pattern `(name = "Alice", ...)` matches any record where the `name` field is "Alice", while ignoring all other fields. The second pattern matches any record with `name` and `age` fields, binding them to variables. Writing a named field as just `name` is shorthand for `name = name`, binding the field to a local of the same name.

As with tuples, records patterns must be of the correct shape to match the type being matched, and all fields must be accounted for either by matching them directly or using the spread operator to ignore them.

## Tags

Tags can also be destructured in patterns, using the same syntax that is used to construct them. For example, if you have a sum type representing shapes:

```nanyx
type Shape =
  | #point
  | #circle(float)
  | #rectangle(float, float)

def area: Shape -> float = { shape ->
  match shape
    | #point -> 0.0
    | #circle(r) -> 3.14159 * r * r
    | #rectangle(w, h) -> w * h
}
```

A tag that carries the unit value is matched as it is written, with empty brackets, and `()` on its own matches the unit value:

```nanyx
def describe: (#some() | #error(string)) -> string = {
  | #some() -> "saved"
  | #error(reason) -> "not saved: {reason}"
}
```

# Nesting patterns

Where pattern matching really shines is in its ability to nest patterns inside each other, allowing you to make complex data structure comparisons and extract values in a single expression. Below is an artificial complex example that demonstrates this:

```nanyx
type Result =
  | #some(int)
  | #error(string)

type Request = (
  id: int
  status: Result
  tags: list(string)
)

def processRequests: list(Request) -> string = { requests ->
  match requests
    | [] -> 
      "No requests"
    | [(id = 1, status = #some(value), tags = ["urgent", ...rest]), ...more] ->
      "Urgent request 1 with value {value}"
    | [(id = id, status = #error(msg), tags = []), (status = #some(val), ...), ...tail] ->
      "Error in request {id}: {msg}, followed by success"
    | [(status = #some(n), ...), ...rest] & requests & { .length > 5 } ->
      "Multiple requests with first one ok, total: {requests.length}"
    | _ -> 
      "Other pattern"
}
```

# Guards

Nanyx doesn't have guards in the traditional sense; instead it allows arbitrary function bodies to be used as patterns. This means you can write complex conditions directly in the pattern:

```nanyx
def describeNumber: int -> string = { n ->
  match n
    | { < 0 } -> "negative"
    | 0 -> "zero"
    | n & { > 100 } -> "{n}, which is large!"
    | _ -> "positive"
}
```

# Pattern-matching functions

Since a function whose entire body is a pattern match is so common, you can merge the function definition and match patterns together:

```nanyx
def sumList: list(int) -> int = {
  | [] -> 0
  | [head, ...tail] -> head + sumList(tail)
}
```

This is purely syntactic sugar, but it can also aid in readability by joining together the code paths of a function with the function's parameters, for example:

```nanyx
def map: (list(a), (a -> b)) -> list(b) = {
  | []             , _ -> []
  | [head, ...tail], f -> [f(head), ...map(tail, f)]
}
```

In this example, the patterns can guide us in understanding the structure of the function, as the empty list case explicitly doesn't use the function argument (the function argument is discarded with `_`), while the non-empty case does.

# "Or" patterns

Sometimes we may wish to handle multiple patterns with the same result. This can be done with "or" patterns, which use the `|` operator to combine multiple patterns into one:

```nanyx
match day
  | "Saturday" | "Sunday" -> "Weekend!"
  | _ -> "Weekday"
```

An or-pattern may span several lines. Every arm must end in `->`, so a `|` that is not preceded by `->` continues the current pattern rather than starting a new arm:

```nanyx
match day
  | "Saturday"
  | "Sunday" -> "Weekend!"
  | _ -> "Weekday"
```

# "And" patterns

"And" patterns allow for the combination of multiple patterns that must all match simultaneously. This is done with the `&` operator:

```nanyx
match getValue()
  | { < 0 } & n -> println("Negative: {n}")
  | { > 100 } & n -> println("Large: {n}")
  | n -> println("Something else: {n}")
```

> **Note:** The Nanyx compiler ensures your patterns are exhaustive — every possible value must be handled. This prevents runtime crashes from unhandled cases.

# Destructuring bindings

Patterns can be used outside of `match` expressions as well. A `def` binding can use a pattern to destructure the value on the right-hand side:

```nanyx
def (x, y) = getCoordinates()

def #Hsl(h, s, l) = getColor()
```

Bear in mind that patterns used in this way must be _total_, meaning they must match all possible values of the type being destructured. For example, if `getCoordinates()` returns a tuple of two integers, then the pattern `(x, y)` is total and will always match. However, if `#Hsl` is a _partial_ [custom pattern](../advanced/custom-patterns) over the return type of `getColor()` then the pattern `#Hsl(h, s, l)` is not total and will cause a compile-time error.

# If-matching

If the pattern is not total and you only care about a single case, you can test a value against a pattern with the `is` keyword instead of writing a full `match` expression:

```nanyx
if getResult() is #some(value) then
  println("Got a value: {value}")
```

The value goes on the left of `is` and the pattern on the right. Any variables the pattern binds (here `value`) are in scope in the `then` branch. The condition is a pattern test, not a comparison: it never reads an existing variable called `value`.

This is equivalent to a `match` expression that only handles one case and ignores everything else:

```nanyx
match getResult()
  | #some(value) -> println("Got a value: {value}")
  | _ -> ()
```

`is` also works as a branch condition in the multi-branch form of `if`:

```nanyx
if | input is #some(n) -> println("Got {n}")
   | input is #none -> println("Got nothing")
   | else -> println("Something else")
```

# Custom patterns

Custom patterns are covered in detail in [Advanced: Custom patterns](../advanced/custom-patterns).