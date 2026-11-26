---
title: "Workflows"
description: "Builder-based workflows and custom keywords"
order: 1
---

Workflows are one of Nanyx's most distinctive features. They let libraries provide language-like constructs by rewriting a block into calls to a context's members. This makes patterns like retrying, error chaining, and DSLs feel native.

# The builder shape

A workflow builder is a function that runs a block under a [context](./contexts). The context's members are the builder's _keywords_, such as `try`, `defer` or `return`. Writing a block after the builder's name:

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

The simplest possible workflow can be illustrated with a builder whose context has a `log` member that prints the value it is given, and a `zero`, the value of a block that ends without `return`:

```nanyx
context Debug = (log: [c] (string, ([c] () -> ())) -> (), zero: ())

def debug: [c, Console] ([Debug, c] () -> ()) -> () = { block ->
  use Debug(
    log = { value, continue ->
      println(value)
      continue()
    }
    zero = ()
  ) in block()
}

debug {
  log! "Debug message 1"
  log! "Debug message 2"
}
```

The block content is rewritten to nested calls to the `log` member, each receiving the rest of the block as its continuation:

```nanyx fragment
debug({
  log("Debug message 1", {
    log("Debug message 2", {
      zero
    })
  })
})
```

Because `log` here calls its continuation immediately, the block runs top to bottom like ordinary sequential code. A keyword that chooses _not_ to call its continuation stops the rest of the block from running, which is how short-circuiting keywords such as `try` work (see below).

Three things about the types are worth knowing when writing a builder:

- **The builder writes its type.** The block's type, `[Debug, c] () -> ()`, is what gives the block the context's members. A builder whose type is inferred can't say that.
- **A continuation takes a context variable.** The rest of the block calls the context's members, so the continuation requires the context. A member that takes a continuation writes its type as `[c] ... -> ...` with a context variable, as `log`'s `[c] () -> ()` does, and so does the block's type, so that the block may also call what requires other contexts, such as `println`.
- **Members are generic at each call.** A member's own type variables are instantiated afresh at each call, so one block can bind values of different types: `try` in `proc` binds an `int` in one statement and a `string` in the next.

Applying a keyword to a value without parentheses, as in `log! "hi"` or `try f()`, is sugar reserved for workflow keywords; ordinary functions are always called with parentheses. The language's own keywords, `try`, `yield` and `return`, are written without the `!`. A keyword applied to a value takes everything after it in its statement, so `try a + b` is `try (a + b)`, but one that is an argument, a field or an element ends at its `,`, or at the end of its line when they are written one per line: `P(a = try num(x), b = try num(y))` binds both.

A builder can take parameters before its block, and can require contexts of its own. `retrying(3) { ... }` is `retrying(3, { ... })`, and a builder whose type is `[Clock, c] ([Retry, c] () -> a) -> a` needs a `Clock` where it is called.

# Yield and sequences

A block with `yield` in it is a [lazy sequence](../stdlib/prelude-sequences) of the values it yields. `yield` is written without `!`, as `try` and `return` are, and `seq { ... }` makes a sequence of such a block:

```nanyx
def items = seq {
  println("Yielding 1")
  yield 1
  println("Yielding 2")
  yield 2
}

println(items \seq.take(1) \seq.toList)
```

The code after a `yield` runs only when the sequence is pulled that far, so the program above prints `Yielding 1` and `[1]`, and never `Yielding 2`.

It isn't an ordinary workflow: the rest of the block runs only when the sequence is pulled, after `seq` has returned, so there's no builder's context left for it to call. Instead `yield` is a keyword of kind `#yield`, which rewrites the block into the sequence itself, with no context left in it:

```nanyx fragment
def items = seq({
  println("Yielding 1")
  seq.cons(1, {
    println("Yielding 2")
    __seqLast(2)
  })
})
```

`yield x` with the rest of the block after it is `seq.cons(x, { rest })`, and the end of the block, or of a branch that doesn't yield, is `seq.empty()`. A `yield` the block ends with has no rest, and is `__seqLast(x)`, the sequence of `x` alone. A `for` loop whose body yields gives the sequences its body gives for each element, one after another, then goes on to the rest of the block: it's `seq.flatMap` over the loop's collection. As with a plain `for` loop, that can be anything `Iterable`: a sequence or a range, which is walked as it is, or a list, an array, a set, a map or a type of your own that attaches `toSeq`, which is read through its sequence.

