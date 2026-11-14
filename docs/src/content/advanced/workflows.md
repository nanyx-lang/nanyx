---
title: "Workflows"
description: "Builder-based workflows and custom keywords"
order: 1
---

Workflows are one of Nanyx's most distinctive features. They let libraries provide language-like constructs by rewriting a block into calls to a context's members. This makes patterns like async/await, error chaining, and DSLs feel native.

# The builder shape

A workflow builder is a function that runs a block under a [context](./contexts). The context's members are the builder's _keywords_, such as `yield`, `await`, `try` or `return`. Writing a block after the builder's name:

```nanyx fragment
someBuilder {
  -- block content
}
```

is an ordinary call of the builder with the block as its argument, a lambda without parameters:

```nanyx fragment
someBuilder({
  -- block content
})
```

The builder's type says the block requires the context, so the block can call the context's members, and the builder loads an instance of the context with `use` and calls the block under it. Inside the block, every keyword is a call to the member of that name with two arguments: the value the keyword was applied to, and a _continuation_ holding the rest of the block. This is the one desugaring rule for all workflows; everything else on this page is a consequence of it.

The simplest possible workflow can be illustrated with a builder whose context has just a `yield` member that prints the value it is given:

```nanyx fragment
context Debug = (yield: [c] (string, ([c] () -> ())) -> ())

def debug: [c & Console] ([Debug & c] () -> ()) -> () = { block ->
  use Debug(
    yield = { value, continue ->
      println(value)
      continue()
    }
  ) in block()
}

debug {
  yield! "Debug message 1"
  yield! "Debug message 2"
}
```

The block content is rewritten to nested calls to the `yield` member, each receiving the rest of the block as its continuation:

```nanyx fragment
debug({
  yield("Debug message 1", {
    yield("Debug message 2", {
      -- end of block
    })
  })
})
```

Because `yield` here calls its continuation immediately, the block runs top to bottom like ordinary sequential code. A keyword that chooses _not_ to call its continuation stops the rest of the block from running, which is how short-circuiting keywords such as `try` work (see below).

Three things about the types are worth knowing when writing a builder:

- **The builder writes its type.** The block's type, `[Debug & c] () -> ()`, is what gives the block the context's members. A builder whose type is inferred can't say that.
- **A continuation takes a context variable.** The rest of the block calls the context's members, so the continuation requires the context. A member that takes a continuation writes its type as `[c] ... -> ...` with a context variable, as `yield`'s `[c] () -> ()` does, and so does the block's type, so that the block may also call what requires other contexts, such as `println`.
- **Members are generic at each call.** A member's own type variables are instantiated afresh at each call, so one block can bind values of different types: `try` in `proc` binds an `int` in one statement and a `string` in the next.

Applying a keyword to a value without parentheses, as in `yield! 1` or `try f()`, is sugar reserved for workflow keywords; ordinary functions are always called with parentheses. `try` and `return` are written without the `!`.

A builder can take parameters before its block, and can require contexts of its own. `retrying(3) { ... }` is `retrying(3, { ... })`, and a builder whose type is `[Scheduler & c] ([Async & c] () -> a) -> a` needs a `Scheduler` where it is called.

# Yield and collection builders

A `seq` builder could provide `yield`, letting you build sequences with a clean syntax:

```nanyx fragment
def numbers = seq {
  yield! 1
  yield! 2
  yield! 3
}
```

Under the single desugaring rule this becomes:

```nanyx fragment
seq({
  yield(1, {
    yield(2, {
      yield(3, { })
    })
  })
})
```

# Async and custom keywords

Workflows can introduce custom keywords. For example, an async workflow can expose `await` by giving its context an `await` member.

```nanyx fragment
def greetingAsync = async {
  def name = await! getName()
  return "Hello, {name}"
}
```

That expands into calls to the members of the context `async` runs its block under, with the binding `def name = await! ...` becoming the parameter of the continuation:

