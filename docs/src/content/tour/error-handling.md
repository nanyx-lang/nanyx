---
title: "Error handling"
description: "Working with Result and Option types"
order: 16
---

In Nanyx, errors are values: a function that can fail says so in its return type, using tag unions, so callers must handle the failure cases. There is no catchable exception mechanism. A program can still _panic_ on an unrecoverable failure (such as running out of memory), but a panic is not something code is expected to catch or recover from.

# Result types

In Nanyx, functions that can fail return a result type using tag unions:

```nanyx
-- A function that might fail
def getCustomer: CustomerId -> #some(Customer) | #error(#notFound | #databaseError)
  = { id -> ... }
```

Handle the result with pattern matching:

```nanyx
match getCustomer(someId)
  | #some(customer) -> println("Found: {customer.name}")
  | #error(#notFound) -> println("Customer not found")
  | #error(#databaseError) -> println("Database error")
```

# The `try` keyword

`proc` is the standard library's result [workflow](../advanced/workflows). Inside a `proc` block, `try` short-circuits on errors and `return` wraps the final value in `#some`:

```nanyx
def result = proc {
  def customer = try getCustomer("someid")
  def latestOrder = try getLatestOrder(customer)
  return latestOrder.total
}
-- `result` has type `#some(int) | #error(#notFound | #databaseError | #noOrders)`
```

If any step returns an error, the function immediately returns that error.

A `proc` block that is only run for what it does doesn't need a `return`. A block that ends without one is `#some()`, the `proc` workflow's `zero`:

```nanyx
def greet: [Console] string -> #some() | #invalidName = { name -> proc {
  def person = try Person.new(name)
  println("Hello, {person.name}!")
} }
```

Every way out of a block must agree, so a block that returns a value on one path can't also end without `return` on another: `if n > 2 then return n` on its own is a type error, since the block would be `#some(int)` one way and `#some()` the other.

# Option types

For values that might not exist, use the `#some` / `#none` pattern:

```nanyx
def findUser: int -> #some(User) | #none = { id ->
  match id
    | 1 -> #some(User(name = "Alice"))
    | _ -> #none
}
```

# The `except` keyword

`value except pattern -> replacement` handles some of the cases of `value` and lets the rest through. A case the pattern matches becomes the replacement, and every other case passes through unchanged. It's a `match` that doesn't have to handle every case.

```nanyx
def parseNumber = { s ->
  int.fromString(s) except #invalidInt -> #invalidVersionNumber
}
```

`int.fromString(s)` is `#some(int) | #invalidInt`. The handled tag leaves the union and the replacement's type joins it, so `parseNumber` returns `#some(int) | #invalidVersionNumber`.

To handle several cases, write the arms on the lines after `except`, as a `match` has them. An arm may bind the parts of what it matches, and may list alternatives:

```nanyx
def load: string -> #some(Config) | #error(#notFound | #unreadable(string) | #denied) = ...

def config = load(path) except
  | #error(#notFound) -> #some(Config.default)
  | #error(#unreadable(reason)) -> #some(Config.default with note = reason)
-- `config` is `#some(Config) | #error(#denied)`
```

A pattern can reach inside a tag. Handling `#error(#notFound)` takes `#notFound` out of the error's payload and leaves the other errors. When every error is handled, `#error` leaves the union altogether.

Because the narrowed type often has a single case left, `except` combines naturally with a destructuring binding, which requires its pattern to be total:

```nanyx
def g = {
  def f: () -> #some(int) | #error(#notFound) = ...

  -- After `except`, the only remaining case is `#some(int)`, so the
  -- pattern `#some(result)` is total and `result` has type `int`
  def #some(result) = f() except #error(#notFound) -> #some(0)

  result
}
```

`except` binds more loosely than every operator and pipe, so it applies to the whole expression before it:

```nanyx
def #some(shift) = scores \indexOf(scores \max) except #notFound -> #some(0)
```

A pattern that the value can never match is a type error, as it is in a `match`. An `except` whose arms handle every case is a `match`, so write it as one.

# Chaining with pipelines

The helpers in the prelude's `Option` and `Result` modules take the value first, so a chain of steps reads as a pipeline:

```nanyx
def doubled: string -> int = { input ->
  input
    \int.fromString
    \mapOption { * 2 }
    \defaultValueOption(0)
}
```

`int.fromString` gives `#some(int) | #invalidInt`, so the `Option` helpers apply: they work on any union with a `#some` case. The `Result` helpers are for `#some(a) | #error(e)`. See [option](../stdlib/option) and [result](../stdlib/result).

> **Philosophy:** By making errors explicit in the type system, Nanyx ensures you always handle failure cases. No more unexpected crashes!