```nanyx
def evens = seq {
  for 1..10 | n ->
    if n % 2 == 0 then yield n
}

println(evens \seq.toList)
```

Any lambda with a `yield` in it is a sequence, not only a block after `seq`. The builders that take every value at once, such as `list.build` and `list.select`, don't take a sequence: their lambda calls `emit`, which pushes each value as it comes, so nothing is lazy and nothing is made per value but the result ([Building a collection](./sequences-and-queries#building-a-collection)).

A sequence has no `return`: it ends where its block does. `yield` gives no value, so it's a statement of its own, never bound with `def`. And it can't be in a `while` loop, whose body runs again each time round: walk a sequence with a `for` loop instead, or make the sequence with `seq.unfold`.

# Custom keywords

Workflows can introduce custom keywords. For example, a workflow that retries what fails can expose `retry` by giving its context a `retry` member.

```nanyx fragment
def page = retrying(3) {
  def text = retry! download(url)
  return parse(text)
}
```

That expands into calls to the members of the context `retrying` runs its block under, with the binding `def text = retry! ...` becoming the parameter of the continuation:

```nanyx fragment
retrying(3, {
  retry(download(url), { text ->
    return(parse(text))
  })
})
```

A keyword is any member of the builder's context. The `@customKeywordType` attribute on the member's declaration says how the keyword's arguments are passed (see [Keyword kinds](#keyword-kinds)). There is no naming convention for keywords; `retry` and `defer` are ordinary member names.

```nanyx fragment
context Retry = (
  @customKeywordType(#bind)
  retry: [c] (Attempt(a), ([c] a -> Attempt(b))) -> Attempt(b)
)

def retrying: [c] int, ([Retry, c] () -> Attempt(a)) -> Attempt(a) = { times, block ->
  use Retry(retry = { attempt, continue -> ... }) in block()
}
```

## Common keywords

