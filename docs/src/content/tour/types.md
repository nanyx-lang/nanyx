---
title: "Types"
description: "Nanyx's type system"
order: 2
---

Nanyx features a strong static type system with Hindley-Milner type inference. Types are descriptive and readable, and you can use record types for structure and tag unions for variants.

# Type inference

Nanyx can infer most types automatically:

```nanyx
-- Type is inferred as: int -> int
def double = { x -> x * 2 }

-- Type is inferred as: string -> int, from the call to string.length
def length = { s -> string.length(s) }
```

# Type annotations

While inference works well, you can (and should) annotate exported functions:

```nanyx
-- Explicit type annotation
def double: int -> int = { x -> x * 2 }

-- Multi-parameter function type
def multiply: (int, int) -> int = { x, y -> x * y }
```

# Basic types

Nanyx has several built-in primitive types:

```nanyx
def count: int = 42
def price: float = 19.99
def name: string = "Alice"
def active: bool = true
```

There is no separate `number` type. Generic code over numbers requires a [context](../advanced/contexts): `Add(a)` to add and subtract, or `Num(a)` to multiply and divide as well. `int` and `float` are the built-in types that have both.

`int` is a 64-bit integer and `float` a 64-bit floating-point number, on every platform. Numbers of other fixed widths are types of their own: `I8`, `I16`, `I32`, `U8`, `U16`, `U32`, `U64` and `F32`. A literal takes the type the code around it expects, `def small: U8 = 200`, their arithmetic stops the program when the result does not fit, and each converts to the others with a definition named after the target, `I32.toInt` or `int.toU8`, which gives `#outOfRange` when the value may not fit; see [Sized numbers](../advanced/sized-numbers).

# Built-in type names

Each built-in type has a capitalised name, which is the type, and a lowercase alias, a keyword that means exactly that type, as `string` means `String` in .NET:

| Alias | Type |
|---|---|
| `string` | `String` |
| `list` | `List` |
| `unit` | `Unit` |
| `seq` | `Seq` |
| `map` | `Map` |
| `set` | `Set` |
| `int` | `I64` |
| `float` | `F64` |
| `bool` | `Boolean` |

