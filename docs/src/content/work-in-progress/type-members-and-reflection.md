---
title: "Type members and reflection"
description: "Associated types on contexts, types passed as arguments, and inspecting them in generic code that is checked once"
order: 4
---

This page records the design for two related features. **Associated types** let a context carry a type as one of its members, such as the element type of an iterable; they are built ([Contexts](../advanced/contexts#associated-types)), and what is left of them here is how they meet type arguments and the prelude contexts that move to them. **Type arguments** let a function take a type as a parameter and inspect its structure; their design is still early. The proposal is that both are the same thing: a record member whose value is a type. The sections below separate what has been decided from the current direction and the open questions.

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
- **A type can be passed as an argument.** The caller writes the type at the call site. The argument fixes a type variable, and the function's body receives a value that stands for the type. The syntax is part of the [current direction](#a-type-argument-is-a-type-member-of-the-parameters).
- **Generic code can inspect a type argument**: whether it is a record or a union, and what its fields or cases are.
- **Generic code is checked once, not per instantiation.** Nanyx does not adopt compile-time templates, at least for now. In Zig's `comptime` and in C++ templates, generic code is type-checked separately for each type it is used at, and errors appear inside library code. Inspection in Nanyx is type-checked once, when the generic function is defined, as all generic code is today.

# Associated types

Associated types are built: a context's member of kind `type`, named `Iter(c).elem` or `elem` in a signature and fixed by name in a constraint, `[Iter(c, elem = string)]`, is described on the [Contexts](../advanced/contexts#associated-types) page. What is still open:

- **It is also a value of type `Type(elem)`,** as a [type argument](#x-type-is-a-value-of-type-typex) is, so `Iter(c).elem` can be passed wherever a type argument is expected. That part is built with type arguments, not before.

## The prelude moves to them

Every prelude context whose later parameter is fixed by its first becomes a context with an associated type. `FromString(a)` has its `failure` already ([#1801](https://github.com/nanyx-lang/nanyx/issues/1801)), and no other context in the prelude or the shipped packages has that shape. What is left:

- **`Iterable(c)`,** planned on the [Lazy sequences](./lazy-sequences#taking-anything-that-can-be-iterated) page, has an associated `elem` type instead of a second parameter ([#1802](https://github.com/nanyx-lang/nanyx/issues/1802)).
# Current direction

These points follow from the decisions above. They have been proposed but not agreed.

## A type argument is a type member of the parameters

Since a function's parameters are a [record](../tour/records), a type argument is a parameter declared the way a context declares an associated type:

```nanyx
def deserialise: [Decode(into)] (string, into: type) -> into = { text, t -> ... }

def p = deserialise('{"x": 1, "y": 2}', into = type Point)
```

The call site matches the constraint syntax: `into = type Point` fixes a parameter's type member as `Iter(c, elem = string)` fixes a context's. One concept covers both features: a record member of kind `type`.

Like any named field, `into` is [passed by name](../tour/functions), so `deserialise(text, type Point)` is an error for this signature. A function whose callers should pass the type positionally declares a positional type parameter instead, written `type a`:

```nanyx
def deserialise: [Decode(a)] (string, type a) -> a = { text, t -> ... }

def p = deserialise('{"x": 1, "y": 2}', type Point)
```

The two forms follow the record rule for every other field. A named field is `name: T` and a positional field is just `T`; likewise a named type member is `into: type` and a positional one is `type a`. A positional field has no name of its own, so the type variable `a` is the only name, and it is a single letter like any other type variable. The signature is written the way the call is: `type a` in the signature, `type Point` at the call site. As with any parameter, the function's author chooses one form, and the caller follows it.

A type member's name can't clash with a built-in type. [Built-in type names](../tour/types#type-names) such as `int`, `string`, `bool` and `list` are reserved by the language, so `(string, list: type) -> list` is an error.

## `x: type` is a value of type `Type(x)`

`into: type` is shorthand for a parameter `into: Type(into)`, and the positional `type a` for a positional parameter of type `Type(a)`. In both, the name, `into` or `a`, is an ordinary type variable, quantified over the whole signature as `a` is today. The expression `type Point` has type `Type(Point)`. Type inference treats type arguments like any other: unifying `Type(Point)` with `Type(into)` solves `into = Point`. This is why `into` can be used in the result type and in the `[Decode(into)]` constraint to its left with no new scoping rule. No dependent types are needed, and partial application, pipes and passing the function around all keep working. Inside generic code, `type a` with a type variable `a` is allowed, and monomorphisation resolves it.

This is the same idea as Haskell's `Proxy a`, Scala's `ClassTag` and Kotlin's `KClass<T>`.

The same reading applies to associated types, as decided: a context's `elem: type` member is also a value of type `Type(elem)`, so `Iter(c).elem` can be passed wherever a type argument is expected, and a generic function over iterables can inspect its element type.

## Type arguments are known at compile time

A `Type` value must be known at compile time: it cannot be computed at runtime, stored in a list of different types, or chosen with `if`. Otherwise a function's return type would depend on a runtime value. This keeps reflection out of the runtime, which matters for a single WebAssembly module whose unused code is removed:

- The module contains a description only of the types that are written as `type X` somewhere in the program, or that need a [`Typed` instance](#a-synthesised-typeda-context). Nothing has to be kept just in case it is reflected over.
- Because every `Type` value is a constant after monomorphisation, the compiler can specialise code that walks a type's description. The output is close to what a template would produce, while the checking stays that of ordinary generic code. This is an optimisation, not part of the semantics.

## Inspection with typed type descriptions

The leading design gives `Type(a)` a structure that can be matched. Each case tells the type checker more about the type:

```nanyx
def encodeAt: (a, type a) -> Json = { value, t ->
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

The type system would need three additions:

- **Narrowing on a `Type` match.** In the `#int` arm, `a` is known to be `int`. General GADTs provide this; a smaller feature would narrow only when matching on a `Type` value, much as TypeScript and Kotlin narrow on type tests.
- **Packaged types** (existential types) for fields and union cases.
- **Rank-2 functions** for `build`, because the function passed to it must work for every field type. Only decoding needs this.

Precedents: Jane Street's `typerep` for OCaml, GHC's `Type.Reflection`, and the "Scrap your boilerplate" approach (`Data`). Swift's `Mirror` and Go's `reflect` have a similar shape but are not typed.

## A synthesised `Typed(a)` context

When a function already takes a value of type `a`, the caller shouldn't have to pass `type a` as well. A context that the compiler [synthesises](../advanced/contexts#context-synthesis) for every type supplies it instead:

```nanyx
context Typed(a) = (typeOf: Type(a))

def encode: [Typed(a)] a -> Json = { value ->
  encodeAt(value, typeOf)
}

def body = encode(point)
```

This is the implicit counterpart of a type argument, as Haskell's `Typeable` is of `Proxy`, and as Scala's `ClassTag` is usually an implicit parameter.

- **It stays static.** The compiler builds the instance where `a` is concrete, so after monomorphisation `typeOf` is a constant, as an explicit `type Point` is.
- **An explicit `use` can't make it lie.** An explicit `use` overrides a synthesised context as usual, but the only way to make a `Type(a)` is to write `type X` with `X` equal to `a`. An override can only supply the description the compiler would have built, so `Typed` needs no rule against overriding.
- **It is a value, not a function.** Context members can be values, as `zero: a` is in `Add`. A function that takes `()` would add nothing, since its result can never change.
- **It doesn't replace type arguments.** In `[Typed(a)] string -> a`, nothing at the call site fixes `a`, so the ambiguity in [types only in output position](#types-only-in-output-position) remains. `Typed` helps only where a value of type `a` is passed in.
- **The walker stays explicit.** When `encodeAt` recurses into a record's fields, each field's type is one the checker never learns, so there is no `Typed` instance for it; only `f.type`. A library therefore takes `Typed(a)` in its public function and passes the type explicitly in the code that walks it, as `encode` and `encodeAt` do above.

Synthesis makes the [nominal types question](#open-questions) more pressing. With explicit arguments, a caller has to write `type Email` to hand over a description of `Email`. With `Typed`, any generic function that asks for `Typed(a)` gets one without the caller writing anything. One answer is to synthesise `Typed` only for structural types, and let a nominal type's home module provide its instance.

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

- **Nominal types.** A [nominal type](../advanced/nominal-and-protected-types) such as `Email` can only be created by its home module. Its description must not offer `build` elsewhere, or decoding could create values that bypass the type's invariants. Does a nominal type appear as a closed case (for example `#nominal(name)`) whose encoding must come from attached definitions or a context, or does it expose its structure only for reading? Is `Typed` synthesised for it at all?
- **One name in two namespaces.** In `(string, into: type) -> into`, `into` is a type in the signature, and a field holding a `Type(into)` value at the call site. Is that pun clear enough?
- **`type` at the call site.** A constraint writes `Iter(c, elem = string)`, but a call writes `into = type Point`. The keyword is needed because arguments are expressions and `Point` could be a value or a constructor. Can the checker do without it when the parameter is known to be a type member, or is the difference worth keeping?
- **Customisation.** How does a type change how it is encoded, for example to rename a field or use a different representation? This probably ties in with attached definitions and with explicit `use` taking priority over synthesis.
- **Rank-2 types.** Are rank-2 functions acceptable for `build`, or should decoding be limited to an applicative-style `build` that the compiler provides?
- **How much narrowing.** Is narrowing on `Type` matches enough, or would full GADTs be needed?
- **Omitting type arguments.** Can a caller leave out `type Point` when the result type is already known, as in `def p: Point = deserialise(text)`? Requiring it keeps calls explicit and fits [no overloading](../foundations/principles#no-overloading); allowing it saves passing type arguments through layers of generic code.
- **What a description contains.** Field names and types, case names and payloads, certainly. Should it also include attributes on fields, the type's name and module, and its type parameters?
- **Built-in intrinsics.** Should `dbg`, interpolation and `==` be reimplemented as ordinary library code over type descriptions once they exist?
