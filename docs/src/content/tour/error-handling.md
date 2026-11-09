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

The `except` keyword is a `match` that doesn't require exhaustive handling: any case you don't handle passes through unchanged. The result type is therefore the input union _minus_ the handled tags. Each handled tag has an arm, and the arm's value must fit that narrowed type.

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

Inside a `proc` block the arm can exit early instead of supplying a replacement value:

```nanyx
def g = proc {
  def #some(result) = f() except #error(#notFound) -> return 0
  return result
}
```

# Chaining with pipelines

```nanyx
input
  \parseInt
  \Result.map { * 2 }
  \Result.defaultValue(0)
```

> **Philosophy:** By making errors explicit in the type system, Nanyx ensures you always handle failure cases. No more unexpected crashes!