The two are the same type everywhere, so `List(int)` and `list(int)` are one type, and so are `def String.trim` and `def string.trim` (see [Attached definitions](../advanced/attached-definitions#attaching-to-a-built-in-type)). The compiler prints the alias: an error message or a hover says `list(string)`.

`Int`, `Float` and `Bool` are not types at all: writing one is the error `NYX0114`, with a fix that writes `int`, `float` or `bool`.

# Record types

Records are structural types with named fields. They have their own Language Tour page covering record types, structural typing, optional fields, and the unified record/tuple model.

See [Records and tuples](./records) for the full guide.

# Top and bottom types

`any` is the top type (accepts any value), and `undefined` is the bottom type (no values).

`undefined` is the type of an expression that never gives a value, such as a call to a function that stops the program. Since there is no value of it, it can be given wherever a value of any type is expected (as an argument, or as what a function annotated to return some type returns), and an `if` or `match` with a branch of that type has the type of its other branches:

```nanyx
def stop: int -> undefined = { x -> stop(x) }

def double = { n -> if n > 0 then n * 2 else stop(n) }   -- int -> int
```

# Tuple types

Tuples are anonymous records with numbered fields:

```nanyx
-- Tuple type
def point: (int, int) = (10, 20)

-- Named tuple (equivalent to record)
def coords: (x: int, y: int) = (x = 10, y = 20)
```

# Unified tuples and records

In Nanyx, tuples and records are one unified structure. You can mix positional and named fields in the same record, making a tuple just a record with all positional fields.

```nanyx
def mixed = (1, 2, label = "origin", unit = "px")
```

The only restriction is ordering: all positional fields must come before any named fields.

```nanyx
-- Valid
def valid = (10, 20, x = 10, y = 20)

-- Invalid (named field before positional field)
-- def invalid = (x = 10, 20)
```

See [Records and tuples](./records) for more on record shapes and field rules.

# Tag unions

Tag unions (also called sum types or discriminated unions) represent values that can be one of several variants:

```nanyx
-- Simple tag union
type Result(a, e) = #some(a) | #error(e)

-- Multiple variants
type Shape =
  | #circle(float)
  | #rectangle(float, float)
  | #triangle(float, float)

-- Using tag unions
def area: Shape -> float = { shape ->
  match shape
    | #circle(r) -> 3.14159 * r * r
    | #rectangle(w, h) -> w * h
    | #triangle(b, h) -> 0.5 * b * h
}
```

## Recursive types

A tag union can name itself in the payload of one of its tags, so a value can hold other values of its own type. This is how you write a syntax tree, or a tree of any kind:

```nanyx
type Expr = #num(int) | #add(Expr, Expr)

def eval: Expr -> int = {
  | #num(n) -> n
  | #add(a, b) -> eval(a) + eval(b)
}

eval(#add(#num(2), #num(3)))  -- 5

type Tree(a) = #leaf | #node(Tree(a), a, Tree(a))

def size: Tree(a) -> int = {
  | #leaf -> 0
  | #node(l, _, r) -> size(l) + 1 + size(r)
}
```

A type can name itself only inside a tag's payload, where each value ends at a tag that holds no more of it, such as `#num` or `#leaf`. Anywhere else, as in `type Pair = (int, Pair)`, it is the error `NYX0228`. Like every alias, a recursive type is the type it stands for: two types with the same tags, such as `Expr` and a `Term = #num(int) | #add(Term, Term)`, are the same type.

Two types can't yet refer to each other, and the WebAssembly backend does not compile a type that holds itself at other type arguments, such as `#node(T(list(a)))` inside `T(a)`.

# Option types

Nanyx has no null values. Use tag unions to represent optional values instead:

```nanyx fragment
def findUser: UserId -> #some(User) | #notFound = { id ->
  def found = ... -- lookup logic
  if found then #some(user) else #notFound
}
```

# Generic types

Types can be parameterized with type variables:

```nanyx
-- Generic function (the type variable `a` is introduced automatically)
def identity: a -> a = { x -> x }

-- Generic list operations
def map: (list(a), (a -> b)) -> list(b) = { xs, f ->
  match xs
    | [] -> []
    | [head, ...tail] -> [f(head), ...map(tail, f)]
}

def zip: (list(a), list(b)) -> list((a, b)) = { xs, ys ->
  match (xs, ys)
    | ([], _) -> []
    | (_, []) -> []
    | ([x, ...xs2], [y, ...ys2]) -> [(x, y), ...zip(xs2, ys2)]
}
```

Type variables are automatically created when used in a function definition.

# Type names

The first letter of a name in a type says what kind of name it is:

- **A capital letter starts a type's name**, such as `Shape`, `UserId` or `Result(a, e)`. The compiler requires every type you define with `type` to start with a capital letter.
- **A single lowercase letter**, such as `a`, `b` or `c`, is a type variable. The parameters of a type or a [context](../advanced/contexts) are type variables, so each of them is a single lowercase letter too: `type Pair(a, b)`, `context Store(c, i)`. A longer name, `type Box(item)`, is an error where it is written.
- **Longer lowercase names** are reserved for [associated types](../work-in-progress/type-members-and-reflection#associated-types), which are not implemented yet.

The exceptions are the built-in types whose names are lowercase keywords for convenience: `int`, `float`, `string`, `bool`, `list`, `map`, `any` and `undefined`. These names are reserved by the language, so none of them can be the name of a type variable, an associated type or a type you define.

# Type aliases

Create meaningful names for complex types:

```nanyx
type UserId = int
type Email = string
type Age = int

type UserProfile = (
  id: UserId
  name: string
  email: Email
  age: Age
)
```

# Function types

Functions have specific type signatures:

```nanyx
-- Simple function type
type Validator(a) = a -> bool

-- Function with multiple parameters (takes a record)
type Transformer(a, b) = a -> b

-- Higher-order function type
type Mapper(a, b) = (list(a), (a -> b)) -> list(b)
```

## Function type precedence

In a type, `,` binds tighter than `->`, and `|` binds tighter than both. That means these two annotations are the same type:

```nanyx
def add: (int, int) -> int = { x, y -> x + y }
def sameAdd: int, int -> int = { x, y -> x + y }
```

Both describe a function whose single argument is a two-field record of ints. The same rule holds inside brackets, so `(int, a -> b)` is that kind of function too, in brackets, like `| a, b -> ...` in a match is a tuple pattern that goes to its body. Brackets only group; they never change what `,` and `->` mean. So:

- A function that is an item of a tuple, a type argument or a tag payload has brackets of its own: `(list(a), (a -> b)) -> list(b)`, `map(string, (a -> b))`.
- A union never needs them: `#some(int) | #none -> int` is a function from the union.
- In a record and in a lambda's parameter list the comma already separates fields and parameters, so a function with several parameters has them bracketed there: `(combine: (a, a) -> a)`.
- A record of named fields that is a type's only argument can share its brackets, as a tag's payload does: `list(name: string)` is `list((name: string))`. A tuple can't, because `list(int, string)` would be two arguments.

The compiler prints a type with the fewest brackets the rule allows, with a signature's parameters bare: `#some(int) | r, (int -> string) -> #some(string) | r`.

# typeof

`typeof x` is a type expression that means "whatever the type of the value `x` is", in the same way as TypeScript's `typeof`. It can be used anywhere a type is expected:

```nanyx
def defaults = (host = "localhost", port = 8080)

-- Same shape as `defaults`, without restating the record type
def override: typeof defaults = (host = "example.com", port = 443)
```

This is useful for referring to the type of a value whose type was inferred rather than declared, such as a builder record passed to `use`.

What follows `typeof` is a value's name or a path of fields, such as `typeof config.server.port`, never an arbitrary expression. Brackets only group it, so `typeof(defaults)` is the same type. The value can be declared later in the module, and a generic value gives a fresh copy of its type each time, as using it would. A type definition is not in the scope of any value, so `typeof` can only be written in the type of a value: a def's annotation or a lambda parameter's.
