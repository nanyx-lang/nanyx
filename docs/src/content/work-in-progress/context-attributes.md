---
title: "Contexts as attributes"
description: "Proposal: write the contexts a type requires as @ attributes on the type instead of a bracketed list"
order: 1
---

This page proposes replacing the bracketed list of contexts in a type, `[Console & ToString(a)] a -> ()`, with
attributes on the type: `@Console @ToString(a) a -> ()`. It is a proposal; the compiler reads the bracketed form
today.

# Motivation

A function's type says which contexts it requires, in brackets before the rest of the type:

```nanyx fragment
def greet: [Console] string -> () = { name -> println("Hello, {name}") }
def abs: [Num(a) & Ord(a)] a -> a = { n -> if n < 0 then -n else n }
```

Two things are wrong with the brackets.

- **They mean a list everywhere else.** `[1, 2, 3]` is a list, and `[]` an empty one. In a type, `[Console]` reads
  as "a list of Console" to anyone who has seen the expression syntax, and `[]`, which says a function is pure,
  reads as an empty list. The type `list(a)` is written with parentheses, so brackets in a type have no other meaning
  to fall back on.
- **They need a join operator.** Several contexts are joined with `&`, which is also intersection on types. A member
  requirement, `` [`+`: a, a -> a] ``, runs to the end of the set, so among other contexts it is bracketed again:
  `` [(`+`: a, a -> a) & Eq(a)] ``. An earlier form of the language joined with `+`, which the compiler still reads
  and the `plusBetweenContexts` linter rule reports.

Attributes already exist in the language and already mean "annotate the thing that follows": `@entry` before a
definition, `@partial` before a module declaration, `@customKeywordType(#bind)` before a record field. Writing a
context as an attribute on a type uses the same idea in one more position.

# Syntax

A context requirement is an attribute written straight before the type it qualifies: `@Name` for a context without
arguments, `@Name(args)` for one with them. Each context is its own attribute. There is no join operator.

```nanyx fragment
def greet: @Console string -> () = { name -> println("Hello, {name}") }
def abs: @Num(a) @Ord(a) a -> a = { n -> if n < 0 then -n else n }
def map.get: @Eq(k) @Hash(k) map(k, v), k -> #some(v) | #notFound = { m, key -> ... }
```

Everything the bracketed list could hold has a form.

| Today | Proposed | |
|---|---|---|
| `[Console] () -> ()` | `@Console () -> ()` | A context without arguments. |
| `[ToString(a)] a -> string` | `@ToString(a) a -> string` | A context with arguments. |
| `[Eq(k) & Hash(k)] k -> int` | `@Eq(k) @Hash(k) k -> int` | Several contexts, one attribute each. |
| `[$Memory] string -> ()` | `@$Memory string -> ()` | A nominal context keeps its `$`. |
| `[c] ([c] () -> a) -> a` | `@c (@c () -> a) -> a` | A context variable. |
| `[$Memory & c] () -> a` | `@$Memory @c () -> a` | A nominal context and a variable. |
| `` [`+`: a, a -> a] a, a -> a `` | `` @(`+`: a, a -> a) a, a -> a `` | A member requirement is a nameless attribute whose argument is the member. |
| `` [(`+`: a, a -> a) & Eq(a)] a, a -> a `` | `` @(`+`: a, a -> a) @Eq(a) a, a -> a `` | Among other contexts it needs no extra brackets, since each attribute ends where the next `@` begins. |
| `[] int -> int` | `@() int -> int` | A pure function, said on purpose. |
| `type T = [C] (int, string)` | `type T = @C (int, string)` | A context-qualified value type ([Context-qualified types](./context-qualified-types)). |
| `context Log = (log: [Console] string -> ())` | `context Log = (log: @Console string -> ())` | A member's own requirements. |

The member requirement and the pure form follow from one reading: a context written inline is a record type of its
members, and a nameless attribute `@(...)` holds such a record. `@()` is then the context with no members, which
requires nothing, and that is the pure form. It means the same as leaving the attributes out, and the formatter keeps
it, as it keeps `[]` today.

