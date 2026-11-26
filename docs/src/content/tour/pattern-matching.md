---
title: Pattern matching
description: Powerful control flow with match expressions
order: 6
---

While most languages have some kind of multi-way branching construct like `switch` statements, not all have pattern matching like Nanyx's `match`, which is far more powerful. Pattern matching allows you to extract nested values, check conditions, and handle different data shapes all in a single expression, making code more concise and safer by ensuring all possible cases are handled.

# Overview

The simplest form of pattern matching is matching against literal values, which is directly equivalent to a C-style `switch` statement:

```nanyx fragment
match someValue
  | 0 -> "zero"
  | 1 -> "one"
  | _ -> "something else"
```

Note the use of `_` as a wildcard pattern that matches anything not previously matched. This may be required to ensure exhaustiveness (depending on the type being matched), and all match expressions must be exhaustive in Nanyx.

# Pattern order

Unlike a C-style `switch`, Nanyx's `match` patterns are evaluated in the order they are written. This allows for more flexible patterns and the ability to handle overlapping cases:

```nanyx fragment
match value
  | 0 -> "zero"
  | 1 -> "one"
  | _ -> "something else"
```

A match that does not cover every possible value is a compiler error. An arm that can never be reached, because an earlier arm already matches everything it would, is not an error (the program is still correct), so it is reported by the [linter](../advanced/linters) as a warning instead.

# Introducing values

Patterns can also introduce new variables that are bound to parts of the matched value:

```nanyx fragment
match someFunction()
  | 0 -> "zero"
  | 1 -> "one"
  | n -> "something else: {n}"
```

# Nested matches

An arm's body may be another `match`. Its arms end at the first arm indented less than its own, so an arm lined up with the outer match's arms belongs to the outer match:

```nanyx fragment
match xs
  | [a] ->
      match a
        | 0 -> "zero"
        | _ -> "one"
  | _ -> "other"
```

A nested match, or a `for` loop's arms, may also start on the outer arm's own line, as in `| #some(lines) -> for lines | line -> println(line)`. Its later arms must then start lines indented further than that one, so the outer match's next arm still ends it.

A match written on one line takes every arm after it on that line, so a nested match in the middle of a one-line match needs brackets: `| [a] -> (match a | 0 -> "zero" | _ -> "one") | _ -> "other"`.

# Destructuring

Destructuring is a syntax that breaks a data structure into its components and binds them to variables. This is especially useful for working with tuples, lists, and custom data types.

## Lists

```nanyx fragment
-- Match an empty list
match myList
  | [] -> "empty!"
  | [head, ...tail] -> "head is {head}"
```

In this example, the pattern `[head, ...tail]` matches a non-empty list, binding the first element to `head` and the rest of the list to `tail`. The `...` syntax (called the spread operator) is used to indicate that `tail` should capture all remaining elements of the list.

## Tuples

```nanyx
-- Destructure a tuple
def (x, y) = 10, 20
```

The brackets around the outermost tuple can be left off, so `def x, y = (10, 20)` is the same binding. Nested tuples still need theirs:

```nanyx fragment
def low, high = sort(3, -1)

def name, (street, city) = person
```

Destructuring is available in pattern matching, allowing for simultaneous testing of data and extracting values at the same time:

```nanyx fragment
match p
  | (0, y) -> "x is zero, y is {y}"
  | (x, 0) -> "y is zero, x is {x}"
  | (x, y) -> "x is {x}, y is {y}"
```

Note how the patterns `(0, y)` and `(x, 0)` use a combination of literal matching and variable binding to match specific shapes of the tuple. 

A `match` can also take several values at once, separated by commas, with an arm giving a pattern for each. Matching several values is the same as matching a tuple of them, so the brackets are optional on either side: a tuple can be matched with `| x, y`, and several values with `| (x, y)`.

```nanyx fragment
match a, b
  | 0, y -> "a is zero, b is {y}"
  | x, y -> "a is {x}, b is {y}"
```

The final pattern `(x, y)` matches any tuple and binds both elements to variables.

Since the values are a tuple, an arm can also give one pattern for all of them: `| _ ->` matches whatever they are, and `| t ->` binds the tuple.

```nanyx fragment
match a, b
  | 0, 0 -> "both zero"
  | t -> "something else: {t}"
```

## Records

Records can also be destructured in patterns, allowing you to match on specific fields while ignoring others:

```nanyx fragment
match r
  | (name = "Alice", ...) -> "Found Alice"
  | (name, age) -> "{name} is {age} years old"
```

Here, the pattern `(name = "Alice", ...)` matches any record where the `name` field is "Alice", while ignoring all other fields. The second pattern matches any record with `name` and `age` fields, binding them to variables. Writing a named field as just `name` is shorthand for `name = name`, binding the field to a local of the same name. The two may be mixed in one pattern: `(name, age = years)` binds `name` and gives `age` the name `years`.

