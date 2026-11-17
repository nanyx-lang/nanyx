---
title: "Official style linter"
description: "Style-focused diagnostics from the official Nanyx linter"
order: 1
---

Nanyx keeps compiler errors focused on correctness, while style and convention checks are handled by linters.

The **official Nanyx style linter** enforces recommended coding conventions, including guidance around exported definitions.

Linters run during compilation and emit diagnostics. The official Nanyx style linter is the package `nanyx.lint`, turned on in a project by an entry in its manifest's `linters`, and in a script by a `::linter nanyx.lint` directive (see [Linters](../advanced/linters#configuring-linters)). `nanyx new` turns it on for you. Writing your own linter is planned, but not possible yet.

Each rule's diagnostics have the code `default/<rule>`, such as `default/unusedBindings`. That is the name to use in [`@allow`](#suppressing-a-rule-in-one-place); in a manifest's `settings` and in a `::lint` directive, a rule is named on its own, `unusedBindings`. Many rules come with a fix, which the editor offers as a quick fix.

| Rule | Default | Reports |
|------|---------|---------|
| [`unannotatedExports`](#exported-annotations) | warning | an exported def that does not write its type |
| [`unusedBindings`](#unused-local-value) | warning | a local, a lambda's argument or a pattern variable that is never used |
| [`unusedImports`](#unused-import) | warning | an import nothing uses |
| [`unusedPrivateDefinitions`](#unused-private-definition) | warning | a definition the module does not export and does not use |
| [`unreachableArms`](#unreachable-match-branch) | warning | a match arm the arms before it already cover |
| [`noTagInCommon`](#comparison-that-is-always-false) | warning | `==` or `!=` on two unions with no tag in common |
| [`useInBraces`](#use--in-with-a-body-in-braces) | warning | `use ... in { ... }`, whose body is a function, not a block |
| [`strayDocComments`](#doc-comment-that-documents-nothing) | warning | a doc comment that documents nothing |
| [`unreachableCode`](#unreachable-code-path) | warning | statements after one that never gives a value |
| [`nonTailRecursion`](#recursion-not-in-tail-position) | info | a function calling itself other than in tail position |
| [`unusedResults`](#unused-return-value) | error | a statement whose value is thrown away |
| [`uselessExpressions`](#expression-that-does-nothing) | error | a pure statement whose value is thrown away |
| [`shadowedPreludeNames`](#name-that-hides-a-prelude-member) | warning | a name that hides one the prelude brings into scope |
| [`semicolons`](#statements-separated-by-) | warning | statements separated by `;` |
| [`unconstructibleType`](#exported-type-no-one-can-get) | error | an exported nominal type nothing its module exports gives out |
| [`unusedAllows`](#allow-that-suppresses-nothing) | warning | an `@allow` that suppresses nothing |
| [`linterFailed`](#when-the-linter-fails) | warning | the linter could not check a module |

# Why it matters

The style linter helps teams keep APIs predictable and readable by flagging patterns such as:

- Exported definitions missing type annotations
- Convention or naming issues
- Other style-oriented recommendations that do not block compilation

# Rules 

## Exported annotations

```nanyx
-- Linter warning: `add` is exported but does not write its type: [Add(a)] a, a -> a
export def add = { a, b -> a + b }
```

The rule (`unannotatedExports`) reports an exported top-level def that does not write its type, including one a `module name =` block exports. Other modules use it by its type, so the type belongs where a reader finds it rather than left to inference. Its fix writes the type the def was inferred to have after its name, as hover shows it, with the contexts it requires separated by `,`: `export def add: [Add(a)] a, a -> a = { a, b -> a + b }`. A destructuring def, `export def (low, high) = ...`, has no one name to write a type after, and is not reported.

## Unused local value

```nanyx
def total =
  def taxRate = 0.2 -- Linter warning: `taxRate` is never used
  100
```

The rule (`unusedBindings`) reports a local def, a lambda's argument or a variable a pattern binds that is never used. A name starting with `_` is not reported, so `_rate` says a binding is unused on purpose. Its fix removes an unused local, or renames an unused argument or pattern variable to `_`.

## Unused function argument

```nanyx
-- Linter warning: argument `title` is never used
def greet: string, string -> string = { name, title ->
  "Hello, {name}!"
}
```

## Unused pattern variable

```nanyx
match response
  | #some(value) -> "Success" -- Linter warning: `value` is introduced by pattern but never used
  | #error(msg) -> "Error" -- Linter warning: `msg` is introduced by pattern but never used
```

## Unused import

```nanyx
import (
  nanyx/math as m
  nanyx/text as t -- Linter warning: `t` is imported but never used
)

def x = m.sqrt(16.0)
```

The rule (`unusedImports`) reports an import that nothing in the module uses. A value, type or context it brings in counts as a use, whether it is written through the import's alias (`m.sqrt`, `m.Shape`) or unqualified, and a type named only in an annotation counts too. The prelude, which every module imports, is never reported, and neither is the import `@entry` adds for you, nor an alias starting with `_`. Its fix removes the import: the whole `import` line when it is the only one there, or just that module's line from an `import (...)` group. In an editor, **Organise imports** is offered as a fix too: it removes every import nothing uses at once, and writes the rest as one sorted import.

## Unused private definition

```nanyx
module shapes

def scale = 2 -- Linter warning: `scale` is never used
type Corner = (x: int) -- Linter warning: type `Corner` is never used
export def area = { w, h -> w * h }
```

The rule (`unusedPrivateDefinitions`) reports a top-level def, type or context that the module does not export, and a member of such a context, when nothing in the module uses it. Only the module can name what it does not export, so an unused one is dead code. A use from within the definition itself, such as a function calling itself or a type naming itself, does not count. In a `module name =` block, the defs the block does not export are private to it, and are reported in the same way.

It is a rule of its own, apart from `unusedBindings`, so a project can keep one and turn the other off. It leaves alone `@entry`'s def, which the runtime calls; a definition attached to a type that an instance picks up, such as a `Point.toString` that string interpolation uses; and names starting with `_`. Its fix removes the definition's lines. An unused context member has no fix, since removing it means editing the context's type.

## Unreachable match branch

```nanyx
match n
  | _ -> "always"
  | 0 -> "never reached" -- Linter warning: this arm can never match: an arm before it matches every value it does
```

The rule (`unreachableArms`) reports an arm that the arms before it already match every value of: one after a catch-all such as `_` or a name, one after an arm that matches everything it does (`#some(5)` after `#some(_)`, `5` after `1..10`), and one after arms that between them match every value it does: every tag of a union, both booleans, every length of list, every int of a range (`3..8` after `1..5` and `6..10`), or every combination of the values a match is on. It points at the arms that cover it, and its fix removes the arm. An arm with a guard (`{ > 0 }`) never counts as covering another, since whether it matches depends on more than the value's shape.

## Comparison that is always false

```nanyx
def isRed: (#red | #green) -> bool = { c ->
  c == #blue -- Linter warning: '#red | #green' and '#blue' have no tag in common, so this is always false
}
```

The rule (`noTagInCommon`) reports `==` or `!=` on two values whose types are closed tag unions that share no tag. The comparison type-checks, since `==` accepts any two tags it can join into one union, but it can never be true (or, with `!=`, never false). A comparison with an open union, such as `o == #none` where `o` can be any tag, is not reported.

## `use ... in` with a body in braces

```nanyx
def total = use Counter(next = { n -> n - 1 }) in { counted(3) } -- Linter warning: The braces make this a function of no arguments, not a block run with the context loaded
```

The rule (`useInBraces`) reports `use ... in` whose body is a lambda of no arguments written as `{ ... }`. Braces make a function, not a block, so the statements in them are not run: `total` is a function. Put the statements on the lines after `in`, indented, to run them with the context loaded (see [Contexts](../advanced/contexts#loading-a-context)). A lambda written with its argument, `{ () -> ... }` or `{ n -> ... }`, says it means a function, and is not reported.

## Doc comment that documents nothing

```nanyx
def f = {
  --| The total. -- Linter warning: This doc comment documents nothing
  42
}
```

The rule (`strayDocComments`) reports a [doc comment](../getting-started/basics#doc-comments) that isn't followed by something it can document: a definition, a type or context, a member of a context, a field, a tag, or the module itself at the top of the file. Its fix makes it an ordinary `--` comment.

## Unreachable code path

```nanyx
def f: int -> int = { x ->
  crash("bad input")
  x + 1 -- Linter warning: this code never runs: `crash` never returns
}
```

The rule (`unreachableCode`) reports the statements of a block that come after one that never gives a value: a statement whose type is [`undefined`](../tour/types#top-and-bottom-types), such as a call to `crash`, a call to a function you have annotated to return `undefined`, or an `if` whose every branch is one of those. It reports the first statement that never runs, through the end of the block, and points at the statement that stops the block. Its fix removes the statements that never run, unless the statement before them is a def, since a block can't end with a def.

## Recursion not in tail position

```nanyx
def length: list(a) -> int = {
  | [] -> 0
  | [_, ...tail] -> 1 + length(tail) -- Linter info: `length` calls itself here, but not in tail position: its result is an operand of `+`
}
```

A [tail call](../tour/functions#tail-recursion) uses no stack, but any other call keeps a stack frame until it returns, so a function like this runs out of stack on a long enough list. The rule (`nonTailRecursion`) reports a function's calls to itself that are not in tail position, and says why each one is not. Calls from a lambda written inside the function are not checked.

It is `info` by default, since recursion that is not in tail position is often fine: a walk over a tree only recurses as deep as the tree. A project that wants every loop to be stack-safe can raise it:

```nanyx fragment
linters = [(package = "nanyx.lint", version = "0.28.0", settings = (rules = (nonTailRecursion = #warning)))]
```

## Unused return value

```nanyx
def compute: [Console] int -> int = { n ->
  readLine() -- Linter error: The result of `readLine`, of type 'string', is never used
  42
}
```

The rule (`unusedResults`) reports a statement of a block, other than its last, whose value is thrown away though it is not `()`: a call made for its effects, whose result nobody reads. It is an error by default. Bind the value to a name, or pass it to [`ignore`](../stdlib/prelude#ignore) to say you don't need it, which is what its fix does: `ignore(readLine())`.

It leaves alone a statement whose type is `()`, such as a call to `println`; the last statement of a block, which is the block's value; a `for` loop, whose value is `()` whatever its body gives; a workflow keyword's statement, such as `try check(args)` in a `proc` block, whose value the workflow takes; a call of the built-in `dbg`, `dbg(xs)` or `xs \dbg`, written on a line of its own to print a value; a statement whose type is [`undefined`](../tour/types#top-and-bottom-types), which `unreachableCode` is about; and a pure statement, which `uselessExpressions` reports.

## Expression that does nothing

```nanyx
def next: int -> int = { n ->
  n + 1 -- Linter error: This value, of type 'int', does nothing and is never used
  n
}
```

The rule (`uselessExpressions`) reports a statement that `unusedResults` would, but which is pure: neither its type nor any call in it requires a [context](../advanced/contexts), so it can do nothing but give its value, and that is thrown away. Usually it is a mistake: a `def` left out, an `==` meant as a binding, or an operand split from the line above. It is an error by default, and its fix removes the statement. A call of a function that requires no context is pure too. One of a function whose contexts are a variable, such as `list.map`'s `[c]`, is not, since the variable may stand for any context.

## Name that hides a prelude member

```nanyx
def pow = { (x: int) -> x * x } -- Linter warning: `pow` hides the prelude's `pow`, which can't be named where it is in scope

def total: list(int) -> int = { xs ->
  def ignore = 0 -- Linter warning: `ignore` hides the prelude's `ignore`, which can't be named where it is in scope
  list.sum(xs) + ignore
}
```

The rule (`shadowedPreludeNames`) reports a top-level def, a local def, a lambda's argument or a variable a pattern binds whose name is one the prelude brings into scope, such as `ignore`, `crash`, `pow` or `memory`. Wherever the new name is in scope, the prelude's can't be named, so a later reader who expects the prelude's gets yours. It is a warning by default, and has no fix: rename the definition.

Only the names the prelude brings into scope unqualified count. A definition attached to a type, such as `list.map`, is named through the type, so a `def map` of your own hides nothing; nor does a `def toString` outside a def that requires `ToString`, where the context's member isn't in scope. The standard library's own modules, which define the prelude's names, are not reported.

## Statements separated by `;`

```nanyx
def total = { n ->
  def tax = n / 5; n + tax -- Linter warning: Put each statement on a line of its own, rather than separating them with `;`
}
```

The rule (`semicolons`) reports every `;` that separates two statements on one line (see [Variables](../tour/variables#several-statements-on-one-line)). One statement per line is the house style, so it is a warning by default. Where the statements are lines of a block, as here, its fix puts the statement after the `;` on a line of its own, at the block's indent. A one-line body, such as a match arm's or a lambda's, `{ x -> log(x); x + 1 }`, has no block lines to split it into, so it gets no fix. Turn the rule off to write short sequences on one line:

```nanyx fragment
linters = [(package = "nanyx.lint", version = "0.28.0", settings = (rules = (semicolons = #off)))]
```

## Exported type no one can get

```nanyx
module noms =
  -- Linter error: 'UserId' is private, and nothing 'noms' exports returns one, so no code outside 'noms' can get a 'UserId'.
  export type UserId = private int

  export def UserId.toString: UserId -> string = { id -> "user" }
```

Only its home module can make a value of a [nominal type](../advanced/nominal-and-protected-types). When nothing that module exports gives one out, every other module can name the type but never get a value of it, which is almost always an oversight: the author meant to export a `UserId.parse` or a `UserId.of` and forgot. The rule (`unconstructibleType`) reports an exported `protected` or `private` type like this, at its name. It is an error by default.

An exported def gives the type out when the type is in an output position of the def's type: a function's result, however deep in it (`string -> #some(UserId) | #invalid`, `() -> list(UserId)`); a value of the type, or of one that holds it; an argument of a callback the caller passes (`withUser: (UserId -> a) -> a`); or a field of a protected type the module gives out, since anyone can read that. A def that only takes the type, as `UserId.toString` does, doesn't count. A type the module doesn't export, an ordinary structural type and a context are never reported.

Its fix adds an exported `of` after the type, which takes the type's representation and wraps it: `export def UserId.of: int -> UserId = { value -> UserId(value) }`.

The linter sees one module at a time, so a type that another module of the same package gives out is reported too. Write `@allow(default/unconstructibleType)` before the type for it, and for a type with no values on purpose, such as a marker used only as a type argument.

## `@allow` that suppresses nothing

```nanyx
-- Linter warning: `default/unusedBindings` reports nothing in this definition
@allow(default/unusedBindings)
def total: () -> int = { 100 }
```

The rule (`unusedAllows`) reports each rule an [`@allow`](../tour/attributes#allow) names that suppresses nothing in the def after it, so that an `@allow` doesn't outlive the code it was written for. It also reports a rule or linter there is none of. It is a warning by default. When the `@allow` names only that rule, its fix removes the `@allow`.

## When the linter fails

If the linter itself fails on a module, a bug in the linter, the module still compiles: the failure is reported as one `default/linterFailed` diagnostic, a warning, and the linter's other diagnostics for that module are left out. Like any rule, it can be given another severity, or turned off.

# Suppressing a rule in one place

A project's manifest turns a rule off everywhere. To let one definition break a rule, write `@allow` before it, naming the rules it may break: their diagnostics inside that def, a local one too, or that top-level type, are left out.

```nanyx fragment
def total: () -> int = { ->
  @allow(default/unusedBindings)
  def draft = { ->
    def rate = 2 -- not reported
    100
  }
  draft()
}
```

# Learn more

For the full linter model (diagnostic levels, configuration, and custom linters), see [Linters](../advanced/linters).
