---
title: "Proposal: Query workflows"
description: "LINQ-style queries as a workflow: what works today, and what sorting, grouping and remote queries would need"
order: 36
---

This page considers queries in the style of C#'s LINQ query syntax and F#'s query expressions, written as a
[workflow](../advanced/workflows): a `query { ... }` block that walks collections with `from!`, filters with `where!`
and gives back what its `return` says. Most of it works today, with keywords the language already has. What doesn't
is the set of operators that act on every row at once, such as sorting and grouping, and running a query anywhere but
in memory. Nothing here is decided.

# What works today

A builder whose context has a `from` member that is `list.flatMap` with the rest of the block, and a `where` member that
runs the rest of the block or gives back no rows, is enough for a query that joins, filters and projects:

```nanyx
type Customer = (name: string, age: int)

context Query = (
  from: [c] (list(a), ([c] a -> list(b))) -> list(b)
  where: [c] (bool, ([c] () -> list(b))) -> list(b)
  return: a -> list(a)
  zero: list(a)
)

def query: [c] ([Query, c] () -> list(a)) -> list(a) = { block ->
  use Query(
    from = { xs, rest -> xs \list.flatMap(rest) }
    where = { ok, rest -> if ok then rest() else [] }
    return = { x -> [x] }
    zero = []
  ) in block()
}

def customers = [
  Customer(name = "Ada", age = 36)
  Customer(name = "Tim", age = 12)
  Customer(name = "Grace", age = 45)
]

@entry
def main = {
  def lengths = query {
    def customer = from! customers
    where! customer.age > 18
    def name = customer.name

    return name.length
  }

  println(lengths) -- [3, 5]
}
```

Both keywords are ordinary `#bind` keywords:

- `def customer = from! customers` is `from(customers, { customer -> rest })`: the rest of the block runs once for each
  customer, and the rows each run gives are put together.
- `where! customer.age > 18` is `where(customer.age > 18, { rest })`: the rest runs only when the test holds.

A second `from!` in the same block is a join: the rest runs for every pair. This is the list monad, which is what
LINQ's `SelectMany` and `Where` are underneath, and it is close to what `seq { for ... yield }` and
[`list.select`](../stdlib/prelude-lists) already do with `yield`.

# Open questions

## Operators over the whole query

`orderBy!`, `groupBy!`, `take!`, `distinct!` and aggregates such as `count!` act on every row the query has made so far,
not on one. No keyword kind can do that today: each keyword after a `from!` is rewritten inside that `from!`'s
continuation, so when `orderBy! customer.age` runs, it sees one customer. `#bindF`, which passes the expression as a
function, doesn't help either: the function still closes over one row.

F#'s query expressions solve this with custom operations that maintain the "variable space". The compiler rewrites the
query so far into a collection of tuples of the names bound so far. An operation such as `sortBy` takes that collection
and a function from the tuple to the key, and the rest of the query is rewritten to take the names back apart. In Nanyx
that would be a new keyword kind, roughly:

```nanyx fragment
-- def customer = from! customers
-- orderBy! customer.age
-- return customer.name
orderBy(
  from(customers, { customer -> yieldRow(customer) }),  -- the rows so far, one value per row
  { customer -> customer.age },                        -- the key, over the names bound so far
  { customer -> return(customer.name) }                -- the rest, run once for each row in the new order
)
```

To decide:

- **The kind and its name.** It differs from `#bind`, `#bindF`, `#delay` and `#yield` in that the rewriting reaches back
  to the start of the query, not just forward to the end of the block.
- **How the names bound so far travel.** A tuple of them, as F# does, or a record named after them, which would let a
  `groupBy!` give back groups whose rows keep their names.
- **What the rows so far are.** The builder's own type, or always a list or sequence that the builder then turns back
  into its type.
- **Where it may appear.** F# allows custom operations only at the top level of the query, not inside an `if`.

## What `from!` walks

The builder above walks lists. A query over a sequence should stay lazy, and one over a set or a map should walk it as
`for` does. `from!` could take anything `Iterable`, as a `for` loop does, and give back a sequence that the caller turns
into what it wants. That would make the result type the same whatever the source, at the cost of a `\seq.toList` at
the end of most queries.

## Running a query somewhere else

A query against a database needs its filter as data it can translate, such as SQL's `WHERE age > 18`. A builder never
sees that: `where!` gets a `bool`, and even `#bindF` would give it a closure, which it can only call. C# solves this with
expression trees and F# with quotations: code the compiler hands over as a syntax tree. Nanyx has no such feature
yet; [`Typed(a)`](../tour/functions#type-arguments) describes types, not code. Quotations would be a large feature of
their own, and are only worth it if remote queries are wanted. In-memory queries don't need them.

## Should the standard library have a `query` builder

If the operators above are built, a `query` builder in the standard library would give every collection the same query
syntax. It would overlap with `seq { for ... yield }` and `list.select`, which already filter and project, so it should
earn its place by what they can't do: sorting, grouping and joining in one readable block.

## Naming the keyword kinds

This came up while asking what to call `#bindF`. The four kinds differ along two axes: whether the keyword gets the
expression's value or the expression as a function, and whether it also gets the rest of the block.

| | gets the rest of the block | doesn't |
|---|---|---|
| **gets the value** | `#bind`: `kw(e, { x -> rest })` | an ordinary call, `kw(e)` |
| **gets the expression as a function** | `#bindF`: `kw({ e }, { rest })` | `#delay`: `kw({ e })` |

So `#bindF` is `#delay` with the rest of the block, as `#bind` is a call with the rest of the block. `#bindDelayed`
would say that. A name for what such a keyword does with the rest, such as `#around`, would fit `defer` and the
resource handling it is used for. A query's `orderBy!` would be a fifth kind, outside this table.