As with tuples, records patterns must be of the correct shape to match the type being matched, and all fields must be accounted for either by matching them directly or using the spread operator to ignore them.

A pattern whose fields have no `=` can end in `...` too. Each field is read as it would be without the `...`: a name alone takes the field of that name, where the value is already known to have one, and any other pattern takes the field at its position, counting every field written before it. The `...` then leaves the other fields out:

```nanyx
def greet: (name: string, age: int, email: string) -> string = { | (name, ...) -> "Hello, {name}" }

def point = 3, 4, 5

match point
  | (x, ...) -> println("x is {x}")

println(greet(name = "Alice", age = 30, email = "alice@example.com"))
```

Here `(name, ...)` is `(name = name, ...)`, and `(x, ...)` takes the tuple's first element. A name the record has no field of is read by position, so `(n, ...)` on `(name: string, age: int)` is an error: that record has no first positional field.

## Tags

Tags can also be destructured in patterns, using the same syntax that is used to construct them. For example, if you have a sum type representing shapes:

```nanyx
type Shape = #point | #circle(float) | #rectangle(float, float)

def area: Shape -> float = { shape ->
  match shape
    | #point -> 0.0
    | #circle(r) -> 3.14159 * r * r
    | #rectangle(w, h) -> w * h
}
```

A payload that is a record with named fields is taken apart by name, as any record is: with `#rectangle(width: float, height: float)`, the pattern `#rectangle(width, height)` binds each field to the local of the same name. This also works for a lambda's parameter, as in `{ #rectangle(width, height) -> width * height }`. As with a record, `...` leaves the other fields out: `#rectangle(width, ...)` binds `width` alone.

An arm for a tag the value's type doesn't have can never match, so it is an error (NYX0201), which suggests the tag it most likely means: a misspelt `#nothing` for `#none`. A value whose type is a single tag, as `def simple = #nil` is, has only that tag, so an arm for any other is the same error. Only a union with a rest, such as `#some(a) | r`, can have a tag it doesn't name.

A tag that carries the unit value is matched as it is written, with empty brackets, and `()` on its own matches the unit value:

```nanyx
def describe: (#some() | #rejected(string)) -> string = {
  | #some() -> "saved"
  | #rejected(reason) -> "not saved: {reason}"
}
```

## Named types

A value of a named type is taken apart by the type's name, as its constructor is written. One pattern in the brackets matches the whole of what the type is made from: `Email(s)` binds the string a `protected string` holds, and `Point(p)` the whole record a record type is. Several patterns, a trailing `...`, or fields named with `=` take a record's fields instead, read as a record pattern reads them:

```nanyx
type Email = protected string
type Point = protected (x: int, y: int)
type Pair = (int, int)

def address: Email -> string = { Email(s) -> s }
def sum = { Pair(a, b) -> a + b }

def describe: Point -> string = {
  | Point(x = 0, ...) -> "on the y axis"
  | Point(x, y) -> "at {x}, {y}"
}

def Point(p) = Point(x = 3, y = 4)

println(address(Email("ada@example.com")))
println(sum(Pair(1, 2)))
println(describe(Point(x = 0, y = 7)))
println(describe(Point(x = 1, y = 2)))
println(p.x + p.y)
```

A private type can only be taken apart in the module that defines it, as its fields can only be read there (NYX0212).

# Nesting patterns

Where pattern matching really shines is in its ability to nest patterns inside each other, allowing you to make complex data structure comparisons and extract values in a single expression. Below is an artificial complex example that demonstrates this:

```nanyx
type Status = #some(int) | #failed(string)

type Request = (id: int, status: Status, tags: list(string))

def processRequests: list(Request) -> string = { requests ->
  match requests
    | [] ->
      "No requests"
    | [(id = 1, status = #some(value), tags = ["urgent", ...rest]), ...more] ->
      "Urgent request 1 with value {value}"
    | [(id = id, status = #failed(msg), tags = []), (status = #some(val), ...), ...tail] ->
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

A guard is any lambda that takes the value being matched and returns a `bool`; the arm matches when it returns `true`. Most often it is a [shorthand lambda](./lambdas-and-higher-order-functions) that leaves out the value, but it can name it too, and it can be nested inside other patterns:

```nanyx
def describe: int, string -> string = {
  | ({ % 2 == 0 }, _) -> "an even number"
  | (_, { .length > 5 }) -> "a long name"
  | ({ x -> x > 0 and x < 10 }, _) -> "a small number"
  | _ -> "something else"
}
```

The guard sees whatever is in scope where the `match` is written, but not the names its own arm binds: those are only bound once the whole arm has matched.

Since the compiler cannot know what a guard will return, an arm with a guard never counts towards making a `match` exhaustive. A `match` whose only arms for some values are guarded still needs an arm that catches the rest, such as `_`.

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
def map: list(a), (a -> b) -> list(b) = {
  | [], _ -> []
  | [head, ...tail], f -> [f(head), ...map(tail, f)]
}
```

