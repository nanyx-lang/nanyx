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

Attached functions are inferred from their bodies like any other function. Only where the body leaves a single parameter open (it could be anything, or any record with the fields it reads) does the parameter take the type the definition is attached to. In the example above, `p` is a `Point`: the body reads `p.x` and `p.y`, which a `Point` has. A parameter the body uses as something else keeps that type, so a constructor function takes what it builds from:

```nanyx
def Point.diagonal = { n -> Point(x = n, y = n) }

def d = Point.diagonal(3)
```

Here `n` is an `int`, and `Point.diagonal` is an `int -> Point`.

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

A name that is not a type cannot have definitions attached to it: `def Option.map` is an error, since there is no type `Option`. A helper that works on any option-like union is an ordinary function, such as the prelude's `mapSome`.

# Extending a type

Any module can attach a new definition to any type, its own or not, as extension methods do in C# and Kotlin: `def string.shout` above can be written anywhere. An extension comes into scope as any definition does, by importing its module, and a use is ambiguous (`NYX0106`) when two imported modules extend a type with the same name.

An attached definition can extend a type, but never redefine what is already in scope for it. A name the type has from an import, or from the standard library for a built-in type, is the error `NYX0254` when attached again:

```nanyx fragment
def string.length: string -> int = { s -> 42 }  -- error NYX0254: 'string.length' is already in scope for 'string', from nanyx/text
def string.shout: string -> string = { s -> s ++ "!" }  -- fine: strings have no `shout`
```

So `"abc" \length` is always the length of the string, whichever module it is written in.

A type's own definitions, those attached in the module that defines it, or in the standard library for a built-in type, are in scope wherever its values are, whether or not their module is imported. A module that gets a `Point` from another module's function pipes it to `Point.norm` without importing `Point`'s module, and `"abc" \length` needs no `import nanyx/text`. A module you do import is reached as the import says: after `import nanyx/text as t`, `string.trim` is `t.string.trim`. Where two types the module can see have the same name, it imports the one it means, as their definitions' names would not say which. The built-in types' comparisons are the compiler's own, so `` def int.`<` `` is the error `NYX0219`.

# When to use attached definitions

- You want method-style access in pipes.
- It feels like these definitions *belong* to the type.
- You want to provide operators or context defaults for a type.
