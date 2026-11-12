---
title: "Official style linter"
description: "Style-focused diagnostics from the official Nanyx linter"
order: 1
---

Nanyx keeps compiler errors focused on correctness, while style and convention checks are handled by linters.

The **official Nanyx style linter** enforces recommended coding conventions, including guidance around exported definitions.

Linters are just Nanyx libraries that run during compilation and emit diagnostics. The official Nanyx style linter is installable / uninstallable in any project, and is included in the default project template. It is entirely possible (even encouraged) to write custom linters for your team or project.

# Why it matters

The style linter helps teams keep APIs predictable and readable by flagging patterns such as:

- Exported definitions missing type annotations
- Convention or naming issues
- Other style-oriented recommendations that do not block compilation

# Rules 

## Exported annotations

```nanyx
-- Linter warning: missing type annotation
export def add = { a, b -> a + b }
```

These diagnostics are warnings/hints from the linter, not the compiler.

## Unused local value

```nanyx
def total =
  def taxRate = 0.2 -- Linter warning: `taxRate` is never used
  100
```

## Unused function argument

```nanyx
-- Linter warning: argument `title` is never used
def greet: (string, string) -> string = { name, title ->
  "Hello, {name}!"
}
```

## Unused pattern variable

```nanyx
match response
  | #some(value) -> "Success" -- Linter warning: `value` is introduced by pattern but never used
  | #error(msg) -> "Error"  -- Linter warning: `msg` is introduced by pattern but never used
```

## Unused import

```nanyx
import (
  nanyx/math as m
  nanyx/text as t -- Linter warning: `t` is imported but never used
)

def x = m.sqrt(16.0)
```

The rule (`unusedImports`) reports an import that nothing in the module uses. A value, type or context it brings in counts as a use, whether it is written through the import's alias (`m.sqrt`, `m.Shape`) or unqualified, and a type named only in an annotation counts too. The prelude, which every module imports, is never reported, and neither is the import `@entry` adds for you, nor an alias starting with `_`. Its fix removes the import: the whole `import` line when it is the only one there, or just that module's line from an `import (...)` group.

## Unused private definition

```nanyx
module shapes

def scale = 2              -- Linter warning: `scale` is never used
type Corner = (x: int)     -- Linter warning: type `Corner` is never used
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
def isRed: #red | #green -> bool = { c ->
  c == #blue -- Linter warning: '#red | #green' and '#blue' have no tag in common, so this is always false
}
```

The rule (`noTagInCommon`) reports `==` or `!=` on two values whose types are closed tag unions that share no tag. The comparison type-checks, since `==` accepts any two tags it can join into one union, but it can never be true (or, with `!=`, never false). A comparison with an open union, such as `o == #none` where `o` can be any tag, is not reported.

## `use ... in` with a body in braces

```nanyx
def total = use down in { counted(3) } -- Linter warning: The braces make this a function of no arguments, not a block run with the context loaded
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

## Contexts joined with `+`

```nanyx
def both: [Eq(a) + Hash(a)] a, a -> bool = { x, y -> x == y and hash(x) == hash(y) } -- Linter warning: Join contexts with `&`
```

The rule (`plusBetweenContexts`) reports a `+` between the contexts a function requires. Contexts are joined with `&`, as in `[Eq(a) & Hash(a)]` (see [Contexts](../advanced/contexts)); `+` is what an earlier version of the language wrote, and the compiler still reads it. Its fix replaces the `+` with `&`, and the formatter writes `&` too. A member's type runs on past a `&`, so the fix also brackets a member before the `+`: `` [`+`: a, a -> a + Eq(a)] `` becomes `` [(`+`: a, a -> a) & Eq(a)] ``.

## Unreachable code path

```nanyx
def f: int -> int = { x ->
  panic("bad input")
  x + 1 -- Linter warning: this code never runs: `panic` never returns
}
```

The rule (`unreachableCode`) reports the statements of a block that come after one that never gives a value: a statement whose type is [`undefined`](../tour/types#top-and-bottom-types), such as a call to `panic`, a call to a function you have annotated to return `undefined`, or an `if` whose every branch is one of those. It reports the first statement that never runs, through the end of the block, and points at the statement that stops the block. Its fix removes the statements that never run, unless the statement before them is a def, since a block can't end with a def.

## Recursion not in tail position

```nanyx
def length: list(a) -> int = {
  | [] -> 0
  | [_, ...tail] -> 1 + length(tail) -- Linter info: `length` calls itself here, but not in tail position: its result is an operand of `+`
}
```

A [tail call](../tour/functions#tail-recursion) uses no stack, but any other call keeps a stack frame until it returns, so a function like this runs out of stack on a long enough list. The rule (`nonTailRecursion`) reports a function's calls to itself that are not in tail position, and says why each one is not. Calls from a lambda written inside the function are not checked.

It is `info` by default, since recursion that is not in tail position is often fine: a walk over a tree only recurses as deep as the tree. A project that wants every loop to be stack-safe can raise it:

```toml
[linters.default]
nonTailRecursion = "warning"
```

## Unused return value

```nanyx
def compute: [Console] int -> int = { n ->
  readLine() -- Linter warning: the result of `readLine` is computed but never used
  42
}
```

# Learn more

For the full linter model (diagnostic levels, configuration, and custom linters), see [Linters](../advanced/linters).