## Arguments and spacing

An attribute takes an argument only when the `(` follows its name with no space. That is already the rule for
attributes on declarations, so that `@entry (x)` is an attribute and then an expression, and it carries over
unchanged:

- `@ToString(a) a -> string` requires `ToString(a)` and takes an `a`.
- `@Console () -> ()` requires `Console` and takes nothing.
- `@Console() -> ()` is an error. `Console` takes no arguments, and the message says to put a space before the `()`.
- `@Log (int, int) -> int` requires `Log` and takes a pair.

## What attributes mean in each position

An attribute annotates what follows it, and what it means depends on what that is:

| Before | It is | Its argument is | Allowed names |
|---|---|---|---|
| a definition or module declaration | metadata the compiler consumes | an expression, `@customKeywordType(#bind)` | the attributes the compiler knows, such as `@entry` and `@partial` |
| a record literal's field | metadata on that field | an expression | the same |
| a type | a context the type requires | a type, `@ToString(a)` | any context, a context variable, or a nameless member set |

Only the syntax is shared. An attribute on a type is part of the type: it takes part in type equality and inference,
in the rule that a pure function can be given where one that requires contexts is expected, and in how the compiler
passes instances at run time. An attribute on a declaration does none of that. The two never share a position, so the
parser branches on where it is, and `@entry` on a type or `@Console` on a `def` is an error that names the right
position.

# What changes

- **Parser.** `contextSet` in `Nanyx.Parser/Types.fs` reads the brackets, the `&` or `+` join, and the bracketed
  member form. It becomes a parser for a run of `@` attributes, each a `typeApply`, `typeIdentifier`, or
  parenthesised `contextMember`, that ends at the first thing that is not an `@`. `typeWithContext` and
  `typeSignature` are unchanged apart from calling it. The AST keeps `TypeWithContext(contexts, inner)`: the list of
  contexts is the same list, written differently.
- **Formatter.** `contextSet` in `Nanyx.Formatter/Formatter.fs` writes `@` before each item and drops the extra
  brackets around a member requirement.
- **Type printer.** `Signatures.fs` and `Hover.fs` write the contexts of a type for hovers, diagnostics and the
  documentation; they switch to the same form.
- **Linters.** `plusBetweenContexts` goes: there is no join to get wrong. The official linter page loses its
  section.
- **Extension.** The TextMate grammar colours `@Name` as an attribute wherever it appears, which already covers the
  new position; a context in a type gets the same colour as `@entry`.
- **Documentation.** Every `[...]` context list in the docs, about 350 of them, is rewritten. The
  [Attributes](../tour/attributes) tour page gains a section, "On types", that links to
  [Contexts](../advanced/contexts). The contexts page, [Context-qualified types](./context-qualified-types),
  [Operator contexts](./operator-contexts) and [Numeric contexts](./numeric-contexts) show the new form.
- **Standard library and examples.** Every `.nyx` file is reformatted; the formatter does it once the parser reads
  both forms.

## Migration

The parser reads both forms for one release. The bracketed form is reported by a linter rule, `bracketedContexts`,
whose fix rewrites it, and the formatter writes the attribute form. The release after, the bracketed form is a parse
error whose message shows the attribute form of the same list.

# Open questions

- **`@()` or a word for pure.** `@()` falls out of the grammar and needs no reserved name. `@pure` reads better but
  reserves `pure` in type-attribute position, where a lowercase name is otherwise a context variable. The proposal
  takes `@()`; the choice is open.
- **Long signatures.** A bracketed list groups its contexts, and a run of attributes does not:
  `@Console @ToString(a) @Eq(a) (a, a) -> bool` is a wall of `@` where `[Console & ToString(a) & Eq(a)]` was one
  unit. The pages with the most multi-context signatures, numeric and operator contexts, should be rewritten by hand
  before the change is made, as the test of whether this reads well enough.
- **Where the attributes go when a signature wraps.** When a def's annotation is too long for one line, the
  attributes could stay on the first line, or each go on its own line as attributes on declarations do. Decide when
  the formatter changes.
