---
title: "Proposal: Positional spread types"
description: "Tuple types that prepend to another, so that a builder can collect results of different types"
order: 37
---

> **Status: proposal, for consideration.** Nothing on this page is implemented or decided. It records a design that
> came out of a discussion about running work concurrently, why the simpler designs were set aside, and the questions
> that remain open (see [Open questions](#open-questions)). Code in plain blocks is proposed syntax, which the compiler
> does not read.

# The motivating example

Today, two requests run at once by starting a task for each and awaiting both:

```nanyx fragment
def t1 = start! fetchTodo("1")
def t2 = start! fetchTodo("2")

def r1, r2 = await(t1), await(t2)
```

This works, and is the way to write it until something on this page is built. What would read better is a block that
says which work runs at once, and gives back all its results:

```
def r1, r2 = concurrent {
  start! fetchTodo("1")
  start! fetchTodo("2")
}
```

Here `concurrent` is a workflow builder, and `start!` is its keyword. Each `start!` starts a task and gives `()`, so a
`def` that binds it binds nothing of use. The block's value is the tuple of the tasks' results, in the order they were
started, so `def r1, r2 = ...` destructures it. The block runs in a task scope of its own (`Async.concurrent`), so
leaving it waits for every task, and a crash in one ends it, as any scope does.

# Designs set aside

**A rule in the compiler for `concurrent`.** The compiler could rewrite a `concurrent` block itself, collecting each
`start!` at the top level of the block into a tuple literal. It is the smallest change, but it is a special case that
knows a name, which is against how Nanyx is built: `concurrent` should be an ordinary builder that a library could
have written.

**Nested pairs, flattened.** A builder can be written today that gives `(r1, (r2, ()))`: each `start!` pairs its
result with what the rest of the block gives. The idea was to let the compiler treat that as `(r1, r2)` everywhere.
But a tuple is a record whose fields are positional: `(a, b)` is the record with fields `0` and `1`, in its type and
in memory. Holding tuples as nested pairs, even hidden ones, would break that, and so would treating `(1, (2, 3))` as
`(1, 2, 3)`.

# The proposal

Two general features, neither of which knows about `concurrent`.

## 1. Positional spread in tuple types

A tuple type may spread another at its end:

```
(a, ...t)
```

This is the tuple whose field `0` is `a` and whose field `k + 1` is `t`'s field `k`. `t` must be a tuple, a record whose
fields are all positional, and `(a, ...())` is `a`'s one-field tuple. A value of the type is a flat record, as every
tuple is, so the unification of tuples and records is untouched.

It is typed as record spread is: a spread record's fields must be known where it is spread, and a positional spread is
a constraint the type checker solves once `t`'s shape is known at each use. Nothing is a spread type at run time: by
the time a value is built, its shape is known.

## 2. Specialising what spreads a type variable

A function whose type spreads a type variable, as `(a, ...t)` does, can't be compiled once for every `t`: the layout
of the record it builds depends on `t`'s shape. So the backend always compiles such a function at each concrete shape
it is used at, and never at boxed types. This is a rule about a kind of type, not about any one function. It is the
same specialisation builder members lack, which made a `for` loop keep a frame per item until its body gave back a tag
instead of calling the rest of the loop ([#2683](https://github.com/nanyx-lang/nanyx/issues/2683)), so the work could
serve both.

## `concurrent` as a prelude builder

With both in place, `concurrent` is a builder in the standard library, and its keyword member reads roughly:

```
start: [Async, c] (() -> a), ([c] () -> t) -> (a, ...t)
  = { work, rest ->
      def task = start! work()
      def others = rest()
      (await(task), ...others)
    }
```

It starts its task, runs the rest of the block, which starts the others, and only then awaits, so the tasks overlap
without anything else arranging it. The block's end gives `()`, the empty tuple, so the last `start!` gives its own
result's one-field tuple, and two give `(a, b)`.

Conditional work is checked by the types, without a rule of its own:

- `start!` in only one branch of an `if` makes the branches give tuples of different shapes, a type error;
- `start!` in both branches, at the same type, is fine;
- `start!` in a loop is an error, unless `concurrent` gives its context a `for`, which could give a list of results,
  one per item.

# Other uses

Positional spread is not only for `concurrent`. It also types:

- `zip` over a fixed number of lists, giving a list of tuples of their items' types;
- a function that adds a field to the front of any tuple, such as a timestamp on a log entry;
- builders that collect differently typed results in general: validation that gathers each field's checked value, or
  a parser built from a sequence of steps.

# Costs

- **The type checker** gains a new kind of constraint: a positional spread solved once the tail's shape is known, with
  error messages that say what shape was expected. The checker, the printer and the language server must all show a
  solved spread as a plain tuple.
- **The backend** must guarantee specialisation for every function whose type spreads a type variable. Today, builder
  members are compiled at boxed types, and convert what they are given and give back
  ([#2683](https://github.com/nanyx-lang/nanyx/issues/2683)).
- **Building** `(a, ...t)` copies `t`'s fields into a new record, so a chain of `n` spreads copies about `n²/2` fields.
  That is nothing for a handful of tasks, but it is not free for a long chain.

# Open questions

1. **Is the spread written by users,** or only by the standard library through an intrinsic? Writing it needs syntax
   in type position, `(a, ...t)`, and makes it part of the language.
2. **The one-field tuple.** Does Nanyx have a one-field tuple distinct from its item? If `(a, ...())` is simply `a`,
   then a single `start!` gives its plain result, which reads well but makes the rule uneven.
3. **Named and positional fields together.** Should a spread be allowed on a record with named fields, prepending a
   positional one, or only on tuples?
4. **A builder keyword named like a built-in form.** `start!` is a form of the compiler's own today. Inside
   `concurrent`'s block, the builder's `start` would have to take precedence. Is that the general rule for a builder's
   keyword that shares a built-in form's name, or should `concurrent`'s keyword have a name of its own?
5. **The name.** `Async.concurrent` already opens a scope. Does the builder replace it, or get another name?
