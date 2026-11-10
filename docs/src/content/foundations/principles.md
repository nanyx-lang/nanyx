---
title: "Principles"
description: "The design values that shape Nanyx"
order: 1
---

Nanyx is guided by a small set of design principles. These rules are not necessarily inviolable or unchanging, but they represent the values that we strive to uphold in the design and implementation of the language and its standard library. They are intended to guide our decisions and help us make trade-offs when designing features and making changes to the language.

# Compiler diagnostics

## Gradual feedback, not blocked flow

Surface type information and diagnostics early, but keep iteration fast. The compiler should inform you without getting in the way of exploration. Unlike most languages, Nanyx does not block the completion of a debug build on most errors, even name or type errors†. Instead, it surfaces diagnostics whilst still allowing you to run your code, making it easier to iterate and learn.

† Syntax errors will still abort compilation, as trying to proceed in the face of syntax errors will likely lead to a cascade of follow-on errors that would be overwhelming and unhelpful.

## No warnings (the compiler is not a linter)

The Nanyx compiler itself never emits warnings; only compile-time errors. Warnings can be ignored or turned off, and contributors may disagree on whether a warning should be heeded or not. The compiler's job is to check types and surface errors, not to enforce style or best practices. We do not believe that a developer should be blocked from running their program or its tests because they commented a line out and were left with a now-unused variable.

Instead, the Nanyx compiler follows the [dotnet analyzer model](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/overview?tabs=net-10), where linting, style rules, and other code quality checks are implemented as separate tools that can be plugged into the compiler and run during compilation.

## Human-readable errors

In the spirit of Elm and Rust, Nanyx aims to have human readable and understandable compiler messages. Bear in mind that the majority of the time a developer will need minimal information to understand a compiler message; most likely the developer will already have seen that specific error message hundreds of times before. Sometimes, however, the developer will never have seen the message before and will need more information to understand it. Errors should support both these scenarios by providing a short, crisp description of the error, followed by more detail and context, and then suggestions / fixes so the developer can correct the problem.

A Nanyx error should include:

| Element | Description |
|---------|-------------|
| Title | An error code, a short, generic description of the error, and the location in the source code where the error occurred |
| Message | A more detailed explanation of the error, using actual names and context from the source that produced the error |
| Suggestions (optional) | One or more actionable suggestions for how to fix the error, "Did you mean..." etc |
| Fixes (optional) | One or more automated fixes that the user's editor can apply |

## Friendly errors

In Nanyx error messages should be written in the passive voice (no "I" or "you"), as if the compiler is narrating what _was done_ and what _was found_. Messages should be matter of fact, rather than directly addressing the user or directing blame. 

| Preferred | Not preferred |
|--------------|-----------|
| Type `string` is not assignable to `int` | You cannot pass a `string` to a function that expects an `int` |
| Duplicate definition `foo` | I found two definitions of `foo` |
| | "You forgot to handle the `#error` case in this match expression", or "Illegal foo".|

An example of a good error message:

| Title | Message | Suggestions | Fixes |
|-------|---------|-------------|-------|
| `E001: Type mismatch at 10:5` | Expected type `int` but found type `string` in the expression `x + 1` | Did you mean to convert `x` to an int with `int(x)`? | Apply `int(x)` conversion |
| `E002: Unsatisfied context at 115:8` | The function `println` can only be run from within a `Console` context | Did you mean to add the context `Console` to the containing function `main`? | Add context `Console` to `main` |
| `E003: Non-exhaustive pattern match at 42:3` | The match expression does not handle the case `#error(#databaseError)` of the result type `#some(Customer) \| #error(#notFound \| #databaseError)` | Did you forget to handle the `#databaseError` case? | Add a case for `#error(#databaseError)` |

## Structural type errors

One of the challenges with structural typing is the fact that two types may differ only in a deeply-nested field, making it difficult to identify the source of a type error ("I know these types aren't compatible; **why** aren't they compatible!?"). Nanyx's error messages include a diff of the expected and actual types, with the differences highlighted, in just the same way as a source control diff. This makes it much easier to identify the source of a type error, even when the types are large and complex.

# Correctness

Represent meaning in the type. Use tag unions and descriptive cases instead of magic values or implicit failure.

