---
title: "Type descriptions"
description: "Why types are passed and inspected as they are, how a match on a Type value is checked, and what it compiles to"
order: 16
---

A `match` on a `Type(a)` inspects the type it stands for, as [Inspecting a type](../tour/functions#inspecting-a-type)
describes. This page gives the design behind type arguments and descriptions, and says how the compiler checks a match
on one and what it compiles to. What is still planned is on
[Type members and reflection](../work-in-progress/type-members-and-reflection) in Work in progress.

# Design

Three problems led here. Code that follows a type's structure (serialisation, equality, hashing, printing) had to be
written by hand or built into the compiler. A decoder, `def deserialise: [Decode(a)] string -> a`, mentions its type
only in its output, so the call site had no good way to say which `a` it wants, as Haskell's `read` and Rust's `parse`
have none, and where nothing fixed `a` the user got an ambiguity error that is hard to read. And a constraint over
collections had to carry the element type everywhere, `[Iter(c, a)]`, though it follows from the container. Associated
types ([Contexts](../advanced/contexts#associated-types)) answer the last; type arguments answer the other two. Both are
the same thing: a record member whose value is a type.

- **A type is passed as an argument,** `type a` or `into: type`, of type `Type(a)`, always written at the call. It is
  the same idea as Haskell's `Proxy a`, Scala's `ClassTag` and Kotlin's `KClass<T>`. The named form makes a type
  parameter the same kind of member as an associated type, gives the argument a name at the call site, and matches the
  constraint syntax; `type a` is its positional counterpart. A named type member is passed by name like any named
  field: letting `deserialise(text, type Point)` skip that rule would make type arguments the one exception to a rule
  that holds for every call and every record construction, and an author who wants positional calls declares
  `type a`.
- **Generic code is checked once, not per instantiation.** Inspection is type-checked when the generic function is
  defined, as all generic code is. Nanyx does not adopt compile-time templates, at least for now: in Zig's `comptime`
  and C++ templates, generic code is checked separately for each type it is used at, and errors appear inside library
  code.
- **Narrowing on a `Type` match only,** not full GADTs, and **a `build` the compiler provides** for decoding, not
  rank-2 functions in the language.
- **A nominal type is read-only to reflection outside its home module:** there its description is `#nominal`, with its
  name and module but no structure and no `build`. Its encoding comes from its attached definitions or a context its
  module provides.
- **A description contains** field and case names and types, the type's name and module, its type parameters,
  attributes on fields and doc comments.
- **Customising an encoding:** a type that provides the library's context itself wins over walking its description, as
  an explicit instance wins over synthesis, and attributes on fields cover renames without one.
- **`Typed(a)` is synthesised for every type,** nominal ones too, so a function given a value of type `a` needn't also
  take `type a`. It is the implicit counterpart of a type argument, as Haskell's `Typeable` is of `Proxy`, and as
  Scala's `ClassTag` is usually an implicit parameter. The walker stays explicit: a record's fields have types the
  checker never learns, so there is no `Typed` instance for them, only `f.type`, and a library takes `Typed(a)` in its
  public function and passes the type explicitly in the code that walks it,
  `def encode: [Typed(a)] a -> Json = { value -> encodeAt(value, typeOf) }`. Giving `Typed` of a nominal type
  everywhere reveals nothing, since outside its home module its description is `#nominal`.

Precedents: Jane Street's `typerep` for OCaml, GHC's `Type.Reflection`, and the "Scrap your boilerplate" approach
(`Data`). Swift's `Mirror` and Go's `reflect` have a similar shape but are not typed.

## Alternatives considered

- **Runtime reflection.** C# and Java keep a description of every type at runtime and let any code inspect any value.
  In a single WebAssembly module this costs binary size, defeats removal of unused code, and needs a description format
  that the runtime and compiler agree on. It would also be an implicit capability, which in Nanyx would have to be a
  context; `Typed(a)` is that context, resolved at compile time.
- **Compile-time templates.** Zig's `comptime`, C++26 static reflection and D's templates allow arbitrary code over
  types at compile time. They are very expressive, but generic code is only checked when it is instantiated, which
  conflicts with checking generic code once. This could be revisited as a later extension if typed descriptions prove
  too limited.
- **Type-level induction over structure.** The compiler could expose a record as a type-level list of field names and
  types, and libraries would handle the "no fields" and "one field, then the rest" cases with contexts whose instances
  the compiler chains together. This is how PureScript's `RowToList` (used by `simple-json`), GHC Generics, Scala 3
  Mirrors and shapeless work, and it suits Nanyx's row-like records. It needs instances for type constructors that
  themselves require contexts, and field names as type-level strings. Users would program at the type level, and
  errors would come from failed instance searches, which these libraries are known for.
- **Deriving clauses and macros.** Haskell's `deriving`, Rust's `#[derive]` and procedural macros, and OCaml's ppx all
  generate code from a type's definition. Nanyx has no compiler plugins that extend the language, which rules out
  procedural macros and ppx. A built-in set of derivations would not let libraries add their own, such as JSON or SQL
  mapping.
- **Structural context synthesis.** Context synthesis works by name: the compiler builds `Add(Point)` from `Point`'s
  attached definitions. Only the [well-known contexts](../advanced/contexts#common-contexts), `Eq`, `Ord`, `Hash` and
  `ToString`, are also derived from structure, so that `Eq` of a record is built from the `Eq` of its fields. Opening
  that to any context would need no new syntax, but it cannot express library-specific behaviour such as JSON field
  names.
- **Source generators.** C#'s source generators run user code at compile time that emits more source. The
  [linter](../advanced/linters) infrastructure could host them, since linters will run Nanyx code at compile time.
  Generated code would have to exist before type checking, while linters run after it, so generators would need their
  own phase and a narrower view of the program. They remain an option for cases that type arguments cannot reach.

# Checking

`InferMatch.inferMatch` hands a match whose one value is a `Type(a)` to `TypeMatches.infer`. The tags a description has, and
the built-in description types (`RecordInfo(a)`, `UnionInfo(a)`, `NominalInfo`, `Field(a)`, `Case(a)`, `SomeType`)
with their members, are in `TypeValues`, which the backend reads too.

- **Narrowing.** Where `a` is a type variable, or a type the checker never learns, an arm whose tag says what it is is
  checked with `a` written as that (`TypeMatches.narrowEnv`) in the types of the names in scope (only names whose
  schemes have free variables, `TypeEnv.Unquantified`, can hold it) and in the type the match is expected to give. A
  lambda's declared result reaches a match that ends its body (`TypeMatches.expectResult`), and a def's block result
  reaches one that ends the block (`ExpectedBlockResults`). Each arm's body is checked against that narrowed result
  with `Visibility.addDeclaredConstraintAt`. Narrowing is not unification: the constraints stay global, and only the
  arm's view of the names in scope changes.
- **Packaged types.** A field, a case, a type argument and a collection's element each have a type the checker never
  learns, a skolem (`TypeDescriptions.skolem`): a `TyApply` with no arguments whose name, `b#12`, no other type has, so
  it unifies only with itself. Diagnostics write it `b`. Reading a member of a field opens it once per name
  (`InferState.OpenedPackages`, by the name's symbol), so `f.get(value)` and `f.type` agree. A value of one can't leave
  the lambda that opened it (`TypeDescriptions.escapingParameters`, checked in `Infer.inferLambdaAfterTypeOfs`) or the
  arm whose tag made it (`TypeDescriptions.escaping`), NYX0274.
- **Recursion.** A walker calls itself at a part's type, so a def whose annotation is complete sees itself, while its
  body is checked, generic in the variables it writes (`InferModule`, where it binds `envWithRec`).
- **For the backend,** each arm that narrows `a` records the type it narrows it to at its pattern's node, in
  `ExprTypes`, which an importing module's `InlinedExprTypes` keeps with the nodes of the definitions it inlines.

# Compiling

A `Type` value is a number at run time, an `IrInt`: the number of the type it stands for in the module
(`CoreIrTypeIds`). `type X` is that number as a constant, and each type gets one when it is first met. The module's
`__describe` function, lowered after every other function (`CoreIrLowerModule`), builds the description of the type of
each number: a tag of `TypeValues.descriptionUnion`, with a record's `RecordInfo` and its fields, each field's type as
a number in turn, so `__describe` goes on until every type numbered has one. The module holds descriptions of the
types its `Type` values name and of the types of their parts, and of no others.

A type is numbered as seen from the file of the `type X` that names it (`CoreIrTypeIds`), and a description's parts
are seen from there too: a nominal type seen from outside its home module (`Compiler.sourceFileName` of its home is
not the file) is described as `#nominal`. So a function specialised at a nominal type can't pick its arm at compile
time: it keeps every arm that could match, its structure's and `#nominal`'s, and matches on `__describe`. A
description's `name` and `parameters` come from the alias or nominal type, its `doc` and its fields' `attributes`
from the type's definition (`TypeDefInfo.Doc`, filled from the module's doc comments by `Compiler.withDocs`, and
`TypeDefInfo.FieldAttributes`, read by `TypeResolution.fieldAttributeArguments`), which the backend has as
`LowerInput.TypeDefinitions`.

`FunctionLowering.Match` hands a match on a `Type` to `CoreIrLowerTypeMatch.lower`:

- **A known type.** In a function specialised at a type it knows, the match picks its arm at compile time. An arm that
  narrows `a` matches if what it narrows it to fits the type, and what that finds of its skolems, the element type of a
  `#list(elem)` arm, is bound from there on (`CoreIrLowerTypes.substitute` maps a skolem by its number). An arm that
  reads nothing of the description is lowered alone; one that does matches on what `__describe` gives.
- **A type only known when the program runs.** A skolem left unbound is `boxedTy`, so a function called at a field's
  type, as a walker is, is specialised once with that type's values boxed (`IrBox`, as a context member's own type
  variables are, see [Context calls](context-calls)). Its match matches on `__describe` of the number it is given. In
  an arm that narrows `a`, a boxed name is unboxed where it is used (`FunctionLowering.Identifier`), and the arm's value
  is boxed back to the match's type (`FunctionLowering.Branch`), both through `CoreIrLowerBoxes.convert`.
- **Canonical boxes.** For that to work whatever the value is, a boxed value of a collection holds its elements boxed:
  a list, a set or an array of boxes, and a map whose entries are tuples of boxes. A record or a union is boxed as it
  is, since only its description's `get`s read it. A field's `get` (`fieldGettingFunction`) and a case's `get`
  (`caseGettingFunction`) are closures over functions of their own that unbox the value, read the part, and box it in
  this form, through a function per collection type (`canonicalisingFunction`).

# Building

A record description's `build` has a type of its own each time it is read (`TypeDescriptions.memberType`): it is
given a function from a field opened at a skolem made for this `build`, `Field(a, b)`, to `#some(b)` or a failure, and
gives `#some(a)` or that failure. Since the function is checked with `b` a type of its own, it gives a value of any
field's type: the rank-2 type users never write. A case's `build`, from its packaged type to `a`, is an ordinary member.

`CoreIrLowerBuild` runs them. A record's description holds, besides the members a program reads, `__make`
(`CoreIrLowerTypes.makeMember`): a closure that makes a value of the record from its fields' values, boxed in their
canonical form, in the order the description has its fields, unboxing each with the inverse of canonical boxing
(`decanonical`). `r.build` (`FunctionLowering.Complex`, on a member access of `build` on a `RecordInfo`) is a closure
over the description and a function of its own, specialised by the IR type of `build` at the use, which calls the
function it is given with each field (`buildingFieldsFunction`), gathers the boxes, and makes the record with
`__make`, or gives the first failure, made again as a value of the result's union. A case's `build` unboxes the payload
and makes the tag.