In this example, the patterns can guide us in understanding the structure of the function, as the empty list case explicitly doesn't use the function argument (the function argument is discarded with `_`), while the non-empty case does.

# "Or" patterns

Sometimes we may wish to handle multiple patterns with the same result. This can be done with "or" patterns, which use the `|` operator to combine multiple patterns into one:

```nanyx fragment
match day
  | "Saturday" | "Sunday" -> "Weekend!"
  | _ -> "Weekday"
```

An or-pattern may span several lines. Every arm must end in `->`, so a `|` that is not preceded by `->` continues the current pattern rather than starting a new arm:

```nanyx fragment
match day
  | "Saturday"
  | "Sunday" -> "Weekend!"
  | _ -> "Weekday"
```

# "And" patterns

"And" patterns allow for the combination of multiple patterns that must all match simultaneously. This is done with the `&` operator:

```nanyx fragment
match getValue()
  | { < 0 } & n -> println("Negative: {n}")
  | { > 100 } & n -> println("Large: {n}")
  | n -> println("Something else: {n}")
```

> **Note:** The Nanyx compiler ensures your patterns are exhaustive — every possible value must be handled. This prevents runtime crashes from unhandled cases.

# Destructuring bindings

Patterns can be used outside of `match` expressions as well. A `def` binding can use a pattern to destructure the value on the right-hand side:

```nanyx fragment
def (x, y) = getCoordinates()

def #Hsl(h, s, l) = getColor()
```

Bear in mind that patterns used in this way must be _total_, meaning they must match all possible values of the type being destructured. For example, if `getCoordinates()` returns a tuple of two integers, then the pattern `(x, y)` is total and will always match. However, if `#Hsl` is a _partial_ [custom pattern](../advanced/custom-patterns) over the return type of `getColor()` then the pattern `#Hsl(h, s, l)` is not total and will cause a compile-time error.

# If-matching

If the pattern is not total and you only care about a single case, you can test a value against a pattern with the `is` keyword instead of writing a full `match` expression:

```nanyx fragment
if getResult() is #some(value) then
  println("Got a value: {value}")
```

The value goes on the left of `is` and the pattern on the right. Any variables the pattern binds (here `value`) are in scope in the `then` branch. The condition is a pattern test, not a comparison: it never reads an existing variable called `value`.

This is equivalent to a `match` expression that only handles one case and ignores everything else:

```nanyx fragment
match getResult()
  | #some(value) -> println("Got a value: {value}")
  | _ -> ()
```

`is` also works as a branch condition in the multi-branch form of `if`:

```nanyx fragment
if | input is #some(n) -> println("Got {n}")
   | input is #none -> println("Got nothing")
   | else -> println("Something else")
```

## Tests anywhere

`x is pattern` is not only for `if`: it is a `bool` expression, so it goes wherever an expression does, such as a lambda's body, a def's value, an argument, or an operand of `and`, `or` and `not`:

```nanyx
type Item = #foo(string) | #bar(string) | #baz | #quux

def items: list(Item) = [#foo("one"), #bar("two"), #baz, #quux]

println(items \list.find { x -> x is #quux })
println(items \list.filter { x -> not (x is #foo(_)) })
```

The names a pattern binds are in scope for the rest of an `and` chain after the test, in an `if` condition also in the `then` branch, and in a `while` condition in the loop's body (see [While loops](./loops#while-loops)):

```nanyx
type MaybeInt = #some(int) | #none

def big: MaybeInt -> bool = { o ->
  o is #some(v) and v > 3
}

def add: MaybeInt, MaybeInt -> int = { a, b ->
  if a is #some(x) and b is #some(y) then x + y else 0
}

println(big(#some(5)))
println(add(#some(1), #some(2)))
```

Brackets only group, so they don't end a chain: `(o is #some(v)) and v > 3` is the same chain as `o is #some(v) and v > 3`, and `if (o is #some(v)) then v else 0` has `v` in its `then` branch. The names are not in scope anywhere else: not after an `or` (`o is #some(v) or v > 3`), not in the `else` branch, not after a `while` loop, not outside a `not`, and not outside a chain that is the operand of another operator, as in `(o is #some(v) and true) == v`. Using one there is an unknown-name error, as the test may not have matched.

`is` binds more loosely than comparisons, equality, pipes and arithmetic, and more tightly than `and` and `or` (see [the operators](../tour/operators#the-operators)). So `xs \list.head is #some(h)` tests the head of `xs`, and `a and b is #some(v)` is `a and (b is #some(v))`. Only `and` or `or` may follow the pattern, so `x is v == 1` is an error that asks for brackets. A `not` binds more tightly than `is`, as it does with `==`, so `not x is #a` is an error too, and the test is negated as `not (x is #a)`.

# Custom patterns

Custom patterns are covered in detail in [Advanced: Custom patterns](../advanced/custom-patterns).