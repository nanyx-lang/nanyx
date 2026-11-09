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
  nanyx/string as s -- Linter warning: import `s` is never used
)

def x = m.sqrt(16)
```

## Unreachable match branch

```nanyx
match n
  | _ -> "always"
  | 0 -> "never reached"
-- Linter warning: unreachable pattern branch
```

## Unreachable code path

```nanyx
-- Linter warning: unreachable code
```

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
