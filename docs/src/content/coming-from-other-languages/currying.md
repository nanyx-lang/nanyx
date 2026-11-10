---
title: Currying
description: Why Nanyx does not curry functions by default
order: 2
---

Nanyx has first-class functions, so you can always curry manually. If you want a curried function, you _can_ write it explicitly:

```nanyx
def add3 = { x -> { y -> { z -> x + y + z } } }

```

When people say Nanyx is not a curried language, they mean functions are not curried by default. This section uses "currying" as shorthand for "functions that are curried by default."

Currying can make some calls more concise, but it comes with significant downsides:

- It lowers error message quality, because calling a function with too few arguments is always valid.
- It makes the pipe operator (`\`) more error-prone in some cases.
- It forces extra parentheses in some higher-order function calls.
- It increases the learning curve for people coming from mainstream languages.
- It encourages pointfree composition, which Nanyx treats as less readable in most cases.
- It can hurt runtime performance without extra compiler work.

Those costs outweigh the conciseness upside for Nanyx. The sections below explain why.

# Currying and the pipe operator

In Nanyx, these evaluate to the same result:

```nanyx
String.concat("Hello, ", "World!")

"Hello, "
  \String.concat("World!")
```

That is what most people expect after learning `\`. In curried languages, the second line usually produces a different result, because the piped value is used as the last argument rather than the first. This makes the operator feel less predictable, even for experienced users.

The difference shows up in arithmetic too:

```nanyx
someNumber \Num.div(2)

someNumber \Num.sub(1)
```

In Nanyx, those mean divide by 2 and subtract 1. In curried languages, they typically mean divide 2 by `someNumber` and subtract `someNumber` from 1, which surprises beginners and is less useful in practice.

Nanyx also benefits from `\` with higher-order functions. Consider:

```nanyx
def answer = numbers \List.map { num ->
    someFunction(
        "some argument"
        num
        anotherArg
    )
}

numbers \List.map(Num.abs)
```

Because `\` passes the value as the first argument, both examples work without extra parentheses. In a curried language, `\List.map Num.abs` usually only works if `List.map` takes the function first and the list second, which makes the first example more awkward and more parenthesized.

Currying and `\` are in tension: the style that makes `\` feel natural works against currying's main advantage.

# Currying and learning curve

Currying makes function types look unfamiliar to people coming from mainstream languages. For example, a binary `and` function would look like `Bool -> Bool -> Bool` instead of `Bool, Bool -> Bool`. Understanding why requires an explanation of partial application and why curried types are written that way.

None of that is impossible to learn, but it is extra conceptual weight that does not pay for itself in Nanyx. The language is easier to pick up when function signatures look like they do in most other languages.

# Pointfree composition

Currying makes pointfree composition effortless, but pointfree code is often harder to read. Compare:

```nanyx
def reverseSort = compose(List.reverse, List.sort)

```

with:

```nanyx
def reverseSort = { list -> List.reverse(List.sort(list)) }

```

The second version is slightly longer, but it is clearer about what happens to `list`. In more complex examples, pointfree code makes readers do extra mental translation, which increases the chance of misunderstanding.

# Summary

Currying can make some expressions shorter, but it tends to make error messages worse, pipelines more surprising, higher-order calls more parenthesized, and the learning curve steeper. Nanyx keeps currying optional so you can write it when it helps, without making it the default everywhere.