```nanyx fragment
async({
  await(getName(), { name ->
    return("Hello, {name}")
  })
})
```

A keyword is any member of the builder's context. The `@customKeywordType` attribute on the member's declaration says how the keyword's arguments are passed (see [Keyword kinds](#keyword-kinds)). There is no naming convention for keywords; `await`, `try` and `yield` are ordinary member names.

```nanyx fragment
context Retry = (
  @customKeywordType(#bind)
  retry: [c] (Attempt(a), ([c] a -> Attempt(b))) -> Attempt(b)
)

def retrying: [c] int, ([Retry & c] () -> Attempt(a)) -> Attempt(a) = { times, block ->
  use Retry(retry = { attempt, continue -> ... }) in block()
}
```

## Common keywords

| Keyword | Description |
|---------|-------------|
| `yield`  | Produce a value in a collection builder |
| `await`  | Await an asynchronous operation in an async workflow |
| `try`    | Short-circuit on errors in a workflow that handles results |
| `return` | Wrap the block's final value and finish the block |
| `zero`   | The value of a block that produces no value (an `if` without `else`, or falling off the end without `return`) |
| `for`    | Run a `for` loop whose body uses a keyword (see [Keywords in loops](#keywords-in-loops)) |
| `defer`  | Run a thunk after the rest of the block has finished |

## Keyword kinds

| Kind | Description |
|---------|-------------|
| `#bind`  | The keyword takes a value and a continuation function. The workflow will call the continuation with the result of the keyword operation. |
| `#bindF` | Like bind, but a function returning the target value is passed instead of the value itself. This allows for workflows such as lazy evaluation and querying |
| | _More coming soon..._ |

# Workflow keywords

Builders expose keywords as the members of their context, and mark them with `@customKeywordType` where they are declared:

```nanyx fragment
context Async = (
  @customKeywordType(#bind)
  await: [c] (Task(a), ([c] a -> Task(b))) -> Task(b)

  @customKeywordType(#bindF)
  defer: [c] (([c] () -> ()), ([c] () -> Task(a))) -> Task(a)

  zero: Task(())
)
```

```nanyx fragment
def main = {
  async {
    defer! println("This runs at the end of the block")
    println("This runs first")
  }
}
```

The block above never calls `return`, so its value is the context's `zero`. A builder whose context has no `zero` can only be used for blocks that always end in `return`: one that can end without it is a compile error, which says to add a `return` or give the context a `zero`. The standard library's `proc` has one, `#some()`. A keyword the context has no member for is a compile error at the keyword.

A block that uses no keyword is still the call of its builder: `proc { #some(1) }` is `proc({ #some(1) })`, and its value is what the block's lambda returns.
# Error and option chaining

A workflow can provide a `try` keyword to short-circuit on errors. This is useful for `Result` or option-like chaining.

```nanyx
def getCustomer: int -> Result(Customer, #customerNotFound) = ...
def getLastOrder: Customer -> Result(Order, #orderNotFound) = ...

def lastOrderTotal: () -> Result(Money, #customerNotFound | #orderNotFound) = proc {
  def customer = try getCustomer(5)
  def order = try getLastOrder(customer)
  return order.total
}
```

`proc` is the standard library's result workflow: the function `proc` runs the block under the context `Proc`. Its `try` calls the continuation with the payload of `#some`, and returns any `#error` value directly without calling the continuation, which is what makes the rest of the block skip. See [Option chaining](../cookbook/option-chaining) for a worked example of writing such a builder.

# Early return and defer

A workflow can also provide a `return` keyword that exits the block early, and a `defer` keyword that runs a thunk at the end of the block.

```nanyx
def main = {
  async {
    defer! println("This runs at the end of the block")

    if b then return "Early exit"

    println("This runs first")
    return "Done"
  }
}
```

# Keywords in branches, loops and lambdas

A keyword means the same wherever it is in the block. A `try` or `return` in a branch of an `if`, or in an arm of a `match`, has the rest of the block as its continuation, just as one outside the branch does. A failed `try` and a `return` end the whole block, not just the branch:

```nanyx
def check: int -> #some(int) | #negative | #odd = { n -> proc {
  if n < 0 then try #negative
  if n == 0 then return 0
  def half = try halve(n)
  return half
} }
```

Each branch goes on to the rest of the block when it finishes, so a `def` of an `if` or `match` binds the value its branch ends with. The `if` or `match` need not be a statement of its own: one in a part of a statement that always runs, such as the value a destructuring `def` takes apart or an operand of `+`, is worked out first, just before its statement:

```nanyx fragment
def (a, b) = if n < 0 then return 0 else (n, n * 2)
```

The values the branches end with are joined as any `if`'s or `match`'s are, so they need not be the same type. An `except` that returns from one case passes the others on, and what it gives the rest of the block is what is left:

```nanyx fragment
def #some(result) = find(id) except #error(#notFound) -> return 0
```

## Keywords in loops

A `try` or `return` in the body of a `for` loop means the same as one outside it. A failed `try` ends the whole block with that failure, and a `return` ends it with its value: the items after it, and what follows the loop, don't run. Neither skips to the next item. To skip one, say so in the body with a `match`, or with a pattern on the loop, as in `for results | #some(x) -> ...`.

```nanyx
def parse: string -> #some(int) | #error(string) = { s ->
  if s == "" then #error("empty") else #some(s.length)
}

def total: list(string) -> #some(int) | #error(string) = { items -> proc {
  for items | item ->
    def n = try parse(item)
    if n > 100 then return n
  return 0
} }
```

The `for` member of the builder's context runs such a loop. It takes the list, the body and the rest of the block. The body takes an item and the rest of the loop, which it calls to go on to the next item once it has finished with this one. A `for` loop statement like the one above becomes, roughly:

```nanyx fragment
for(items, { item, next -> <the body, then next()> }, { <the rest of the block> })
```

So the builder decides how the iterations combine. `proc`'s `for` goes through the items in order and stops at the first failure or `return`, since a body that ends that way never calls `next`. Another builder could collect every failure, or run the iterations at the same time. A loop whose body uses no keyword runs as a loop, and needs no `for`. One that does, in a builder whose context has no `for`, is a compile error, which says to handle the value with a `match` in the body or give the context a `for`.

For now the loop must be a statement of its own in the block, and walk one list.

## Where a keyword can't be

A keyword whose continuation cannot be the rest of the block is an error:

- **Inside a lambda.** A lambda runs when it is called, not where it is written. This includes a lambda passed to a function, as in `list.map(xs, { x -> try f(x) })`. A builder block of its own inside the lambda, such as `{ x -> proc { ... } }`, is fine.
- **In the body of a `for` loop that isn't a statement of its own, or that walks several collections.** Make the loop a statement of its own over one list, or handle the value with a `match` in the body.
- **In a `while` loop.** Its condition and body run again each time round. Handle the value with a `match` in the loop instead.
- **In a part of an expression that may not run.** That means the right of `and` or `or`, as in `ok or (if c then try f() else false)`, or a branch of an `if` or an arm of a `match` that is itself in such a part. Make the `if` or `match` a statement of its own.

`try` and `return` are keywords of the language, so outside any builder block they are an error too, which says that each is a workflow keyword to use inside a builder block such as `proc { ... }`.

# Query and DSL workflows

Workflows are a natural fit for DSLs. A query builder can turn a block into a pipeline of filtering, joining, and projection steps.

```nanyx
def result = query {
  def customer = from customers
  def order = from orders
  join order.customerId == customer.customerId
  where order.total > 100
  select customer.name, order.total
}
```

Workflows are also how HTML-style builders or configuration DSLs are implemented.

# When to use workflows

- You want syntax that reads like a language feature, but is implemented in libraries.
- You need structured control flow around effects (async, retries, error handling).
- You want to provide a DSL for a narrow domain.
