# Equality and well-known contexts

> **Status: agreed, not implemented.** The decisions are listed under [Open questions](#open-questions); one small
> question remains. Code references are at commit `b84acc0`.

Nanyx mentions `Eq(a)`, `Ord(a)`, `Sum(a)` and `ToString(a)` in several places, but has never decided what equality
*is*, or which contexts the compiler knows about. This document records what exists today, then proposes one design
for equality and applies the same pattern to ordering, hashing and printing.

# What exists today

## Contexts: designed and implemented

Contexts are implicit parameters ([docs: Contexts](../../docs/src/content/advanced/contexts.md)). Implemented:

- **Declaring** a context as a record type: `context Show(a) = (show: a -> string)`.
- **Requiring** one in a signature: `def describe: [Show(a)] a -> string`. A function that does not write its type
  has its requirements inferred.
- **Loading** an instance: `use Show(Point) = ( show = ... )`. The call form `use Show(Point)(...)` that the docs use
  does not parse yet ([#333](https://github.com/nanyx-lang/nanyx/issues/333)).
- **Resolving** an instance at each call (`Infer.fs:877-902`, `resolveCallInstances`), first match wins:
  1. an instance for that exact type loaded with `use` around the call, nearest first (`Infer.fs:787-799`);
  2. the instance the enclosing function's own signature declares (a generic function passes its `[Show(a)]` on);
  3. an instance **synthesized** from the type's attached definitions, matched by member name: `Point.show` makes
     a `Show(Point)` (`Infer.fs:814-841`, [#328](https://github.com/nanyx-lang/nanyx/issues/328)). A missing or
     ill-typed definition is reported as `MissingContextMembers`, naming it.

Synthesis is **by name only**. Nothing builds an instance for `list(a)` from the instance for `a`, and no instance is
built from a type's structure ([type arguments and reflection](../../docs/src/content/work-in-progress/type-arguments-and-reflection.md),
lines 38 and 153-155).

## Equality: implemented as an untyped intrinsic

- **Type checking.** `==` and `!=` unify their two operands and return `bool`; nothing else is checked
  (`Infer.fs:1381-1382`). In effect `==` has the type `a, a -> bool` for *every* `a`, functions included, and adds
  no requirement to a generic function. `List.distinct: list(a) -> list(a)` relies on this
  (`stdlib/nanyx/prelude.nyx:281-288`).
- **Lowering.** After monomorphisation the backend picks an implementation from the operand's concrete type
  (`CoreIrLower.fs:479-504`): `i32.eq` for int, bool and unit; `f64.eq` for float; a byte comparison for strings;
  `__nanyx_tag_equals` for tags. Anything else, records, tuples, lists and functions, type-checks and then fails in
  the backend with `NYX0301 UnsupportedByBackend` ([#424](https://github.com/nanyx-lang/nanyx/issues/424)). A generic
  `def same: a, a -> bool = { x, y -> x == y }` is accepted, and fails only when some caller uses it at a record.
- **Tags compare their payload word.** `__nanyx_tag_equals` (`WasmRuntime.fs:667-698`) compares the payload with
  `i32.eq`, so a string or other boxed payload is compared by address: `#some("a" ++ "a") == #some("aa")` is
  `false` ([#526](https://github.com/nanyx-lang/nanyx/issues/526)).
- **Open unions.** Because `==` *unifies* its operands, `def isNone = { o -> o == #none }` infers `o: #none`, and
  `isNone(#some(3))` is a type error.

## Ordering: numbers only

`<`, `<=`, `>` and `>=` constrain both operands to a numeric type (`Infer.fs:1376-1380`). Strings, tuples and type
variables cannot be ordered ([#476](https://github.com/nanyx-lang/nanyx/issues/476)), which is why `List.sortBy`
takes `(a -> int)` keys (`prelude.nyx:301-302`) and there is no `List.sort`
([#475](https://github.com/nanyx-lang/nanyx/issues/475), [#191](https://github.com/nanyx-lang/nanyx/issues/191)).

## Hashing, maps and sets: nothing

There is no `Hash` context, no map or set type, and no map literal (#191, `groupBy`).

## Printing: designed, partly implemented

[Printing values](../../docs/src/content/work-in-progress/printing-values.md) proposes a `ToString(a)` context
behind interpolation holes, synthesized from an attached `toString`, with `dbg` separate and printing anything. The
attached `toString` on the built-in types exists ([#329](https://github.com/nanyx-lang/nanyx/issues/329)); checking
holes through the context does not ([#330](https://github.com/nanyx-lang/nanyx/issues/330),
[#331](https://github.com/nanyx-lang/nanyx/issues/331)).

## Where the docs and the compiler disagree

| The docs say | The compiler does |
|---|---|
| Operators on built-in types are attached definitions: `1 + 2` resolves to ``int.`+` `` ([operators.md:45](../../docs/src/content/tour/operators.md)). | Each built-in operator is typed directly in `Infer.fs`. The prelude attaches no operators. |
| A user type gets an operator by attaching one: ``def Vector.`+` = ...`` (operators.md:47-51, contexts.md:169-173). | A definition named in backticks does not parse, attached or not, and neither does an operator-named context member ([#527](https://github.com/nanyx-lang/nanyx/issues/527)). So the docs' `Eq(a)` and `Sum(a)`, whose members are `` `==` `` and `` `+` ``, cannot be declared. |
| `Eq(a)` is a "common context" with a `` `==` `` member, used as `[Eq(a)]` (contexts.md:106-118). | No `Eq` exists, and `==` never requires one. |
| `sort: [Ord(a)] list(a) -> list(a)` ([memory.md:14](../../docs/src/content/advanced/memory.md)). | No `Ord`, no `sort`. |
| `List.sortBy: (list(a), (a -> b))` ([collections.md:251](../../docs/src/content/stdlib/collections.md)). | `(a -> int)` keys only. |
| `use` only changes the instance generic code sees, "never what `1 + 2` means" (operators.md:53). | Consistent today, but [printing-values.md:45-52](../../docs/src/content/work-in-progress/printing-values.md) has `use ToString(int)` changing a concrete `"{total}"`. The two pages disagree with each other. |

Two backend bugs turned up while checking this: [#526](https://github.com/nanyx-lang/nanyx/issues/526) (above) and
[#528](https://github.com/nanyx-lang/nanyx/issues/528), where a lambda inside a generic function cannot call a member
of the context the function requires.

# Proposal

## Well-known contexts

A **well-known context** is a context declared in the prelude that the compiler knows by name. It differs from a user
context in exactly two ways:

1. **Syntax is sugar for its members.** `a == b` is `Eq(T).==(a, b)` for the type `T` of `a` and `b`; `a < b` is read
   through `Ord(T)`; an interpolation hole through `ToString(T)`.
2. **The compiler can build instances from structure.** `Eq(list(int))` is built from `Eq(int)`; `Eq((x: int, y:
   string))` from `Eq(int)` and `Eq(string)`. User contexts only get synthesis by name.

Everything else is the ordinary context machinery: a generic function requires them in its signature, `use` loads an
instance, and the resolution order above is unchanged, with structural derivation added as a fourth, last step.

The proposed set is small and closed:

| Context | Members | Sugar | Derived from structure |
|---|---|---|---|
| `Eq(a)` | `` `==`: (a, a) -> bool `` | `==`, `!=` | numbers, bool, unit, string, tuples, records, lists, tags and unions |
| `Ord(a)` | `compare: (a, a) -> #less \| #equal \| #greater` | `<`, `<=`, `>`, `>=` | numbers, bool, string, tuples, lists (see [Q3](#open-questions)) |
| `Hash(a)` | `hash: a -> int` | none | the same types as `Eq` |
| `ToString(a)` | `toString: a -> string` | interpolation holes | the built-in types only, as [printing values](../../docs/src/content/work-in-progress/printing-values.md) proposes |

Libraries cannot add to this list. A library that wants structure-driven behaviour for its own context, such as JSON
encoding, uses [type arguments](../../docs/src/content/work-in-progress/type-arguments-and-reflection.md) when they
exist. Numeric operators are discussed [separately below](#numeric-operators).

## Equality

**Recommendation.**

- **Structural by default.** Two values are equal when they have the same shape and their parts are equal: records
  field by field, tuples and lists element by element, tags by name and then payload. Strings compare by content.
  There is no reference equality: values are immutable, so nothing can observe identity.
- **Functions have no equality.** There is no `Eq` of a function type, so `f == g` is a type error, and so is
  comparing any record, list or tag that holds a function. The error names where the function is (see
  [Diagnostics](#diagnostics)).
- **`!=` is not a member.** It is always `not (a == b)`, so a type cannot make the two disagree.
- **Floats follow IEEE 754.** `NaN == NaN` is `false` and `0.0 == -0.0` is `true`, as every other language does. `Eq(float)`
  is therefore not quite an equivalence; the docs say so.
- **Generic code says it compares.** `==` on a type variable `a` requires `Eq(a)`. A function that writes its type must
  write `[Eq(a)]`; one that does not has it inferred. Generic code is then checked once, where it is written, not at
  each type it is used with. `List.distinct` becomes `[Eq(a)] list(a) -> list(a)`.
- **A type overrides equality by attaching `==`.** ``def $Email.`==` = { a, b -> lower(a) == lower(b) }`` is found by
  the existing synthesis step, ahead of structural derivation. Only nominal and protected types may do this. A
  structural type built from one uses it: a
  `list($Email)` compares its elements with `` $Email.`==` ``. This needs [#527](https://github.com/nanyx-lang/nanyx/issues/527).
- **A block overrides it with `use`.** `use Eq(string) = (`` `==` `` = caseInsensitive)` changes what a plain
  `a == b` in that block means, and what generic code called from it sees (`xs \List.contains(name)`).
- **Nominal and protected types** get the structural equality of their underlying type unless they attach `==`.
  Comparing two `$UserId`s does not construct one, so it cannot break the type's invariants.

**Alternatives.**

- *Implicit equality on every non-function type* (OCaml, F#, Elm): `==` stays `a, a -> bool`, generic code needs no
  annotation, and the compiler rejects functions when it instantiates. This is today's behaviour plus a
  check, and it is lighter to write. But errors appear at the instantiation, in library code the user did not write,
  which the [reflection page](../../docs/src/content/work-in-progress/type-arguments-and-reflection.md) rules out for
  generic code ("checked once"). F# hides the same rule behind an inferred `equality` constraint, which is the
  recommendation with the constraint left unwritten.
- *`==` is a fixed structural intrinsic, and `Eq` is a separate, optional context* for custom equivalences. Simpler to
  implement, but then `==` and `Eq(a)` can disagree, and a type such as `$Email` has no way to say what equal means.

## How instances are found

The lookup at each use of `==`, or call that requires `Eq(T)`, gains a last step:

1. an instance loaded with `use` for `T`, nearest first;
2. the instance the enclosing function's signature declares;
3. an instance synthesized from definitions attached to `T` by name (`` T.`==` ``);
4. **for a well-known context only**, an instance derived from `T`'s structure, each part found by this same lookup.

Because step 4 looks up each part afresh, overrides compose: a record containing an `$Email` compares it with
`` $Email.`==` ``, and inside `use Eq(string) = ...` a `list(string)` compares its elements with the loaded instance.

In generic code a requirement on a structured type **reduces** to requirements on its variables: `==` on a
`list(a)` needs `Eq(a)`, and on `(name: string, value: a)` needs `Eq(a)`. So a signature writes `[Eq(a)]`, not
`[Eq(list(a))]`. The diagnostic for a missing requirement names the reduced one.

With monomorphisation every instance is known when code is generated, so the backend generates one equality function
per concrete type (fixing [#424](https://github.com/nanyx-lang/nanyx/issues/424) and
[#526](https://github.com/nanyx-lang/nanyx/issues/526)) and no instance is passed at run time.

## Open tag unions

**Recommendation.** `a == b` joins its operands instead of unifying them: each is assignable to a fresh type `t`, and
the comparison requires `Eq(t)`. So `{ o -> o == #none }` infers `o: #none | r`, and `isNone(#some(3))` checks,
comparing at `#some(int) | #none`. This is the same join `if` and `match` branches need
([#433](https://github.com/nanyx-lang/nanyx/issues/433)).

A row variable needs an instance too: `Eq(#none | r)` reduces to `Eq(r)`, meaning "every tag the rest holds has
equality". A function generic over a union's rest writes `[Eq(r)]`. Most code never sees this, because it is
inferred.

Comparing two closed types with no tag in common, `#red == #some(1)`, is always false; the compiler warns.

## Ordering

`Ord(a)` has one member, `compare`, returning `#less | #equal | #greater`. The four operators are derived from it, so
they cannot disagree with each other. `<` on a type variable requires `[Ord(a)]`.

Derived orders: numbers numerically, `false < true`, strings by their bytes (the order of their Unicode code
points), tuples and lists lexicographically. For floats the operators follow IEEE 754 (every comparison with `NaN`
is false) while `Float.compare` is a total order (`NaN` after everything), so sorting a list of floats is well
defined; this is what Java and Rust do.

**Records and tags get no derived order.** A record's fields have no order in its type (`(x: int, y: int)` is
`(y: int, x: int)`), and neither do a union's tags, so any derived order would be alphabetical by accident. A type
that wants one is made nominal and attaches `compare`; otherwise use `sortBy` with a key. See [Q3](#open-questions).

`Ord` does not require `Eq`; Nanyx contexts have no hierarchy. A type that attaches both should keep them
consistent, and the docs say so.

This closes [#476](https://github.com/nanyx-lang/nanyx/issues/476) and unblocks `List.sort`, `min`, `max`
([#475](https://github.com/nanyx-lang/nanyx/issues/475)) and a `sortBy` with `(a -> b)` keys.

## Hashing

`Hash(a)` has `hash: a -> int`, derived for every type `Eq` is derived for. A map or set keyed by `k` requires
`[Eq(k) & Hash(k)]`, so records and tags can be keys without an order.

A type that attaches its own `==` gets **no derived `Hash`**: the derived hash would disagree with the custom
equality. Asking for `Hash($Email)` is then an error that says to attach `$Email.hash` too.

Whether maps are hash-based (needing `Hash`) or ordered trees (needing `Ord`) is a standard library decision; this
design makes hashing the natural choice, since more types have it.

## Printing

Unchanged from [printing values](../../docs/src/content/work-in-progress/printing-values.md): `ToString(a)` is a
well-known context for interpolation holes, synthesized from an attached `toString`. Whether compound types get a
derived `ToString` stays that page's open question. As with `==`, a `use ToString(T)` changes concrete holes in its
block, which is what that page already proposes.

## Numeric operators

`+`, `-`, `*`, `/`, `%` and `**` stay as they are for now: typed directly, on numbers only, with `++` on strings and
lists. Making them well-known contexts (the docs' `Sum(a)` and `Num(a)`) raises separate questions: one context per
operator or one `Num`, how literals like `0` are typed, and how units and dimensions fit. That deserves its own
document. The principle this design sets, that a built-in operator is sugar for a well-known context's member, is
meant to hold for them too.

## Diagnostics

Each is reported by the type checker, with a span; none reaches the backend.

| Situation | Message (draft) |
|---|---|
| `==` on functions | `'(int -> int)' cannot be compared with ==: functions have no equality` |
| `==` on a type holding a function | `'Button' cannot be compared with ==: its field 'onClick' is a function, and functions have no equality` |
| Annotated generic function compares without `[Eq(a)]` | `'same' compares values of type 'a' with ==, so its type must require Eq(a): write [Eq(a)] a, a -> bool` |
| `<` on a type with no order | `'Point' cannot be ordered with <: records have no order. Attach 'Point.compare', or compare a key with sortBy` |
| Attached `==` with the wrong type | the existing `MissingContextMembers` message |
| `!=` attached to a type | `'!=' cannot be defined: it is always 'not (a == b)'. Attach '==' instead` |
| `==`, `compare` or `hash` attached to a structural type | `'Point.==' cannot be defined: Point is a structural type, so it always compares by its fields. Make it nominal ($Point) to define its own equality` |
| `Hash(T)` wanted where `T` has its own `==` | `'Map' needs Hash($Email), and $Email defines its own ==, so its hash must agree with it: attach '$Email.hash'` |
| Closed types with nothing in common | warning: `'#red' and '#some(int)' have no tag in common, so this is always false` |

The language server's code action for the third adds the requirement to the signature.

## What changes for existing code

- Generic functions that use `==` without a signature are unaffected: the requirement is inferred.
- Generic functions with a signature that use `==` on a type variable need `[Eq(a)]` added. In the prelude that is
  `List.distinct` and any future `contains`/`indexOf` ([#474](https://github.com/nanyx-lang/nanyx/issues/474)).
- `==` on records, tuples and lists starts working; on functions it moves from a backend error to a type error.
- The docs' contexts page keeps its `Eq(a)` example, but as the prelude's context rather than one the user declares.

## Order of work

1. Parse backtick-named definitions and context members ([#527](https://github.com/nanyx-lang/nanyx/issues/527)).
2. `Eq` in the prelude; `==` and `!=` read through it; structural derivation; per-type equality functions in the
   backend; the join for open unions. Closes #424 and #526.
3. `Ord`, `compare` and the four operators; `List.sort`, `min`, `max`, generic `sortBy`. Closes #476.
4. `Hash`, then maps and sets.
5. `ToString` per printing values (#330).

# Open questions

## Decided

1. **Generic code says it compares.** `==` on a type variable requires `[Eq(a)]`, inferred when the type is not
   written. (Rejected: every non-function type has equality implicitly, checked at instantiation.)
2. **`use` changes a concrete `a == b` too.** `==` is always a lookup of `Eq(T)`, so a `use Eq(string)` changes plain
   `==` in its block as well as generic code called from it. This reverses operators.md:53, which is updated when
   the work lands, and agrees with printing-values.md. (Rejected: `use` reaches only generic code.)
3. **Derived orders** are for numbers, bool, string, tuples and lists only; records and tags attach `compare` or
   use `sortBy`. (Rejected: tags by name, records by field name.)
4. **First round:** `Eq`, then `Ord`, then `Hash` with maps, each its own piece of work. Numeric operators get
   their own design.

5. **The member is named `` `==` ``**, as the docs have it, so this waits on
   [#527](https://github.com/nanyx-lang/nanyx/issues/527). (Rejected: `equals`.)
6. **Only nominal and protected types may attach `==`, `compare` or `hash`.** A structural type, including an alias
   such as `type Point = (x: int, y: int)`, always compares structurally, so `(x = 1, y = 2)` compares the same
   whether or not its alias is known. Attaching one of these to a structural type is an error. (Rejected: any named
   type.)
7. **Mutable `MArray` and `MRef` have no `Eq`**; convert to a list first. (Rejected: identity, contents.)

## Still open

- Should a protected type's module be able to withhold equality from other modules? [Recommended: no, not until
  someone needs it.]
