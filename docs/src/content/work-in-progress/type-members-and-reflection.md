---
title: "Type members and reflection"
description: "Associated types on contexts, types passed as arguments, and inspecting them in generic code that is checked once"
order: 4
---

This page records an early design for two related features, neither of which is implemented. **Associated types** let a context carry a type as one of its members, such as the element type of an iterable. **Type arguments** let a function take a type as a parameter and inspect its structure. The proposal is that both are the same thing: a record member whose value is a type. Little here is settled; the sections below separate what has been decided from the current direction and the open questions.

# The problems

## Code written once per type

Serialization, equality, hashing, printing and mapping to storage all follow a type's structure. In Nanyx today each is either written by hand or built into the compiler. The [JSON cookbook](../cookbook/json-parsing) decodes field by field, so each field name appears twice:

```nanyx
def decodeUser: string -> Result(User, DecodeError) = { data ->
  data
    \json.decode(json.record(
      name = json.field("name", json.string)
      age = json.field("age", json.int)
    ))
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

- **A type can be passed as an argument.** The caller writes the type at the call site. The argument fixes a type variable, and the function's body receives a value that stands for the type. The syntax is part of the [current direction](#a-type-argument-is-a-type-member-of-the-parameters).
- **Generic code can inspect a type argument**: whether it is a record or a union, and what its fields or cases are.
- **Generic code is checked once, not per instantiation.** Nanyx does not adopt compile-time templates, at least for now. In Zig's `comptime` and in C++ templates, generic code is type-checked separately for each type it is used at, and errors appear inside library code. Inspection in Nanyx is type-checked once, when the generic function is defined, as all generic code is today.

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

Elsewhere in the signature the associated type is written `Iter(c).elem`, or just `elem` where only one constraint has a member of that name, as the planned `iter` module assumed (see [Lazy sequences](./lazy-sequences), which replaces it with `Iterable(c, a)`):

```nanyx
def headOr: [Iter(c)] (c, Iter(c).elem) -> Iter(c).elem = { items, fallback ->
  match next(items)
  | #some(value, _) -> value
  | #done -> fallback
}
```

Each associated type lives on the context that owns the operation, so a function asks only for the capabilities it uses:

```nanyx
context Indexed(c) = (
  elem: type
  get: (c, int) -> elem
)
```

The prelude's `FromString` is the case that motivates this today. As an ordinary context, the tag `fromString` gives back for text it cannot read has to be a second parameter, so every generic signature carries it, `[FromString(a, e)] list(string) -> list(#some(a) | e)`, even though `e` is always determined by `a`. With an associated type it would be:

```nanyx
context FromString(a) = (
  failure: type
  fromString: string -> #some(a) | failure
)

def readAll: [FromString(a)] list(string) -> list(#some(a) | FromString(a).failure)
```

and `int.fromString` would fix `failure` as `#invalidInt`.

An associated type's name is lowercase and longer than one letter, which [type names](../tour/types#type-names) reserve for associated types.

## A type argument is a type member of the parameters

Since a function's parameters are a [record](../tour/records), a type argument is a parameter declared the way a context declares an associated type:

```nanyx
def deserialise: [Decode(into)] (string, into: type) -> into = { text, t -> ... }

def p = deserialise('{"x": 1, "y": 2}', into = type Point)
```

The call site matches the constraint syntax: `into = type Point` fixes a parameter's type member as `(elem = string)` fixes a context's. One concept covers both features: a record member of kind `type`.

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

The same reading would apply to associated types: a context's `elem: type` member would also be a value of type `Type(elem)`, so `Iter(c).elem` could be passed wherever a type argument is expected, and a generic function over iterables could inspect its element type.

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
context Typed(a) = (
  typeOf: Type(a)
)

def encode: [Typed(a)] a -> Json = { value -> encodeAt(value, typeOf) }

def body = encode(point)
```

This is the implicit counterpart of a type argument, as Haskell's `Typeable` is of `Proxy`, and as Scala's `ClassTag` is usually an implicit parameter.

- **It stays static.** The compiler builds the instance where `a` is concrete, so after monomorphisation `typeOf` is a constant, as an explicit `type Point` is.
- **An explicit `use` can't make it lie.** An explicit `use` overrides a synthesised context as usual, but the only way to make a `Type(a)` is to write `type X` with `X` equal to `a`. An override can only supply the description the compiler would have built, so `Typed` needs no rule against overriding.
- **It is a value, not a function.** Context members can be values, as `` `0`: a `` is in `Sum`. A function that takes `()` would add nothing, since its result can never change.
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

Context synthesis works by name today: the compiler builds `Sum(Point)` from `Point`'s attached definitions, but nothing builds `Eq` of a record from the `Eq` of its fields. It could be extended to build `Eq`, `Hash` or `Show` of a record or union from the instances of its fields. This needs no new syntax and covers equality, hashing and printing well. It cannot express library-specific behaviour such as JSON field names. It may still be worth doing alongside type arguments.

## Source generators

C#'s source generators run user code at compile time that emits more source. The [linter](../advanced/linters) infrastructure could host them, since linters will already run Nanyx code at compile time. Generated code would have to exist before type checking, while linters run after it, so generators would need their own phase and a narrower view of the program. They remain an option for cases that type arguments cannot reach.

# Open questions

- **Nominal types.** A [nominal type](../advanced/nominal-and-protected-types) such as `Email` can only be created by its home module. Its description must not offer `build` elsewhere, or decoding could create values that bypass the type's invariants. Does a nominal type appear as a closed case (for example `#nominal(name)`) whose encoding must come from attached definitions or a context, or does it expose its structure only for reading? Is `Typed` synthesised for it at all?
- **One name in two namespaces.** In `(string, into: type) -> into`, `into` is a type in the signature, and a field holding a `Type(into)` value at the call site. Is that pun clear enough?
- **`type` at the call site.** A constraint writes `(elem = string)`, but a call writes `into = type Point`. The keyword is needed because arguments are expressions and `Point` could be a value or a constructor. Can the checker do without it when the parameter is known to be a type member, or is the difference worth keeping?
- **Bare associated type names.** When can a signature write `elem` rather than `Iter(c).elem`? The planned `iter` module assumed it can whenever only one constraint has an `elem`.
- **Customisation.** How does a type change how it is encoded, for example to rename a field or use a different representation? This probably ties in with attached definitions and with explicit `use` taking priority over synthesis.
- **Rank-2 types.** Are rank-2 functions acceptable for `build`, or should decoding be limited to an applicative-style `build` that the compiler provides?
- **How much narrowing.** Is narrowing on `Type` matches enough, or would full GADTs be needed?
- **Omitting type arguments.** Can a caller leave out `type Point` when the result type is already known, as in `def p: Point = deserialise(text)`? Requiring it keeps calls explicit and fits [no overloading](../foundations/principles#no-overloading); allowing it saves passing type arguments through layers of generic code.
- **What a description contains.** Field names and types, case names and payloads, certainly. Should it also include attributes on fields, the type's name and module, and its type parameters?
- **Built-in intrinsics.** Should `dbg`, interpolation and `==` be reimplemented as ordinary library code over type descriptions once they exist?
