---
title: "Type members and reflection"
description: "Associated types on contexts, types passed as arguments, and inspecting them in generic code that is checked once"
order: 4
---

This page records an early design discussion about two related features, neither of which is implemented. **Associated types** let a context carry a type as one of its members, such as the element type of an iterable. **Type arguments** let a function take a type as one of its parameters, `into = type Point`, and inspect it without becoming a template. The proposal is that both are the same thing: a record member whose value is a type. Little here is settled; the sections below separate what has been decided from the current direction and the open questions.

# Background

## The use cases

Most languages reach for reflection, macros or code generation to avoid writing the same code once per type. The cases that matter most for Nanyx are:

- **Serialization**, such as JSON encoding and decoding.
- **Equality and hashing** of records and unions.
- **Printing and debugging** values.
- **Mapping to storage**, such as SQL rows.
- **Test frameworks**, which discover and describe tests.
- **Dependency injection**, which [contexts](../advanced/contexts) already cover: a function declares what it needs and the caller loads it.

Today, each of these is either written by hand or built into the compiler. The [JSON cookbook](../cookbook/json-parsing) writes every decoder and encoder field by field, so each field name appears twice:

```nanyx
def decodeUser: string -> Result(User, DecodeError) = { data ->
  data
    \json.decode(json.record(
      name = json.field("name", json.string)
      age = json.field("age", json.int)
    ))
}
```

Printing and equality are compiler intrinsics: `dbg` and string interpolation print tags, and `==` compares any value but a function by its structure. Nothing in the language lets a library do the same for any record.

Generic APIs over collections have a smaller problem. A constraint has to carry both the container type and the element type everywhere, even where the element type follows from the container:

```nanyx
def iterStrings: [Iter(c, a)] c -> () = { items -> ... }
```

## What Nanyx already has

