---
title: "Attached definitions"
description: "Methods and properties attached to types"
order: 8
---

Attached definitions let you add functions and values directly onto a type. This is how you define methods, operators, and static-like members without classes.

# Attaching a function

Use dot syntax to attach a definition to a type.

```nanyx
type Point = (x: int, y: int)

def Point.length = { p ->
  Math.sqrt(p.x ** 2 + p.y ** 2)
}
```

Attached functions are inferred exactly like any other function; attaching a definition to a type says nothing about the types of its parameters. In the example above, `p` is inferred to be a `Point` because the body accesses `p.x` and `p.y`.

Call it in a qualified way like any other function:

```nanyx
def p = Point(x = 3, y = 4)

def len = Point.length(p)
```

Or use it in a pipeline without needing to specify the type name:

```nanyx
def len = p \length
```

# Dot access

Dot syntax on a value reads what the value itself has: the fields of a record, and the properties a built-in type has, such as the `length` of a string or a list. It never reaches an attached definition. An attached definition is reached by its qualified name, or by piping the value into it, which resolves the name by the value's type:

```nanyx
def len1 = Point.length(p)
def len2 = p \length
```

`p.length` would be an error here, since `Point` has no `length` field. Keeping the two apart means a field and an attached definition of the same name never compete: `p.x` is always the field `x`, and `p \x` always the definition attached to `Point`. See [Piping](../tour/piping) for passing further arguments.

# Attaching operators

Operators are just attached definitions with symbolic names:

```nanyx
export def Point.`+` = { a, b ->
  Point(x = a.x + b.x, y = a.y + b.y)
}
```

The built-in arithmetic and comparison operators on `int`, `float` and the other primitive types are attached definitions in exactly the same way; see [Operators](../tour/operators).

# Attached functions as typeclass defaults

Attached definitions can satisfy contexts or typeclass-style constraints. For example, defining `+` and `0` on a type can allow a `Sum` context to be synthesized.

```nanyx
def Point.`+` = { a, b -> Point(x = a.x + b.x, y = a.y + b.y) }

def Point.`0` = Point(x = 0, y = 0)
```

# Attaching to a built-in type

The name before the dot must be a type. A built-in type can be named by its alias as well as by its name (see [Built-in type names](../tour/types#built-in-type-names)), and the two mean the same type here too, so a definition attached to strings can be written and called with `string` or `String`:

```nanyx
def string.shout = { (s: string) -> s ++ "!" }

def a = string.shout("hi")
def b = String.shout("hi")
def c = "hi" \shout
```

`def String.shout` would be the same definition. The prelude writes its functions with the aliases: `int.fromString`, `list.map`, `map.get` and so on.

`Int`, `Float` and `Bool` are not types: `Int.fromString` is the error `NYX0114`, with a fix that writes `int`.

A name that is not a type cannot have definitions attached to it: `def Option.map` is an error, since there is no type `Option`. A helper that works on any option-like union is an ordinary function, such as the prelude's `mapOption`.

# When to use attached definitions

- You want method-style access in pipes.
- It feels like these definitions *belong* to the type.
- You want to provide operators or context defaults for a type.
