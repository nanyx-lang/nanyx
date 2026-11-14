---
title: "Equality and well-known contexts"
description: "What equality is, and which contexts the compiler knows about"
order: 8
---

> **Status: agreed, partly implemented.** `Eq(a)`, `Ord(a)` and `Hash(a)` are in the prelude: `==` and `!=` require
> `Eq`, `<`, `<=`, `>` and `>=` require `Ord`, functions have no equality or hash, and records and tags have no order.
> [What is implemented](#what-is-implemented) says what is done and what is left of
> [#532](https://github.com/nanyx-lang/nanyx/issues/532), [#476](https://github.com/nanyx-lang/nanyx/issues/476) and
> [#533](https://github.com/nanyx-lang/nanyx/issues/533).
> The decisions are listed under [Open questions](#open-questions); one small question remains. The code references
> under [What existed before](#what-existed-before) are at commit `b84acc0`.

# What is implemented

`Nanyx.Compiler/WellKnownContexts.fs` holds what the three contexts share: which types the compiler has an instance of
its own for, how a call that requires one finds it, and how an operator on a type with a variable in it is checked
once the type is known. `EqualityOperators.fs` and `OrderingOperators.fs` hold what is particular to each context's
operators. The prelude declares `Eq(a)` and `__equals`, `Ord(a)`, `__compare` and the four functions the ordering
operators call, and `Hash(a)` and `__hash`.

## Equality

- **`Eq(a)` is a prelude context**, `` context Eq(a) = (`==`: (a, a) -> bool) ``.
- **Functions have no equality.** `==` or `!=` on a function, or on a record, list, tag or nominal type that holds
  one, is `NYX0205` at the comparison, naming the part that is a function. So is passing such a type to a function
  that requires `Eq`, unless the type attaches its own `==`, which is found first.
- **Generic code says it compares.** `==` on a type variable of a def that writes its type needs `[Eq(a)]` in that
  type. A def that does not write its type has the requirement inferred, and its `==` then calls the member of the
  instance it is passed. A requirement on a type built from a variable reduces to the variable: `==` on a `list(a)`
  needs `Eq(a)`. `list.distinct` is `[Eq(a)] list(a) -> list(a)`.
- **`!=` is not a member.** It is `not (a == b)`, and calls whatever `==` calls.
- **Instances at a call.** A call that requires `Eq(T)` finds: an instance in scope (a `use`, or the enclosing
  signature); then the `==` attached to `T`; then the compiler's own, whose member is the prelude's `__equals`, a
  generic function that compares by structure at the type it is specialised for.
- **Only nominal types attach `==`** (decision 6). `` def Point.`==` `` on a structural type, or on a
  built-in type such as `string`, is `NYX0219`, and so is attaching `!=` to anything. `AttachedOperators.fs` holds
  the rule, with a list of the members it covers: `==`, `compare` and `hash`.
- **Local functions are checked at each call.** A local function requires no contexts: the backend compiles it again
  at each type it is used at, a closure for each (`FunctionLowering.GenericLocal` in `CoreIrLower.fs`), so its `==`
  compares and frees a string or a list as that type does. A lambda that captures the local captures a closure for
  each type the lambda uses it at (`FunctionLowering.CapturedInstances`); where the lambda is itself generic, the
  closure at each of its types is made where the lambda's is. A use the backend cannot type reads the closure compiled
  with its type variables as unit, which compares them as words. A call to a local `def same = { x, y -> x == y }` still needs an `Eq` of the type
  it compares at the call, so `same(f, f)` with functions is `NYX0205` at the call, and a def that calls it with one
  of its own type variables requires `Eq` of it (`WellKnownContexts.checkLocalCall`, which `Ord` shares). The local
  function named as a value is checked the same way, at the type it is passed as, so `check(same)` where `check` calls
  it with functions is `NYX0205` at `same` (`ContextInstances.checkComparisonsAt`).
- **The operands are joined when one is a tag.** `{ o -> o == #none }` is `#none | r -> bool`, so `isNone(#some(3))`
  checks, as the join for `if` and `match` branches does (`BranchJoin.joinBranchTypes`, chosen by
  `EqualityOperators.joinsOperands`). Other operands are still made equal, so a comparison of two `Point`s still
  names `Point`. The official linter's `noTagInCommon` rule warns about `#red == #some(1)`: closed types with no tag
  in common.
- **A union's rest is checked at each call, not passed.** Comparing an `#none | r` compares whatever tags `r` holds,
  so `isNone(#some(f))` with a function `f` is `NYX0205` at the call, naming the payload that is a function. The
  function is passed nothing for `Eq(r)`: its comparison is the compiler's own at each type it is specialised at, so
  `isNone` stays `#none | r -> bool`, and `classify(#ok)` makes no closure. Inference records, for each function, the
  rests of its type that its comparisons, or its calls to such functions, compare
  (`ContextInference.checkRestsAtCalls`, kept in `InferState.CheckedRests` and carried by imports), and a call checks
  `Eq` of what each is there as a call to a local function does (`WellKnownContexts.checkLocalCall`). A function that
  writes its type with an open union, `#none | r -> bool`, needs no `[Eq(r)]`. As for local functions, the function
  named as a value is checked too, at the type it is passed as.
- **A concrete `a == b` is a lookup too** (decision 2). Where `==` is bound to a definition that takes the operands,
  as it is after `use ignoringCase` (an `Eq(string)`), `a == b` calls it, on a built-in type as on any other; else, on
  a type with a `==` attached, it calls that, as any operator on a type that attaches it does (`OperatorCalls.fs`,
  [#527](https://github.com/nanyx-lang/nanyx/issues/527)). Inside the attached `==` itself, `==` on its own type is
  the built-in comparison.

- **A list is compared through its elements' `Eq`.** `==` and `!=` on two lists whose elements have an `Eq` other
  than the compiler's own (one in scope, or an attached `==`) call the prelude's `__listEquals` with it
  (`WellKnownContexts.throughElements`): `[Email("a")] == [Email("b")]` uses `` Email.`==` ``, and so does `==` on a
  `list(a)` in a function that requires `[Eq(a)]` with the `Eq(a)` it is passed. `Ord` does the same for `<`.

- **Structural derivation that looks up each part** ([#631](https://github.com/nanyx-lang/nanyx/issues/631)). A tag
  with a payload, a tuple, a record or a list of them with a part that has an `==` of its own, one attached to the
  part's type or the `Eq` a function requires of a type variable, is compared by a definition the compiler derives
  (`Nanyx.Compiler/StructuralInstances.fs`), which compares each part with `==`. So a record that holds an `Email`
  compares it with `` Email.`==` ``, and inside a function that requires `[Eq(a)]`, `==` on a `#some(a)` compares
  through the instance, also where the operands' type is only known later. A structure with no such part is still
  compared by the compiler's own. `ToString` is derived the same way (see
  [ToString(a)](../advanced/contexts#tostringa)).

- **A union with a rest is compared through the tags it names**
  ([#830](https://github.com/nanyx-lang/nanyx/issues/830)). `{ o -> o == #some(Email("a")) }` compares at
  `#some(Email) | r`, where `r` is the function's own. Its `==` is derived as it would be for the closed union, with
  the rest kept in the definition's type, and two values of a tag the rest holds, or of different tags, are compared
  by the compiler's own `__equals` (`OpenTagShape` in `StructuralInstances.fs`). The definition requires no `Eq(r)`:
  the rest is checked at each call, as above. Only a comparison derives one. A call that requires `Eq` of an open
  union, as `same(v, w)` with `v: #x(a) | r` does, is still passed the compiler's own.

- **A derived `==` is built from the instances where it is used**
  ([#630](https://github.com/nanyx-lang/nanyx/issues/630)). A part of a concrete type with an `Eq` loaded by `use` in
  the block is compared through it: the derived definition is written for the structure with that part as a type
  variable, and passed the instance in scope (`StructuralInstances.abstractParts`). A call that requires `Eq` of a
  structure with a type variable in it, `same([x], [y])` inside a function that requires `[Eq(a)]`, is passed the
  derived definition as a closure over the `Eq(a)` the function was given (`InstanceDerived`), so a user-defined
  `Eq(a)` that differs from structural equality is the one that compares the elements.

## Ordering

- **`Ord(a)` is a prelude context**, `context Ord(a) = (compare: (a, a) -> #less | #equal | #greater)`, and the
  four operators read it, so they cannot disagree. `Ord` does not require `Eq`.
- **The compiler's own orders** numbers, bools (`false` first), strings (by bytes), and tuples and lists of them,
  lexicographically. On floats the operators follow IEEE 754, while `__compare`, the member of the compiler's own
  instance, and `float.compare` are a total order with NaN last, so `list.sort` of floats is well defined. The
  backend writes one ordering function per type (`CoreIrOrdering.fs`), reached through the intrinsic
  `__nanyx_compare`. `int`, `float`, `bool` and `string` each attach a `compare` that is the compiler's own, as a
  function to call; a built-in type's `Ord` is always the compiler's own, so nothing reads an attached `compare` for one.
- **Records, tags and functions have no order.** `<` on one, or on a tuple or list that holds one, is `NYX0205` at
  the operator, naming the part; so is passing one to a function that requires `Ord`. The message for a record or
  tag says to attach `compare` to a nominal type, or to sort by a key.
- **Generic code says it orders**, as for `Eq`: `<` on a type variable needs `[Ord(a)]` in a written type, and has
  it inferred in a def that does not write one. `<` on a `list(a)` needs `Ord(a)`.
- **Instances.** An operator, like a call, finds an instance in scope (a `use`, decision 2, or the enclosing
  signature, which also covers a concrete `"a" < "b"`), then the `compare` attached to a type that is not a built-in
  one, then the compiler's own. Any instance but the compiler's own is called: `x < y` calls the prelude's `__less`
  (and `<=`, `>`, `>=` call `__lessOrEqual`, `__greater` and `__greaterOrEqual`), which is passed the instance.
- **Only nominal types attach `compare`** (decision 6): attaching it to a structural type is `NYX0219`,
  as for `==`. A built-in type may attach one, as the prelude does, but it is not read.
- **Lists.** `list.sort` and `list.sortBy` (now with keys of any ordered type, `[Ord(b)]`), and `list.min` and
  `list.max`, which give `#emptyList` for an empty list, as `list.head` does.
- **Local functions are checked at each call**, as for `Eq`: a call to a local `def lt = { x, y -> x < y }` with
  records is `NYX0205` at the call, and a def that calls it with one of its own type variables requires `Ord` of it.
- **A list is ordered through its elements' `Ord`**, as for `Eq`: `[v(1), v(2)] < [v(3)]` on `list(Version)` calls
  the prelude's `__listLess` (and `__listLessOrEqual`, `__listGreater`, `__listGreaterOrEqual`) with
  `Version.compare`, lexicographically with a prefix first, and so does `<` on a `list(a)` in a function that
  requires `[Ord(a)]`.

Not done yet, as for `Eq`:

- **Structural derivation that looks up each part, beyond one list.** A tuple holding a `Version`, or a list of lists
  of them, is still an error, and `<` on a `list(a)` whose type is only known later orders by structure
  ([#631](https://github.com/nanyx-lang/nanyx/issues/631)).

## Hashing

- **`Hash(a)` is a prelude context**, `context Hash(a) = (hash: a -> int)`, with no operators. It is the third
  well-known context (`WellKnownContexts.hashing`), so a call that requires it finds an instance as one that requires
  `Eq` does: one in scope, then the `hash` attached to a type that is not a built-in one, then the compiler's own.
- **The compiler's own hashes by structure**, for every type it has its own `Eq` for, so values `==` finds equal by
  their structure have the same hash: ints, bools and unit are their own hash, a float is hashed by its bits with
  `-0.0` as `0.0`, a string by its bytes (FNV-1a), and records, tuples, lists and tags part by part. Its member is
  `__hash`, which calls the intrinsic `__nanyx_hash`; the backend writes one hash function per type
  (`CoreIrHashing.fs`). `int`, `float`, `bool` and `string` each attach a `hash` that is the compiler's own, to call
  when writing a type's own `hash`.
- **Functions have no hash**: passing a function, or anything that holds one, to a function that requires `Hash` is
  `NYX0205` at the call, naming the part.
- **A type that attaches its own `==` must attach `hash` too.** `WellKnownContext.AgreesWith` says that `hash` must
  agree with `==`, and `WellKnownContexts.disagreeing` finds a type, in the one asked for, that attaches `==` and no
  `hash`: `Hash(Email)`, or `Hash(list(Email))`, is then `NYX0205`, which says to attach `Email.hash`.
- **Only nominal types attach `hash`** (decision 6), as for `==` and `compare`.
- **Generic code says it hashes**, as for `Eq`: a def that passes a value of its type variable to a function that
  requires `Hash` needs `[Hash(a)]`, written or inferred.
- **A structure is hashed through its parts' own `Hash`** ([#641](https://github.com/nanyx-lang/nanyx/issues/641)), as
  `==` compares through their `Eq`: where a part has a `hash` of its own, attached (`list(Email)`) or the `Hash(a)`
  a function is passed, `StructuralInstances` derives a `hash` for the structure, which hashes each part with
  `__hashOf` and folds the hashes with `__hashCombine`, after the tag's name for a tag, and a list's with
  `__listHashBy`. A built-in type's attached `hash` is the compiler's own, so a structure of only those keeps it.

Not done yet:

- **Maps and sets keyed by `Hash`** are done in the prelude, but do not use the hash yet; see [Maps and sets](./maps-and-sets).

# What existed before

This design was written when Nanyx mentioned `Eq(a)`, `Ord(a)`, `Sum(a)` and `ToString(a)` in several places, but had
not decided what equality *is*, or which contexts the compiler knows about. This section records what existed then;
the rest proposes one design for equality and applies the same pattern to ordering, hashing and printing.

# Contexts: designed and implemented

Contexts are implicit parameters ([docs: Contexts](../advanced/contexts)). Implemented:

- **Declaring** a context as a record type: `context Show(a) = (show: a -> string)`.
- **Requiring** one in a signature: `def describe: [Show(a)] a -> string`. A function that does not write its type
  has its requirements inferred.
- **Loading** an instance: `use <expr>`, where the value's type is the context at a type, as
  `def showPoint: Show(Point) = (show = ...)` then `use showPoint` (`useInstance` in `ContextInstances.fs`). The older
  `use Show(Point) = (...)` is a parse error that says so. The call form, `use Show(Point)(show = ...)`, writes the
  instance out where it is loaded: the parser reads it as a block that defines it under a made-up name, typed
  `Show(Point)`, and gives it back (`UseInstanceSyntax`), so it is checked and loaded as the `def` is.
- **Resolving** an instance at each call (`resolveCallInstances` in `ContextInstances.fs`), first match wins:
  1. an instance for that exact type loaded with `use` around the call, nearest first (`instanceInScope`);
  2. the instance the enclosing function's own signature declares (a generic function passes its `[Show(a)]` on);
  3. an instance **synthesized** from the type's attached definitions, matched by member name: `Point.show` makes
     a `Show(Point)` (`synthesizeInstance`, [#328](https://github.com/nanyx-lang/nanyx/issues/328)). A missing or
     ill-typed definition is reported as `MissingContextMembers`, naming it.

Synthesis is **by name only**. Nothing builds an instance for `list(a)` from the instance for `a`, and no instance is
built from a type's structure (see [structural context synthesis](./type-members-and-reflection#structural-context-synthesis)).

# Equality: implemented as an untyped intrinsic

- **Type checking.** `==` and `!=` unify their two operands and return `bool`; nothing else is checked
  (`Infer.fs:1381-1382`). In effect `==` has the type `a, a -> bool` for *every* `a`, functions included, and adds
  no requirement to a generic function. `list.distinct: list(a) -> list(a)` relies on this
  (`stdlib/nanyx/prelude-lists.nyx`).
- **Lowering.** After monomorphisation the backend picks an implementation from the operand's concrete type
  (`CoreIrLower.fs:479-504`): `i64.eq` for int; `i32.eq` for bool and unit; `f64.eq` for float; a byte comparison for strings;
  `__nanyx_tag_equals` for tags. Anything else, records, tuples, lists and functions, type-checks and then fails in
  the backend with `NYX0301 UnsupportedByBackend` ([#424](https://github.com/nanyx-lang/nanyx/issues/424)). A generic
  `def same: a, a -> bool = { x, y -> x == y }` is accepted, and fails only when some caller uses it at a record.
- **Tags compare their payload word.** `__nanyx_tag_equals` (`WasmRuntime.fs:667-698`) compares the payload with
  `i32.eq`, so a string or other boxed payload is compared by address: `#some("a" ++ "a") == #some("aa")` is
  `false` ([#526](https://github.com/nanyx-lang/nanyx/issues/526)).
- **Open unions.** Because `==` *unifies* its operands, `def isNone = { o -> o == #none }` infers `o: #none`, and
  `isNone(#some(3))` is a type error.

# Ordering: numbers only

`<`, `<=`, `>` and `>=` constrain both operands to a numeric type (`Infer.fs:1376-1380`). Strings, tuples and type
variables cannot be ordered ([#476](https://github.com/nanyx-lang/nanyx/issues/476)), which is why `list.sortBy`
takes `(a -> int)` keys (`prelude-lists.nyx`) and there is no `list.sort`
([#475](https://github.com/nanyx-lang/nanyx/issues/475), [#191](https://github.com/nanyx-lang/nanyx/issues/191)).
*Since fixed:* `Ord` exists, and `list.sort` and `list.sortBy` take any ordered type.

# Hashing, maps and sets: nothing

There was no `Hash` context, no map or set type, and no map literal (#191, `groupBy`). Since then `map(k, v)`, map
literals and `list.groupBy` exist ([#477](https://github.com/nanyx-lang/nanyx/issues/477)), and the `Hash` context
([#533](https://github.com/nanyx-lang/nanyx/issues/533)): a map's keys, and `groupBy`'s, need `Eq` and `Hash`. See
[Maps and sets](./maps-and-sets).

# Printing: designed, partly implemented

A printing design proposed a `ToString(a)` context behind interpolation holes, synthesized from an attached
`toString`, with `dbg` separate and printing anything. The attached `toString` on the built-in types existed
([#329](https://github.com/nanyx-lang/nanyx/issues/329)); checking holes through the context did not
([#330](https://github.com/nanyx-lang/nanyx/issues/330), [#331](https://github.com/nanyx-lang/nanyx/issues/331)).
*Since done:* [ToString(a)](../advanced/contexts#tostringa) describes it.

# Where the docs and the compiler disagree

| The docs say | The compiler does |
|---|---|
| Operators on built-in types are attached definitions: `1 + 2` resolves to ``int.`+` `` ([operators.md:45](../tour/operators)). | Each built-in operator is typed directly in `Infer.fs`. The prelude attaches no operators. An operator on a value of a type variable calls the member of that name of a context the function requires. |
| A user type gets an operator by attaching one: ``def Vector.`+` = ...`` (operators.md:47-51, contexts.md:169-173). | A definition named in backticks did not parse, attached or not, and neither did an operator-named context member ([#527](https://github.com/nanyx-lang/nanyx/issues/527)). *Since fixed:* they parse, and `a + b` on a type that attaches `` `+` `` calls it. |
| `Eq(a)` is a "common context" with a `` `==` `` member, used as `[Eq(a)]` (contexts.md:106-118). | No `Eq` exists, and `==` never requires one. |
| `sort: [Ord(a)] list(a) -> list(a)` ([memory.md:14](../advanced/memory)). | No `Ord`, no `sort`. *Since fixed:* both exist. |
| `list.sortBy: (list(a), (a -> b))` (the old collections page; now [lists](../stdlib/prelude-lists#listsortby)). | `(a -> int)` keys only. *Since fixed:* `[Ord(b)]`, keys of any ordered type. |
| `use` only changes the instance generic code sees, "never what `1 + 2` means" (operators.md:53). | Consistent then, but the printing design had a `ToString(int)` loaded with `use` changing a concrete `"{total}"`. The two designs disagreed. *Since settled:* a `use` changes concrete holes, as [ToString(a)](../advanced/contexts#tostringa) shows. |

Two backend bugs turned up while checking this: [#526](https://github.com/nanyx-lang/nanyx/issues/526) (above) and
[#528](https://github.com/nanyx-lang/nanyx/issues/528), where a lambda inside a generic function cannot call a member
of the context the function requires.

# Proposal

# Well-known contexts

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
| `ToString(a)` | `toString: a -> string` | interpolation holes | tags, tuples, records and lists, as [ToString(a)](../advanced/contexts#tostringa) describes |

Not every context in the prelude is well-known. `FromString(a, e)`, which an attached `fromString` gives a type as
`toString` gives it a `ToString`, is an ordinary prelude context: no syntax reads through it and it is never derived
from structure, so it is found only by `use`, a signature, or synthesis by name.

Libraries cannot add to this list. A library that wants structure-driven behaviour for its own context, such as JSON
encoding, uses [type arguments](./type-members-and-reflection) when they
exist. Numeric operators are discussed [separately below](#numeric-operators).

# Equality

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
  each type it is used with. `list.distinct` becomes `[Eq(a)] list(a) -> list(a)`.
- **A type overrides equality by attaching `==`.** ``def Email.`==` = { a, b -> lower(a) == lower(b) }`` is found by
  the existing synthesis step, ahead of structural derivation. Only nominal types may do this. A
  structural type built from one uses it: a
  `list(Email)` compares its elements with `` Email.`==` ``. This needs [#527](https://github.com/nanyx-lang/nanyx/issues/527).
- **A block overrides it with `use`.** Loading `def ignoringCase: Eq(string) = (`` `==` `` = caseInsensitive)` with
  `use ignoringCase` changes what a plain
  `a == b` in that block means, and what generic code called from it sees (`xs \list.contains(name)`).
- **Nominal types** get the structural equality of their underlying type unless they attach `==`.
  Comparing two `UserId`s does not construct one, so it cannot break the type's invariants.

**Alternatives.**

- *Implicit equality on every non-function type* (OCaml, F#, Elm): `==` stays `a, a -> bool`, generic code needs no
  annotation, and the compiler rejects functions when it instantiates. This is today's behaviour plus a
  check, and it is lighter to write. But errors appear at the instantiation, in library code the user did not write,
  which the [reflection page](./type-members-and-reflection) rules out for
  generic code ("checked once"). F# hides the same rule behind an inferred `equality` constraint, which is the
  recommendation with the constraint left unwritten.
- *`==` is a fixed structural intrinsic, and `Eq` is a separate, optional context* for custom equivalences. Simpler to
  implement, but then `==` and `Eq(a)` can disagree, and a type such as `Email` has no way to say what equal means.

# How instances are found

The lookup at each use of `==`, or call that requires `Eq(T)`, gains a last step:

1. an instance loaded with `use` for `T`, nearest first;
2. the instance the enclosing function's signature declares;
3. an instance synthesized from definitions attached to `T` by name (`` T.`==` ``);
4. **for a well-known context only**, an instance derived from `T`'s structure, each part found by this same lookup.

Because step 4 looks up each part afresh, overrides compose: a record containing an `Email` compares it with
`` Email.`==` ``, and after `use ignoringCase` a `list(string)` compares its elements with the loaded instance.

In generic code a requirement on a structured type **reduces** to requirements on its variables: `==` on a
`list(a)` needs `Eq(a)`, and on `(name: string, value: a)` needs `Eq(a)`. So a signature writes `[Eq(a)]`, not
`[Eq(list(a))]`. The diagnostic for a missing requirement names the reduced one.

With monomorphisation every instance is known when code is generated, so the backend generates one equality function
per concrete type (fixing [#424](https://github.com/nanyx-lang/nanyx/issues/424) and
[#526](https://github.com/nanyx-lang/nanyx/issues/526)) and no instance is passed at run time.

# Open tag unions

**Recommendation.** `a == b` joins its operands instead of unifying them: each is assignable to a fresh type `t`, and
the comparison requires `Eq(t)`. So `{ o -> o == #none }` infers `o: #none | r`, and `isNone(#some(3))` checks,
comparing at `#some(int) | #none`. This is the same join `if` and `match` branches need
([#433](https://github.com/nanyx-lang/nanyx/issues/433)).

A row variable needs an instance too: `Eq(#none | r)` reduces to `Eq(r)`, meaning "every tag the rest holds has
equality". As built ([#623](https://github.com/nanyx-lang/nanyx/issues/623)), the requirement is checked at each
call but never passed, so it is not part of the function's type and nobody writes `[Eq(r)]`: the comparison is the
compiler's own at each type the function is specialised at, as a local function's is. Passing an instance for it
would make every call to a function that compares with a tag build a closure.

Comparing two closed types with no tag in common, `#red == #some(1)`, is always false; the compiler warns.

# Ordering

`Ord(a)` has one member, `compare`, returning `#less | #equal | #greater`. The four operators are derived from it, so
they cannot disagree with each other. `<` on a type variable requires `[Ord(a)]`.

Derived orders: numbers numerically, `false < true`, strings by their bytes (the order of their Unicode code
points), tuples and lists lexicographically. For floats the operators follow IEEE 754 (every comparison with `NaN`
is false) while `float.compare` is a total order (`NaN` after everything), so sorting a list of floats is well
defined; this is what Java and Rust do.

**Records and tags get no derived order.** A record's fields have no order in its type (`(x: int, y: int)` is
`(y: int, x: int)`), and neither do a union's tags, so any derived order would be alphabetical by accident. A type
that wants one is made nominal and attaches `compare`; otherwise use `sortBy` with a key. See [Q3](#open-questions).

`Ord` does not require `Eq`; Nanyx contexts have no hierarchy. A type that attaches both should keep them
consistent, and the docs say so.

This closes [#476](https://github.com/nanyx-lang/nanyx/issues/476) and unblocks `list.sort`, `min`, `max`
([#475](https://github.com/nanyx-lang/nanyx/issues/475)) and a `sortBy` with `(a -> b)` keys.

# Hashing

`Hash(a)` has `hash: a -> int`, derived for every type `Eq` is derived for. A map or set keyed by `k` requires
`[Eq(k) & Hash(k)]`, so records and tags can be keys without an order.

A type that attaches its own `==` gets **no derived `Hash`**: the derived hash would disagree with the custom
equality. Asking for `Hash(Email)` is then an error that says to attach `Email.hash` too.

Whether maps are hash-based (needing `Hash`) or ordered trees (needing `Ord`) is a standard library decision; this
design makes hashing the natural choice, since more types have it.

# Printing

As [ToString(a)](../advanced/contexts#tostringa) describes, `ToString(a)` is a
well-known context for interpolation holes, synthesized from an attached `toString`, and derived part by part for
tags with a payload, tuples, records and lists. As with `==`, a `ToString(T)` loaded with `use` changes concrete holes in its
block.

# Numeric operators

Making the arithmetic operators well-known contexts raises separate questions: one context per operator or one
`Num`, how literals like `0` are typed, and how units and dimensions fit. They are answered in
[Numeric contexts](./numeric-contexts), which keeps the principle this design sets: a built-in operator is sugar for
a well-known context's member. `+`, `-` and prefix `-` now read through the prelude's `Add(a)`, which the compiler
finds by the member each reads rather than by name ([Operator contexts](./operator-contexts)).

# Diagnostics

Each is reported by the type checker, with a span; none reaches the backend.

| Situation | Message (draft) |
|---|---|
| `==` on functions | `'(int -> int)' cannot be compared with ==: functions have no equality` |
| `==` on a type holding a function | `'Button' cannot be compared with ==: its field 'onClick' is a function, and functions have no equality` |
| Annotated generic function compares without `[Eq(a)]` | `'same' compares values of type 'a' with ==, so its type must require Eq(a): write [Eq(a)] a, a -> bool` |
| `<` on a type with no order | `'Point' cannot be ordered with <: records have no order. Attach 'Point.compare', or compare a key with sortBy` |
| Attached `==` with the wrong type | the existing `MissingContextMembers` message |
| `!=` attached to a type | `'!=' cannot be defined: it is always 'not (a == b)'. Attach '==' instead` |
| `==`, `compare` or `hash` attached to a structural type | `'Point.==' cannot be defined: Point is a structural type, so it always compares by its fields. Make it nominal (Point) to define its own equality` |
| `Hash(T)` wanted where `T` has its own `==` | `'map.insert' hashes values of type 'Email', which cannot be hashed: 'Email' defines its own ==, so its hash must agree with it: attach 'Email.hash'` (implemented) |
| Closed types with nothing in common | warning: `'#red' and '#some(int)' have no tag in common, so this is always false` |

The language server's code action for the third adds the requirement to the signature.

# What changes for existing code

- Generic functions that use `==` without a signature are unaffected: the requirement is inferred.
- Generic functions with a signature that use `==` on a type variable need `[Eq(a)]` added. In the prelude that is
  `list.distinct`, `list.contains` and `list.indexOf`.
- `==` on records, tuples and lists starts working; on functions it moves from a backend error to a type error.
- The docs' contexts page keeps its `Eq(a)` example, but as the prelude's context rather than one the user declares.

# Order of work

1. **Done.** Parse backtick-named definitions and context members, and call an attached operator
   ([#527](https://github.com/nanyx-lang/nanyx/issues/527)).
2. `Eq` in the prelude; `==` and `!=` read through it; structural derivation; per-type equality functions in the
   backend; the join for open unions. Closes #424 and #526.
3. `Ord`, `compare` and the four operators; `list.sort`, `min`, `max`, generic `sortBy`. Closes #476.
4. `Hash` (done, [#533](https://github.com/nanyx-lang/nanyx/issues/533)), then maps and sets keyed by it.
5. `ToString` (#330, done; see [ToString(a)](../advanced/contexts#tostringa)).

# Open questions

# Decided

1. **Generic code says it compares.** `==` on a type variable requires `[Eq(a)]`, inferred when the type is not
   written. (Rejected: every non-function type has equality implicitly, checked at instantiation.)
2. **`use` changes a concrete `a == b` too.** `==` is always a lookup of `Eq(T)`, so an `Eq(string)` loaded with `use` changes plain
   `==` in its block as well as generic code called from it. This reverses operators.md:53, which is updated when
   the work lands, and agrees with how `ToString` works. (Rejected: `use` reaches only generic code.)
3. **Derived orders** are for numbers, bool, string, tuples and lists only; records and tags attach `compare` or
   use `sortBy`. (Rejected: tags by name, records by field name.)
4. **First round:** `Eq`, then `Ord`, then `Hash` with maps, each its own piece of work. Numeric operators get
   their own design.

5. **The member is named `` `==` ``**, as the docs have it, so this waits on
   [#527](https://github.com/nanyx-lang/nanyx/issues/527). (Rejected: `equals`.)
6. **Only nominal types may attach `==`, `compare` or `hash`.** A structural type, including an alias
   such as `type Point = (x: int, y: int)`, always compares structurally, so `(x = 1, y = 2)` compares the same
   whether or not its alias is known. Attaching one of these to a structural type is an error. (Rejected: any named
   type.)
7. **Mutable `MArray` and `MRef` have no `Eq`**; convert to a list first. (Rejected: identity, contents.)

# Still open

- Should a private type's module be able to withhold equality from other modules? [Recommended: no, not until
  someone needs it.]