- **Context synthesis.** The compiler [builds a context](../advanced/contexts#context-synthesis) such as `Sum(Point)` from a type's attached definitions, and an explicit `use` overrides it. This is the closest thing Nanyx has to deriving, but it works by name, not by structure: nothing builds `Eq` of a record from the `Eq` of its fields.
- **`typeof`.** [`typeof x`](../tour/types#typeof) is a type expression that names the type of a value. It only appears in annotations and gives no access to the type's structure.
- **Attributes.** [Attributes](../tour/attributes) are compile-time metadata whose meaning the compiler defines. `@workflow` is documented as generating a helper, which is a small built-in form of code generation.
- **Linters.** [Linters](../advanced/linters) are planned as Nanyx packages compiled to WebAssembly and run at compile time on the engine the compiler already embeds. That infrastructure could later run code generators too.
- **Monomorphisation.** Generic code is compiled once for each set of concrete types it is used at, so every type is known at every use site when code is generated.
- **Parameters are a record.** A function's parameters are a [record](../tour/records), which may mix positional and named fields. This is how named arguments work.
- **Type names.** A [type's name](../tour/types#type-names) starts with a capital letter, and a type variable is a single lowercase letter. Longer lowercase names are reserved for associated types, except for the built-in types such as `int`, `string`, `bool` and `list`, whose names are reserved by the language.

## Constraints

Any design has to fit the language's [principles](../foundations/principles) and its target:

- **One language.** There are no compiler plugins that change or extend the language's semantics, which rules out procedural macros and syntax extensions.
- **Readable errors.** Errors must point at the user's code in terms the user wrote. Template and macro systems are notorious for errors that point inside library code.
- **Minimal surface.** A feature should be general enough to cover several of the use cases above, not one.
- **One WebAssembly module.** Runtime reflection needs a description of every type that might be inspected. In a single module whose unused code is removed, there is no way to know which types those are, so either everything is kept or reflection breaks.
- **Structural and nominal types.** A structural record's field names and types are part of its type, so its structure is always known. A [nominal type](../advanced/nominal-and-protected-types) can only be created by its home module, and reflection must not give a way around that.

# The problem: types only in output position

Generics work well when a type appears in a function's inputs: in `list(a) -> int`, the argument says what `a` is. A function such as a decoder mentions its type only in its output:

```nanyx
def deserialise: string -> a
```

Contexts can already supply the behaviour, as `[Decode(a)] string -> a`, in the way Haskell's `read` and Rust's `parse` work. What is missing is a good way for the call site to say which `a` it wants. The only way today is an annotation on the result, and where nothing fixes `a` the user gets an ambiguity error that is hard to read.

# Decisions

## A type can be passed as an argument

A function can take a type as an ordinary argument, and the caller writes the type at the call site. This combines generics with something like C#'s `typeof` in one feature: the type argument fixes a type variable, and the function's body receives a value that stands for the type. The syntax is part of the [current direction](#a-type-argument-is-a-type-member-of-the-parameters).

## Generic code can inspect a type argument

The body of a function can look at a type argument's structure: whether it is a record or a union, and what its fields or cases are.

## Generic code is checked once, not per instantiation

Nanyx does not adopt compile-time templates, at least for now. In Zig's `comptime` and in C++ templates, generic code is type-checked separately for each type it is used at, and errors appear at the instantiation, inside library code. Inspection in Nanyx must be type-checked once, when the generic function is defined, as all generic code is today.

# Current direction

These points follow from the decisions above. They have been proposed but not agreed.

## Associated types

A context can declare a member whose value is a type. Other members refer to it by name:

```nanyx
context Iter(c) = (
  elem: type
  next: c -> #some(elem, c) | #done
)
```

A function then constrains only the container type, and can fix the associated type in the constraint:

```nanyx
def iterStrings: [Iter(c): (elem = string)] c -> () = { items ->
  for items | item -> dbg(item)
}
```

Elsewhere in the signature the associated type is written `Iter(c).elem`, or just `elem` where only one constraint has a member of that name, as the [`iter` module](../stdlib/iter) assumes:

```nanyx
def headOr: [Iter(c)] (c, Iter(c).elem) -> Iter(c).elem = { items, fallback ->
  match next(items)
  | #some(value, _) -> value
  | #done -> fallback
}
```

Contexts compose, so a function asks only for the capabilities it uses, and each associated type lives on the context that owns the operation:

```nanyx
context Sized(c) = (
  length: c -> int
)

context Indexed(c) = (
  elem: type
  get: (c, int) -> elem
)

def countItems: [Sized(c)] c -> int = { items ->
  length(items)
}
```

## A type argument is a type member of the parameters

Since a function's parameters are a record, a type argument is a parameter declared the way a context declares an associated type:

```nanyx
def deserialise: [Decode(into)] (string, into: type) -> into = { text, t -> ... }

def p = deserialise('{"x": 1, "y": 2}', into = type Point)
```

The call site matches the constraint syntax: `into = type Point` fixes a parameter's type member as `(elem = string)` fixes a context's. Because positional and named fields mix, `deserialise(text, type Point)` works too. One concept covers both features: a record member of kind `type`.

A type member's name can't clash with a built-in type. [Built-in type names](../tour/types#type-names) such as `int`, `string`, `bool` and `list` are reserved by the language, so `(string, list: type) -> list` is an error.

## `x: type` is a value of type `Type(x)`

`into: type` is shorthand for a parameter `into: Type(into)`, where `into` is an ordinary type variable, quantified over the whole signature as `a` is today. The expression `type Point` has type `Type(Point)`. Type inference treats type arguments like any other: unifying `Type(Point)` with `Type(into)` solves `into = Point`. This is why `into` can be used in the result type and in the `[Decode(into)]` constraint to its left with no new scoping rule. No dependent types are needed, and partial application, pipes and passing the function around all keep working. Inside generic code, `type a` with a type variable `a` is allowed, and monomorphisation resolves it.

This is the same idea as Haskell's `Proxy a`, Scala's `ClassTag` and Kotlin's `KClass<T>`.

The same reading would apply to associated types: a context's `elem: type` member would also be a value of type `Type(elem)`, so `Iter(c).elem` could be passed wherever a type argument is expected, and a generic function over iterables could inspect its element type.

## Type arguments are known at compile time

A `Type` value must be known at compile time: it cannot be computed at runtime, stored in a list of different types, or chosen with `if`. Otherwise a function's return type would depend on a runtime value. This keeps reflection out of the runtime:

- The module contains a description only of the types that are written as `type X` somewhere in the program, so nothing has to be kept just in case it is reflected over.
- Because every `Type` value is a constant after monomorphisation, the compiler can specialise code that walks a type's description. The output is close to what a template would produce, while the checking stays that of ordinary generic code. This is an optimisation, not part of the semantics.

## Inspection with typed type descriptions

The leading design gives `Type(a)` a structure that can be matched. Each case tells the type checker more about the type:

```nanyx
def encode: (value: item, item: type) -> Json = { value, t ->
  match t
    | #int -> json.int(value)              -- here item = int
    | #string -> json.string(value)
    | #list(elem) -> value \map { x -> encode(x, elem) } \json.array
    | #record(fields) ->
        fields \map { f -> (f.name, encode(f.get(value), f.type)) } \json.object
    | #union(cases) -> ...
}
```

Each field packages up some type `b` that the checker never learns, together with `name: string`, `type: Type(b)` and `get: a -> b`. The checker only needs to know that `f.get(value)` and `f.type` agree to check the recursive call once.

Decoding builds a record instead of taking one apart. It needs a function that works for every field's type:

```nanyx
| #record(r) -> r.build { f -> decode(obj \json.field(f.name), f.type) }
```

The type system would need three additions:

- **Narrowing on a `Type` match.** In the `#int` arm, `item` is known to be `int`. General GADTs provide this; a smaller feature would narrow only when matching on a `Type` value, much as TypeScript and Kotlin narrow on type tests.
- **Packaged types** (existential types) for fields and union cases.
- **Rank-2 functions** for `build`, because the function passed to it must work for every field type. Only decoding needs this.

Precedents: Jane Street's `typerep` for OCaml, GHC's `Type.Reflection`, and the "Scrap your boilerplate" approach (`Data`). Swift's `Mirror` and Go's `reflect` have a similar shape but are not typed.

# Alternatives considered

## A separate `type a` parameter form

An earlier draft wrote a type parameter as `type a`, as in `(string, type a) -> a`, a form of its own beside associated types. Writing it as a type member instead means one concept rather than two, gives the argument a name at the call site, and matches the constraint syntax.

## Runtime reflection

C# and Java keep a description of every type at runtime and let any code inspect any value. In a single WebAssembly module this costs binary size, defeats removal of unused code, and needs a description format that the runtime and compiler agree on. It would also be an implicit capability; in Nanyx it would have to be a context. The static type arguments above give the useful parts without any of this.

## Compile-time templates

Zig's `comptime`, C++26 static reflection and D's templates allow arbitrary code over types at compile time. They are very expressive, but generic code is only checked when it is instantiated, which conflicts with the decision to check generic code once. This could be revisited as a later extension if typed descriptions prove too limited.

## Type-level induction over structure

The compiler could expose a record as a type-level list of field names and types, and libraries would handle the "no fields" and "one field, then the rest" cases with contexts whose instances the compiler chains together. This is how PureScript's `RowToList` (used by `simple-json`), GHC Generics, Scala 3 Mirrors and shapeless work, and it suits Nanyx's row-like records. It needs instances for type constructors that themselves require contexts, and field names as type-level strings. Users would program at the type level, and errors would come from failed instance searches, which these libraries are known for.

## Deriving clauses and macros

Haskell's `deriving`, Rust's `#[derive]` and procedural macros, and OCaml's ppx all generate code from a type's definition. Procedural macros and ppx conflict with "one language". A built-in set of derivations would not let libraries add their own, such as JSON or SQL mapping.

## Structural context synthesis

Context synthesis could be extended so that the compiler builds `Eq`, `Hash` or `Show` of a record or union from the instances of its fields. This needs no new syntax and covers equality, hashing and printing well. It cannot express library-specific behaviour such as JSON field names. It may still be worth doing alongside type arguments.

## Source generators

C#'s source generators run user code at compile time that emits more source. The linter infrastructure could host them, since linters will already run Nanyx code at compile time. Generated code would have to exist before type checking, while linters run after it, so generators would need their own phase and a narrower view of the program. They remain an option for cases that type arguments cannot reach.

# Open questions

- **One name in two namespaces.** In `(value: item, item: type)`, `item` is a type in the signature and a `Type(item)` value in the body. Is that pun clear enough, or should the value be reached some other way?
- **`type` at the call site.** A constraint writes `(elem = string)`, but a call writes `into = type Point`. The keyword is needed because arguments are expressions and `Point` could be a value or a constructor. Can the checker do without it when the parameter is known to be a type member, or is the difference worth keeping?
- **Bare associated type names.** When can a signature write `elem` rather than `Iter(c).elem`? The [`iter` module](../stdlib/iter) assumes it can whenever only one constraint has an `elem`.
- **Nominal types.** The description of a nominal type such as `$Email` must not offer `build` outside its home module, or decoding could create values that bypass the type's invariants. Does a nominal type appear as a closed case (for example `#nominal(name)`) whose encoding must come from attached definitions or a context, or does it expose its structure only for reading?
- **Customisation.** How does a type change how it is encoded, for example to rename a field or use a different representation? This probably ties in with attached definitions and with explicit `use` taking priority over synthesis.
- **Rank-2 types.** Are rank-2 functions acceptable for `build`, or should decoding be limited to an applicative-style `build` that the compiler provides?
- **How much narrowing.** Is narrowing on `Type` matches enough, or would full GADTs be needed?
- **Omitting type arguments.** Can a caller leave out `type Point` when the result type is already known, as in `def p: Point = deserialise(text)`? Requiring it keeps calls explicit and fits [no overloading](../foundations/principles#no-overloading); allowing it saves passing type arguments through layers of generic code.
- **What a description contains.** Field names and types, case names and payloads, certainly. Should it also include attributes on fields, the type's name and module, and its type parameters?
- **Built-in intrinsics.** Should `dbg`, interpolation and `==` be reimplemented as ordinary library code over type descriptions once they exist?
