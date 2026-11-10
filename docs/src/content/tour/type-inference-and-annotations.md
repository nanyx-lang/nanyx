---
title: "Type inference and annotations"
description: "How Nanyx infers types and when to annotate"
order: 7
---

Type inference is foundational in Nanyx; type annotations very rarely _need_ to be written as the compiler infers types from values and usage. Annotations are, however, recommended for definitions exported from a module and _strongly_ recommended for definitions exported from a package.

# Inference by value

```nanyx
def message = "Hello world" -- inferred as string

def message: string = "Hello world" -- explicitly annotated
```

# Inference for functions

```nanyx
def add = { a, b -> a + b }
-- inferred as `int, int -> int` from the usage of `+` on `a` and `b`
```

The inferred types on this page are written as an editor shows them, in hovers and in the lens above a definition. A function's parameters are written bare, as `int, int -> int`, which is the same type as `(int, int) -> int`.

Since functions conceptually have exactly one parameter, the parameter types are inferred from how the function is used. If the function is never called, or if the parameter types cannot be inferred from the body of the function, then the parameter types will be inferred as generic type variables, and the function will be inferred as a generic function:

```nanyx
def identity = { x -> x }
-- inferred as `α -> α`, meaning it can take any type `α` and returns the same type `α`
```

A type variable the compiler made up is named with a Greek letter, `α`, `β`, `γ` and so on, so it is easy to tell from one written in an annotation, which keeps its own name. In the source you write type variables in plain letters, as in `def identity: a -> a`. If you would rather see ASCII in the editor too, turn on [`nanyx.asciiTypes`](../standard-tools/vs-code-extension#how-types-are-shown).

## Inferring tag unions

When the branches of an `if` or `match` evaluate to different [tags](./tags), the compiler does not pick one branch's type and reject the others. It collects the tags from every branch into a union, so a function that returns a tag per outcome needs no annotation:

```nanyx
def getMessage = {
  def message = "Hello, from Nanyx!"
  if false then #some(message)
  else #noMessage
}
-- inferred as `() -> #some(string) | #noMessage`
```

The same applies across the arms of a `match`:

```nanyx
def classify = { n ->
  match n
    | 0 -> #zero
    | 1 -> #one(n)
    | _ -> #many(n)
}
-- inferred as `int -> #zero | #one(int) | #many(int)`
```

Only tags are collected this way. Branches of other types still have to agree, so an `if` with an `int` in one branch and a `string` in the other is an error. Payloads are unified per tag, which means `#some(1)` in one branch and `#some("one")` in another is also an error, because a single `#some` cannot carry both.

An annotation still makes sense when you want the union fixed up front, for example to let a branch you have not written yet return a tag that no current branch produces:

```nanyx
def getMessage: () -> #some(string) | #noMessage | #error(string) = {
  if false then #some("Hello") else #noMessage
}
```

## Positional vs named inference

When functions are unannotated, Nanyx assumes that any deconstructed arguments are positional. If you want to use named parameters, annotate the function type:

```nanyx
-- Inferred as `int, int -> int` with positional parameters
def magnitude = { x, y -> math.sqrt(x * x + y * y) }
```

```nanyx
-- The annotation makes field names part of the function signature
def magnitude2: (x: int, y: int) -> float = { x, y -> math.sqrt(x * x + y * y) }
```

Field names in function signatures should be a deliberate choice, since callers must use the field names to call the function:

```nanyx
magnitude2(3, 4) -- ✗ This will not compile because the function expects named parameters

magnitude2(x = 3, y = 4) -- The correct way to call a function with named parameters
```

This is the one rule for named fields everywhere in the language: it applies equally when constructing a record from a type alias (see [Constructing records from a type alias](../tour/records#constructing-records-from-a-type-alias)), so `Point(x = 3, y = 4)` is correct and `Point(3, 4)` is an error for a type with named fields.

Also, when fields in a function signature are named--as with any kind of deconstruction--deconstruction must also be by name in the function body (although if the deconstructed names are the same as the field names, you can collapse `field = field` to just `field`):

```nanyx
def magnitude2: (x: int, y: int) -> float = { x, y -> math.sqrt(x * x + y * y) }

-- or 

def magnitude3: (x: int, y: int) -> float
    = { x = innerX, y = innerY -> 
        math.sqrt(innerX * innerX + innerY * innerY) 
    }
```

# Partial annotations

An annotation does not have to spell out the whole type. Write `_` for any part of it and the compiler infers that part, as it would with no annotation at all:

```nanyx
def xs: list(_) = [1, 2]
-- inferred as list(int)

def add: (int, _) -> _ = { a, b -> a + b }
-- inferred as int, int -> int

def answer: #ok(_) | #error(string) = #ok(42)
-- the payload of #ok is inferred as int
```

This is useful when one part of a type is worth stating, such as a function's input, and the rest is obvious from the code. The parts you do write are still checked, so `def xs: list(_) = "hello"` is a type error.

Each `_` is a type of its own, and is filled in by inference rather than standing for any type. A named type variable is different: `a -> a` promises that the function works for every `a`, while `_ -> _` only says that it takes something and returns something, and leaves the rest to inference. So `def inc: _ -> _ = { x -> x + 1 }` is `int -> int`, where `def inc: a -> a = ...` is an error. A def whose annotation has holes is generalised just as an unannotated one is, so `def id: _ -> _ = { x -> x }` is still `α -> α`.

In an editor, hovering over a `_` shows the type it was inferred to be.

`_` can be used wherever the type of a value is written: a def's annotation, a local def's or a lambda parameter's. It cannot be used in a `type` definition, which says what a type is and so has nothing to infer it from:

```nanyx
type Names = list(_)
-- error NYX0216: '_' cannot be used in the definition of the type 'Names', only in the type of a value
```

# Exported values

By convention, exported values should be annotated:

```nanyx
export def add = { a, b -> a + b }
-- Linter warning: missing type annotation

export def add: (int, int) -> int = { a, b -> a + b }
```

Although the language itself doesn't mandate this practice, the [official style linter](../standard-tools/official-linter) will emit a warning if this is not followed.

# Target typing

Nanyx can infer types based on how values are used:

```nanyx
def parse: string -> a = { json -> ... }

def obj = parse("{}")

def person: Person = obj
```

You can also use assertions to guide inference:

```nanyx
def obj = parse("..."): Person
```
