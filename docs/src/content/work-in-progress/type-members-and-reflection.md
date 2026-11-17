---
title: "Type members and reflection"
description: "Associated types on contexts, types passed as arguments, and inspecting them in generic code that is checked once"
order: 4
---

This page records the design for two related features. **Associated types** let a context carry a type as one of its members, such as the element type of an iterable; they are built ([Contexts](../advanced/contexts#associated-types)), and what is left of them here is how they meet type arguments and the prelude contexts that move to them. **Type arguments** let a function take a type as a parameter and inspect its structure; their design was decided on 2026-11-17 and is filed in stages ([Plan](#plan)). Passing a type is built ([Type arguments](../tour/functions#type-arguments)); inspecting it is not yet. Both are the same thing: a record member whose value is a type.

# The problems

## Code written once per type

Serialization, equality, hashing, printing and mapping to storage all follow a type's structure. In Nanyx today each is either written by hand or built into the compiler. The [JSON cookbook](../cookbook/json-parsing) decodes field by field, so each field name appears twice:

```nanyx
def decodeUser: string -> Result(User, DecodeError) = { data ->
  data
    \json.decode(
      json.record(name = json.field("name", json.string), age = json.field("age", json.int))
    )
}
```

`dbg`, string interpolation and `==` work on any record only because they are compiler intrinsics. Nothing lets a library do the same.

## Types only in output position

Generics work well when a type appears in a function's inputs: in `list(a) -> int`, the argument says what `a` is. A decoder mentions its type only in its output:

```nanyx
def deserialise: [Decode(a)] string -> a
```

The context can supply the behaviour, as Haskell's `read` and Rust's `parse` do, but the call site has no good way to say which `a` it wants. The only way today is an annotation on the result, and where nothing fixes `a` the user gets an ambiguity error that is hard to read.

## Element types in constraints

A constraint over collections has to carry the element type everywhere, even though it follows from the container type:

```nanyx
def iterStrings: [Iter(c, a)] c -> () = { items -> ... }
```

# Decisions

- **Contexts have associated types,** described on the [Contexts](../advanced/contexts#associated-types) page.
- **A type can be passed as an argument,** built and described on the [Functions](../tour/functions#type-arguments) page.
- **Generic code can inspect a type argument**: whether it is a record or a union, and what its fields or cases are.
- **Generic code is checked once, not per instantiation.** Nanyx does not adopt compile-time templates, at least for now. In Zig's `comptime` and in C++ templates, generic code is type-checked separately for each type it is used at, and errors appear inside library code. Inspection in Nanyx is type-checked once, when the generic function is defined, as all generic code is today.

# Associated types

Associated types are built: a context's member of kind `type`, named `Iter(c).elem` or `elem` in a signature and fixed by name in a constraint, `[Iter(c, elem = string)]`, is described on the [Contexts](../advanced/contexts#associated-types) page. What is still open:

- **It is also a value of type `Type(elem)`,** as a [type argument](../tour/functions#type-arguments) is, so `Iter(c).elem` can be passed wherever a type argument is expected, and a generic function over iterables can inspect its element type ([#1999](https://github.com/nanyx-lang/nanyx/issues/1999)).

## The prelude moves to them

Every prelude context whose later parameter is fixed by its first is a context with an associated type: `FromString(a)` has its `failure` ([#1801](https://github.com/nanyx-lang/nanyx/issues/1801)), and `Iterable(c)` its `elem` ([#1802](https://github.com/nanyx-lang/nanyx/issues/1802)). No other context in the prelude or the shipped packages has that shape.

# Type arguments

Passing a type is built: a parameter `type a` or `into: type` of type `Type(a)`, the value `type Point` of type `Type(Point)`, always written at the call and never left out, and known at compile time, as the [Functions](../tour/functions#type-arguments) page describes. It is the same idea as Haskell's `Proxy a`, Scala's `ClassTag` and Kotlin's `KClass<T>`. A `Type` value holds nothing at run time yet. What is open is inspecting one.

Decided by the maintainer on 2026-11-17:

- **A nominal type is read-only to reflection outside its home module:** there its description is `#nominal`, with its name and module but no structure and no `build`. Its encoding comes from its attached definitions or a context its module provides. Deserialisation-only constructors are logged for later ([#2001](https://github.com/nanyx-lang/nanyx/issues/2001)).
- **Narrowing on a `Type` match only,** not full GADTs, and **a `build` the compiler provides** for decoding, not rank-2 functions in the language.
- **A description contains** field and case names and types, the type's name and module, its type parameters, attributes on fields and doc comments.
- **Customising an encoding:** a type that provides the library's context itself wins over walking its description, as an explicit instance already wins over synthesis, and attributes on fields cover renames without one.
- **`dbg`, interpolation and `==` become library code over descriptions, eventually,** once reflection is proven and measured ([#2000](https://github.com/nanyx-lang/nanyx/issues/2000)).

## Descriptions are in the module only when used

Since a `Type` value is known at compile time, reflection stays out of the runtime, which matters for a single WebAssembly module whose unused code is removed:

- The module contains a description only of the types that are written as `type X` somewhere in the program, or that need a [`Typed` instance](#the-synthesised-typeda-context). Nothing has to be kept just in case it is reflected over.
- Because every `Type` value is a constant after monomorphisation, the compiler can specialise code that walks a type's description. The output is close to what a template would produce, while the checking stays that of ordinary generic code. This is an optimisation, not part of the semantics.

## Inspection with typed type descriptions

The leading design gives `Type(a)` a structure that can be matched. Each case tells the type checker more about the type:

```nanyx
def encodeAt: a, type a -> Json = { value, t ->
  match t
    | #int -> json.int(value)              -- here a = int
    | #string -> json.string(value)
    | #list(elem) -> value \map { x -> encodeAt(x, elem) } \json.array
    | #record(fields) ->
        fields \map { f -> (f.name, encodeAt(f.get(value), f.type)) } \json.object
    | #union(cases) -> ...
}
```

Each field packages up some type `b` that the checker never learns, together with `name: string`, `type: Type(b)` and `get: a -> b`. The checker only needs to know that `f.get(value)` and `f.type` agree to check the recursive call once.

Decoding builds a record instead of taking one apart. It needs a function that works for every field's type:

```nanyx
| #record(r) -> r.build { f -> decode(obj \json.field(f.name), f.type) }
```

The type system needs three additions, as decided:

- **Narrowing on a `Type` match** only: in the `#int` arm, `a` is known to be `int`, as TypeScript and Kotlin narrow on type tests. Not general GADTs.
- **Packaged types** (existential types) for fields and union cases.
- **A `build` the compiler provides** for each record description and union case. The function given to it must work for every field type, a rank-2 type, which users never write.

Precedents: Jane Street's `typerep` for OCaml, GHC's `Type.Reflection`, and the "Scrap your boilerplate" approach (`Data`). Swift's `Mirror` and Go's `reflect` have a similar shape but are not typed.

## The synthesised `Typed(a)` context

`Typed(a) = (typeOf: Type(a))` is built: the compiler gives an instance for every type, nominal ones too, so a function that is given a value of type `a` needn't also take `type a` ([Type arguments](../tour/functions#type-arguments)). It is the implicit counterpart of a type argument, as Haskell's `Typeable` is of `Proxy`, and as Scala's `ClassTag` is usually an implicit parameter. What is left open is what its `typeOf` describes, which comes with descriptions ([#1996](https://github.com/nanyx-lang/nanyx/issues/1996)):

- **The walker stays explicit.** When `encodeAt` recurses into a record's fields, each field's type is one the checker never learns, so there is no `Typed` instance for it; only `f.type`. A library therefore takes `Typed(a)` in its public function and passes the type explicitly in the code that walks it: `def encode: [Typed(a)] a -> Json = { value -> encodeAt(value, typeOf) }`.
- **A nominal type gives nothing away.** Outside the type's home module its description is the read-only `#nominal`, so giving `Typed` of it everywhere reveals no structure.

# Alternatives considered

## Only the positional `type a` form

An earlier draft had only `type a`, as in `(string, type a) -> a`, as a form of its own beside associated types. Adding the named form `into: type` makes a type parameter the same kind of member as an associated type, gives the argument a name at the call site, and matches the constraint syntax. `type a` stays as the positional counterpart.

## Passing a named type member positionally

`deserialise(text, type Point)` could be allowed for `(string, into: type) -> into` by letting type arguments skip the rule that a named field is passed by name. That would make type arguments the one exception to a rule that holds for every call and every record construction. A function's author who wants positional calls declares `type a` instead.

## Runtime reflection

C# and Java keep a description of every type at runtime and let any code inspect any value. In a single WebAssembly module this costs binary size, defeats removal of unused code, and needs a description format that the runtime and compiler agree on. It would also be an implicit capability, which in Nanyx would have to be a context; `Typed(a)` is that context, resolved at compile time.

## Compile-time templates

Zig's `comptime`, C++26 static reflection and D's templates allow arbitrary code over types at compile time. They are very expressive, but generic code is only checked when it is instantiated, which conflicts with the decision to check generic code once. This could be revisited as a later extension if typed descriptions prove too limited.

## Type-level induction over structure

The compiler could expose a record as a type-level list of field names and types, and libraries would handle the "no fields" and "one field, then the rest" cases with contexts whose instances the compiler chains together. This is how PureScript's `RowToList` (used by `simple-json`), GHC Generics, Scala 3 Mirrors and shapeless work, and it suits Nanyx's row-like records. It needs instances for type constructors that themselves require contexts, and field names as type-level strings. Users would program at the type level, and errors would come from failed instance searches, which these libraries are known for.

## Deriving clauses and macros

Haskell's `deriving`, Rust's `#[derive]` and procedural macros, and OCaml's ppx all generate code from a type's definition. Nanyx has no compiler plugins that extend the language, which rules out procedural macros and ppx. A built-in set of derivations would not let libraries add their own, such as JSON or SQL mapping.

## Structural context synthesis

Context synthesis works by name: the compiler builds `Add(Point)` from `Point`'s attached definitions. Only the [well-known contexts](../advanced/contexts#common-contexts), `Eq`, `Ord`, `Hash` and `ToString`, are also derived from structure, so that `Eq` of a record is built from the `Eq` of its fields. Opening that to any context would need no new syntax, but it cannot express library-specific behaviour such as JSON field names.

## Source generators

C#'s source generators run user code at compile time that emits more source. The [linter](../advanced/linters) infrastructure could host them, since linters will already run Nanyx code at compile time. Generated code would have to exist before type checking, while linters run after it, so generators would need their own phase and a narrower view of the program. They remain an option for cases that type arguments cannot reach.

# Open questions

- **Deserialisation-only constructors** for nominal types, logged for later ([#2001](https://github.com/nanyx-lang/nanyx/issues/2001)).
- **The description's exact shape:** the case names and the fields of `#record`, `#union` and `#nominal`, settled as [#1996](https://github.com/nanyx-lang/nanyx/issues/1996) is built.

# Plan

| Step | Issue | After |
|---|---|---|
| Type arguments: `type a`, `into: type`, `Type(a)` values (built) | [#1995](https://github.com/nanyx-lang/nanyx/issues/1995) | |
| Type descriptions, matched with narrowing | [#1996](https://github.com/nanyx-lang/nanyx/issues/1996) | #1995 |
| A compiler-provided `build` for decoding | [#1997](https://github.com/nanyx-lang/nanyx/issues/1997) | #1996 |
| `Typed(a)`, synthesised for every type (built) | [#1998](https://github.com/nanyx-lang/nanyx/issues/1998) | #1995 |
| An associated type as a `Type` value | [#1999](https://github.com/nanyx-lang/nanyx/issues/1999) | #1995 |
| JSON for any record, on reflection | [#1789](https://github.com/nanyx-lang/nanyx/issues/1789) | #1996, #1997, #1998 |
| `dbg`, interpolation and `==` as library code (Planning) | [#2000](https://github.com/nanyx-lang/nanyx/issues/2000) | #1997, #1998 |
| Deserialisation-only constructors (Planning) | [#2001](https://github.com/nanyx-lang/nanyx/issues/2001) | #1997 |