## Exhaustive pattern matches

The Nanyx compiler enforces that pattern matches handle all cases of an algebraic data type. A non-exhaustive match expression is a compile error: it blocks a release build, and in a debug build the match panics if an unhandled case is reached. We believe this encourages more robust code and enables safer refactoring of algebraic data types.

## Separation of pure and impure code

Nanyx supports both functional and imperative programming styles; we do not feel that mutation or side effects are bad, but _hidden_ mutation or side effects are.

So, the type and effect system of Nanyx cleanly and safely separates pure code from impure code. That is, if a function is pure then the programmer can trust that the function behaves like a mathematical function: it returns the same value when given the same arguments and it has no side-effects.

## Illegal states should be unrepresentable

We believe that a language should make it easy to make illegal states unrepresentable. For example, tag unions can be used to precisely define the possible values of a type.

## Effect transparency

Side effects should be visible in types and in code structure. Contexts and workflows make effects explicit without making them heavyweight.

# Language feel

## Minimal surface, maximal leverage

Nanyx tries to provide a language that is high-level and powerful, but also _feels small and simple_. It does this by providing features that are general and composable, rather than adding special syntax or constructs for specific cases. This keeps the language surface minimal while still enabling a wide range of patterns.

Favor small, orthogonal pieces that combine cleanly: pipelines, builders, and first-class functions that fit together without ceremony. 

Add features sparingly and keep syntax small. If a feature is powerful, it should work broadly and reduce the need for additional ones. Consistency lowers the learning curve and makes tools easier to build.

Strive for consistency; operators and symbols should always have a single, clear meaning. For example, braces are always a function definition, not a record or a block of statements. Users will pick the language up quickly if they can effectively predict what a piece of syntax does based on their understanding of another part of the language.

## One language

Nanyx is one programming language. The Nanyx compiler does not have feature flags or compiler plugins that change or extend the semantics of the language. We want to avoid fragmentation in the ecosystem where programs end up being written in different "dialects" of the language. There is one language, now and forever. Of course that does not imply that the language will not evolve over time.

## Sensible defaults

Nanyx should work well out of the box, with sensible defaults that cover common cases. The language should provide powerful tools for when you need to go beyond the defaults, but it should be easy to get started and be productive without needing to configure or customize the language.

Don't make the user write things they don't care about.

# Performance you can reason about

Defaults should be efficient and predictable. When performance matters, the language should give you clear paths to control it.

This is why Nanyx frees memory by [reference counting](../foundations/reference-counting) rather than with a garbage collector: a value is freed at a point the program determines, as soon as nothing refers to it, and there are no collector pauses.

Nanyx is cautious about implementing compiler optimizations that can make program performance less predictable (and that the user can't control). We don't want developers to accidentally tank the performance of their code by making an innocuous change that causes an object in a tight loop to be allocated on the heap instead of the stack.

Having said that, in the interests of productivity and the approachability of the language we do want to allow users to "not care" about performance in individual cases and just let the language decide. As a compromise between these two viewpoints, we prefer to provide clear, explicit tools for performance when needed, while keeping the common case straightforward and efficient.

# Developer productivity

Nanyx is designed to be a real-world, productive language for developers. It should be easy to write, read, and maintain code in Nanyx. The language should provide features that help developers be productive, such as powerful type inference, a rich standard library, and good tooling support, rather than chasing academic interest.

Editor tooling and the developer experience is the deciding factor in whether or not a language achieves widespread adoption. Nanyx should be designed for great editor support, readable error messages, and a smooth debugging experience.

# No global state

In Nanyx there is no global shared state. This avoids a plethora of issues, including difficulties with initialization order and race conditions in the presence of concurrency. A Nanyx programmer is free to construct some state in the main function and pass it around, but there is no built-in mechanism to declare global variables. In a real system, the programmer still has to deal with the state of the world, e.g. the state of the file system, the network, and other resources.

# No overloading

Nanyx does not have function overloading. Each function has a unique name, and the type system is powerful enough to express different behaviors without needing overloading. This keeps the language simpler and avoids ambiguity in function calls. If you need different behavior based on types, you can use pattern matching or tag unions to achieve that without overloading. See [Overloading](../coming-from-other-languages/overloading) for details.