| Keyword | Description |
|---------|-------------|
| `yield`  | Produce an element of a lazy sequence (a `#yield` keyword, see [Yield and sequences](#yield-and-sequences)) |
| `try`    | Short-circuit on errors in a workflow that handles results |
| `return` | Wrap the block's final value and finish the block |
| `zero`   | The value of a block that produces no value (an `if` without `else`, or falling off the end without `return`) |
| `for`    | Run a `for` loop whose body uses a keyword (see [Keywords in loops](#keywords-in-loops)) |
| `defer`  | Run the expression after it once the rest of the block has finished (a `#bindF` keyword) |
| `start`  | Start a task that runs the expression after it (a `#delay` keyword that is a def attached to `Async`, `Async.start`, see [Async](./async#tasks)) |

## Keyword kinds

| Kind | `kw! e` becomes | For |
|---------|-------------|-----|
| `#bind`  | `kw(e, { rest of the block })` | `try`, and `retry` in a retrying workflow: the member takes a value and the continuation, which it calls with the result |
| `#bindF` | `kw({ e }, { rest of the block })` | `defer`, which runs `e` after the rest of the block |
| `#delay` | `kw({ e })` | anything that takes "this computation, not its value": `start!`, a `retry!`, a `timeout!` |
| `#yield` | `seq.cons(e, { rest of the block })` | `yield`: the lambda it is in is a lazy sequence, and no member is called |

A member without the attribute is a `#bind` keyword. A `#bindF` member's first parameter is a function of no parameters, `[c] () -> a`, and its second the continuation. A `#delay` member takes only the function: its type is of the form `(() -> a) -> r`, with the lambda's contexts as usual (`[c] () -> a` where the member is generic in them). A member whose type doesn't take a function of no parameters where its kind passes one is a compile error at the attribute ([NYX0046](../standard-tools/errors#nyx0046)).

A `#delay` keyword rewrites nothing but itself: `kw! e` is a call of the keyword's function with `{ e }`, wherever it's written. So it needs no builder, and a def [attached to a context](./attached-definitions#attached-to-a-context) can be one, marked with the attribute as a member is. A keyword belongs to a context either way: an attached def is in scope by its bare name wherever its context is, as a member is, so the keyword is too, a lambda included. `nanyx/async`'s `Async.start` is one, so `start!` is wherever `Async` is:

```nanyx fragment
@customKeywordType(#delay)
export def Async.start: [Async, TaskScope, c] ([Async, c] () -> a) -> Task(a) = { work -> ... }

def pages = { urls -> urls \map { url -> start! http.get(url) } }   -- start({ http.get(url) })
```

Like `try`, every keyword takes the whole expression after it. For a `#delay` keyword that matters: in `start! http.get(url) \orCrash("failed")` the pipe is inside the lambda, so `orCrash` runs in the task, not on the `Task` that `start!` gives back.

A keyword's kind is the one its member's or attached def's declaration gives it, in the module or in a module it imports.

A `#yield` keyword makes the lambda it is in a lazy sequence, as [above](#yield-and-sequences). The language's `yield` is one, declared by the prelude's `Yield` context, and a context of your own can declare another, used as `emit! x`. Any other kind is a compile error at the attribute ([NYX0019](../standard-tools/errors#nyx0019)), as is a kind other than `#delay` before an attached def, which needs a builder, and any kind before a def attached to no context. The attribute anywhere but before a member of a `context` declaration or a def attached to a context is an error too.

# Workflow keywords

Builders expose keywords as the members of their context, and mark them with `@customKeywordType` where they are declared:

```nanyx fragment
context Jobs = (
  @customKeywordType(#bind)
  run: [c] (Job(a), ([c] a -> Job(b))) -> Job(b)

  @customKeywordType(#bindF)
  defer: [c] (([c] () -> ()), ([c] () -> Job(a))) -> Job(a)

  zero: Job(())
)
```

```nanyx fragment
def main = {
  jobs {
    defer! println("This runs at the end of the block")   -- defer({ println(...) }, { _ -> ... })
    println("This runs first")
  }
}
```

The block above never calls `return`, so its value is the context's `zero`. A builder whose context has no `zero` can only be used for blocks that always end in `return`: one that can end without it is a compile error, which says to add a `return` or give the context a `zero`. The standard library's `proc` has one, `#some()`. A keyword the context has no member for is a compile error at the keyword.

A block that uses no keyword is still the call of its builder: `proc { #some(1) }` is `proc({ #some(1) })`, and its value is what the block's lambda returns.
# Error and option chaining

A workflow can provide a `try` keyword to short-circuit on errors. This is useful for chaining [option-like](option-like-types) steps.

```nanyx fragment
def getCustomer: int -> #some(Customer) | #customerNotFound = ...
def getLastOrder: Customer -> #some(Order) | #orderNotFound = ...

def lastOrderTotal: () -> #some(Money) | #customerNotFound | #orderNotFound = proc {
  def customer = try getCustomer(5)
  def order = try getLastOrder(customer)
  return order.total
}
```

`proc` is the standard library's result workflow: the function `proc` runs the block under the context `Proc`. Its `try` calls the continuation with the payload of `#some`, and returns any other tag, a failure, directly without calling the continuation, which is what makes the rest of the block skip. See [Option chaining](../cookbook/option-chaining) for a worked example of writing such a builder.

# Early return and defer

A workflow can also provide a `return` keyword that exits the block early, and a `defer` keyword, of kind `#bindF`, that runs the expression after it at the end of the block.

```nanyx fragment
def main = {
  jobs {
    defer! println("This runs at the end of the block")

    if b then return "Early exit"

    println("This runs first")
    return "Done"
  }
}
```

# Keywords in branches, loops and lambdas

A keyword means the same wherever it is in the block. A `try` or `return` in a branch of an `if`, or in an arm of a `match`, has the rest of the block as its continuation, just as one outside the branch does. A failed `try` and a `return` end the whole block, not just the branch:

```nanyx fragment
def check: int -> #some(int) | #negative | #odd = { n ->
  proc {
    if n < 0 then try #negative
    if n == 0 then return 0
    def half = try halve(n)
    return half
  }
}
```

Each branch goes on to the rest of the block when it finishes, so a `def` of an `if` or `match` binds the value its branch ends with. The `if` or `match` need not be a statement of its own: one in a part of a statement that always runs, such as the value a destructuring `def` takes apart or an operand of `+`, is worked out first, just before its statement:

```nanyx fragment
def (a, b) = if n < 0 then return 0 else (n, n * 2)
```

The values the branches end with are joined as any `if`'s or `match`'s are, so they need not be the same type. An `except` that returns from one case passes the others on, and what it gives the rest of the block is what is left:

```nanyx fragment
def #some(result) = find(id) except #notFound -> return 0
```

## Keywords in loops

A `try` or `return` in the body of a `for` loop means the same as one outside it. A failed `try` ends the whole block with that failure, and a `return` ends it with its value: the items after it, and what follows the loop, don't run. Neither skips to the next item. To skip one, say so in the body with a `match`, or with a pattern on the loop, as in `for results | #some(x) -> ...`.

```nanyx
def parse: string -> #some(int) | #empty = { s ->
  if s == "" then #empty else #some(s.length)
}

def total: list(string) -> #some(int) | #empty = { items ->
  proc {
    for items | item ->
      def n = try parse(item)
      if n > 100 then return n

    return 0
  }
}
```

The `for` member of the builder's context runs such a loop. It takes the list, the body and the rest of the block. A loop over a map or a set is given the list `map.toList` or `set.toList` makes of it, so a builder's `for` takes only lists. The body takes an item, and gives back `#__nextItem` once it has finished with it, where the loop goes on to the next item, or else what ends the block: a failure, where a `try` fails, or a `return`'s value. A `for` loop statement like the one above becomes, roughly:

```nanyx fragment
for(items, { item -> <the body, then #__nextItem> }, { <the rest of the block> })
```

So the builder decides how the iterations combine. `proc`'s `for` goes through the items in order, calling the body on each, and stops at the first failure or `return`:

```nanyx fragment
for: [d] (list(x), ([d] x -> #__nextItem | #some(y) | s), ([d] () -> #some(y) | s)) -> #some(y) | s
```

```nanyx fragment
def procFor
  : [c] list(a), ([c] a -> #__nextItem | #some(b) | r), ([c] () -> #some(b) | r) -> #some(b) | r
  = {
    | [], _, rest -> rest()
    | [item, ...others], body, rest ->
      match body(item)
        | #__nextItem -> procFor(others, body, rest)
        | ended -> ended
  }
```

Each body's call returns before the next item's, and the loop goes on with a tail call, so a loop runs in constant stack however many items it walks. Another builder could go on past a failure, collect every failure, or run the iterations at the same time. A loop whose body uses no keyword runs as a loop, and needs no `for`. One that does, in a builder whose context has no `for`, is a compile error, which says to handle the value with a `match` in the body or give the context a `for`.

For now the loop must be a statement of its own in the block, and walk one list, map or set.

A member that does something after its continuation returns, as `defer` does, makes each step of a loop that uses it keep a stack frame, and always will.

## Where a keyword can't be

A keyword whose continuation cannot be the rest of the block is an error:

- **Inside a lambda.** A lambda runs when it is called, not where it is written. This includes a lambda passed to a function, as in `list.map(xs, { x -> try f(x) })`. A builder block of its own inside the lambda, such as `{ x -> proc { ... } }`, is fine.
- **In the body of a `for` loop that isn't a statement of its own, or that walks several collections.** Make the loop a statement of its own over one list, map or set, or handle the value with a `match` in the body.
- **In a `while` loop.** Its condition and body run again each time round. Handle the value with a `match` in the loop instead.
- **In a part of an expression that may not run.** That means the right of `and` or `or`, as in `ok or (if c then try f() else false)`, or a branch of an `if` or an arm of a `match` that is itself in such a part. Make the `if` or `match` a statement of its own.

`try` and `return` are keywords of the language, so outside any builder block they are an error too, which says that each is a workflow keyword to use inside a builder block such as `proc { ... }`.

# Query and DSL workflows

Workflows are a natural fit for DSLs. A query builder can turn a block into a pipeline of filtering, joining, and projection steps.

```nanyx fragment
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
- You need structured control flow around effects (retries, cleanup, error handling).
- You want to provide a DSL for a narrow domain.
