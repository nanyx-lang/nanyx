---
title: "Protected types without the sigil"
description: "Proposal: declare a nominal type with type Point = protected ... instead of a $ on its name"
order: 1
---

This page proposes declaring a nominal type with a keyword on its declaration, `type Point = protected (x: int, y: int)`,
instead of a `$` on its name, `type $Point = (x: int, y: int)`. The name is then an ordinary type name everywhere it is
used, and the tools show which types are nominal. The work is tracked in
[#1221](https://github.com/nanyx-lang/nanyx/issues/1221).

**Status.** The compiler reads `protected` and decides nominality from the declaration, through
`TypeDefModifier.isNominal` and `TypeDefInfo.IsNominal`, rather than from the name. A `$` before a declared name still
declares a protected type and a `$` before a name where it is used is read and dropped, so `$Email` is `Email`; the
formatter writes the new form. The prelude declares `Memory` with `protected`. The language server marks nominal
types' names as semantic tokens with a `nominal` modifier, and `private` for private ones (`NominalNames.inModule`),
which the extension shows in italics, and hover names the kind and the home module. The official
linter's `dollarPrefixedType` rule reports each `$`, with a fix. Still to do: rewriting the docs and examples. The info view does not list a module's types,
so it has nothing to show the kind on yet.

The words, as this page uses them: a *nominal* type is one whose name matters, as opposed to an alias for a structural
record or union. There are two kinds. A *protected* type is nominal and its shape is visible everywhere, so `Email` is
still a `string`. A *private* type is nominal and its shape is hidden outside its home module. Today the `$` says
"nominal" and `private` picks the second kind; the proposal has a keyword for each kind and nothing for the umbrella.

# Motivation

A [nominal type](../advanced/nominal-and-protected-types) is declared with a `$` in front of its name, and the `$` is
part of the name: it is written at every use. The type, its constructor, the pattern that takes a value apart, the
attached definitions and a qualified reference all carry it:

```nanyx fragment
export type $Email = string
export def $Email.parse: string -> Result($Email, #invalidEmail) = { value -> ... }
def wrapped = $Email("ada@example.com")
def $Email(raw) = wrapped
def address: email.$Email = input \$Email.parse
```

The sigil says one thing, that only the home module can construct the type, and it says it in the places where that
matters least. Construction is already confined to the home module; a caller elsewhere can only read a value or pass it
on, and for that the type's nominality changes nothing. What the caller pays for the sigil is a name that is harder to
type and read, and that looks different from every other type name, including the structural types and contexts it sits
beside in a signature.

Two other things make the sigil a poor fit now.

- **Nominal contexts.** `$Memory` and `$HostRuntime` are contexts with the same prefix. [Contexts as
  attributes](./context-attributes) proposes writing a required context as `@Memory`; with the sigil kept that is
  `@$Memory`, two sigils on one name.
- **Nominality is carried in the name.** The parser reads `$Email` as the identifier `"$Email"`, and the type resolver
  decides whether a type is nominal by looking at the first character of its name (`isNominalName` in
  `TypeResolution.fs`). A property of a declaration is stored as a property of a string.

# Syntax

A protected type is declared with `protected` after the `=`, where `private` is written today:

```nanyx fragment
module email

export type Email = protected string

export def Email.parse: string -> Result(Email, #invalidEmail) = { value ->
  if value \contains("@") then #some(Email(value)) else #error(#invalidEmail)
}
```

Everything else is written as it is for any other type. `Email` is the type, `Email(value)` the constructor, `Email(raw)`
the pattern, `Email.parse` an attached definition, `email.Email` the qualified name. The rules do not change: the
constructor and the pattern are allowed only in the home module, the type is assignable to its shape, and two modules
that each declare an `Email` declare two different types.

A protected record type has the constructor every record type has, taking its fields by name, and the restriction
to the home module applies to it:

```nanyx fragment
export type User = protected (
  name: string
  email: Email
)

def user = User(name = "Ada", email = wrapped)   -- in module email only
```

A nominal context is declared the same way, and `scoped` still requires it:

```nanyx fragment
scoped context Memory = protected (...)
```

## Private types

Today a private type is written `type $UserId = private int`: nominal by the sigil, hidden by the keyword. With the
sigil gone, `private` says both:

```nanyx fragment
export type UserId = private int
```

A private type is nominal, since a type whose shape nobody else can see has nothing else to be, so `private` is the
other kind and never needs `protected` beside it; writing both is an error that says to keep `private`.

The documentation keeps "nominal" for the umbrella and names the kinds by their keywords. The page
[Nominal and protected types](../advanced/nominal-and-protected-types) becomes "Nominal types", with a section for
each kind: a protected type has a name that matters and a shape anyone can use; a private type has a name and nothing
else outside its module.

| | Today | Proposed |
|---|---|---|
| Nominal, shape visible (protected) | `type $Email = string` | `type Email = protected string` |
| Nominal, shape hidden (private) | `type $UserId = private int` | `type UserId = private int` |
| Nominal context | `context $Memory = (...)` | `context Memory = protected (...)` |
| Use the type | `$Email` | `Email` |
| Construct, in the home module | `$Email(v)` | `Email(v)` |
| Take apart, in the home module | `def $Email(raw) = e` | `def Email(raw) = e` |
| Attached definition | `def $Email.parse: ...` | `def Email.parse: ...` |
| Qualified | `p.$Person` | `p.Person` |

# Telling nominal types apart

Without the sigil, a reader of plain text cannot tell `Email` from a structural alias. The tools can, and the proposal
relies on them:

- **The extension** gives nominal types a semantic token modifier, so they are coloured apart from structural types and
  aliases in every position: declaration, use, constructor, pattern and attached definition. A second modifier marks
  the private ones, with its own colour where the theme has one.
- **Hover** on the name says which kind it is and where it lives: "protected type `Email = string`, declared in
  `email`", or "private type `UserId`, declared in `ids`". For a private type outside its module the shape is not shown,
  as it is not available.
- **The info view and library browser** list a module's types with their kind, so a module's API shows which of its
  types it alone can construct.
- **Diagnostics** name the kind. Calling a constructor outside its home module reports "the protected type `Email` can
  only be constructed in the module that declares it", as the same diagnostic does today for `$Email`.

What the sigil gave the reader in a code review, a diff or a docs page without the tools, the proposal gives up. The
cases where it matters are the home module's own code, where the kind of each type is already known, and a call to a
constructor elsewhere, which the compiler rejects. A reader of a signature elsewhere sees a type name, which is what
they need.

# What changes

- **Parser.** `typeNameIdentifier` in `ParseState.fs` reads `$` and keeps it in the name. It reads `$` only for the
  migration release, and then only to report it, and the type-definition parsers in `Program.fs` read `protected`
  where they read `private`, adding a `Protected` case to `TypeDefModifier` beside `Private`. `protected` joins the
  reserved words.
- **Type resolution.** `isNominalName` and the code around it in `TypeResolution.fs` decide nominality from the name.
  They decide it from the declaration's modifiers instead, through `TypeDefInfo`. `TyNominal` and everything that
  consumes it are unchanged.
- **Everything that matches on the name's first character.** `Ast.fs`, `Infer.fs`, `AttachedOperators.fs`, `CodeGen.fs`
  and `CoreIrLower.fs` look for `$` in a name. Each reads the type's nominality from its definition instead.
- **Formatter.** Writes `protected` after the `=`, as it writes `private`, and `scoped context Memory = protected (...)`
  for a nominal context.
- **Language server and extension.** The semantic token modifier, hover text and info view entries above. The TextMate
  grammar's rule for `$Name` goes, and `protected` joins its keyword list.
- **Documentation.** The nominal-and-protected-types page is rewritten as above, with every `$` removed.
  [Memory](../advanced/memory), [Contexts](../advanced/contexts), [Naming conventions](../tour/naming-conventions),
  [Context escape](./context-escape), [Hosts](./hosts), [Equality and well-known contexts](./equality-and-well-known-contexts)
  and the other pages that write `$Memory`, `$Email` or `$UserId`, about 170 occurrences, follow. The naming conventions
  table drops `$UserId` from its examples.
- **Standard library and examples.** `$Memory` in the prelude, and the examples that declare `$`-prefixed types, such as
  `money.nyx` and `constraints.nyx`, are rewritten.

## Migration

For one release the parser reads both forms: a `$` on a declared name declares a protected type, and `$Name` at a use
site is `Name`. A linter rule, `dollarPrefixedType`, reports each `$` with a fix that removes it and adds `protected`
to the declaration. The formatter writes the new form. The release after, `$` before a name is a parse error whose
message shows the declaration without it.

# Open questions

- **The word.** `protected` is the proposal, since it says what the type is, a type whose construction is protected, and
  pairs with `private`: both are about what the home module keeps to itself, the constructor alone or the shape too.
  `nominal` is kept for the umbrella and is not a keyword, since no declaration is nominal without being one kind or
  the other.
- **Before the name or after the `=`.** `type Point = protected (...)` matches `private` and reads as a property of the
  shape. `protected type Point = (...)` matches `scoped context` and reads as a property of the declaration, which is
  what it is. The proposal takes the first for consistency with `private`; if `private` moves, both move together.
- **Docs without tooling.** The docs site could render nominal types with the compiler's highlighting so that the
  distinction survives there. That is a docs-site change, not a language one, and can come later.
