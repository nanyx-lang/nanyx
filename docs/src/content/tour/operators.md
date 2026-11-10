---
title: "Operators"
description: "Operators as functions"
order: 14
---

Operators are ordinary functions with special names. They can be used infix, prefix, postfix, or passed around like any other function.

# Defining operators

You define operators by naming a function with a valid operator name, encased in backticks:

```nanyx
def `+++`: string, string -> string = { a, b -> a + b }

def r = "one" +++ "two"
```

Operators can take more than two arguments. Extra arguments are passed as a record, [in the same way as piping](../tour/piping#piping-with-extra-arguments):

```nanyx
def `<+>` = { a, b, c -> ... }

def r = a <+> (b, c)
```

# Using operators as functions

Since operators are just specially-named functions, you can also use them in a non-operator way:

```nanyx
def r1 = "one" +++ "two"
def r2 = `+++`("one", "two")
def f = `+++`
```

Also, lambda syntax supports an [operator-based shorthand](../tour/functions#shorthand-lambdas) for using operators:

```nanyx
[1, 2, 3] \fold(0) { + }
```

# Arithmetic and comparison

Core arithmetic (`+`, `-`, `*`, `/`, `%`, `**`) and comparison (`==`, `!=`, `<`, `<=`, `>`, `>=`) are sugar for the members of contexts of the prelude: `x + y` and `x - y` read through [`Add`](../advanced/contexts#adda), and `x == y` through [`Eq`](../advanced/contexts#eqa). On the built-in types they are the built-in operators: `1 + 2` adds two ints, and calls nothing. To give your own type an operator, attach a definition to it:

```nanyx
def Vector.`+` = { a, b -> Vector(x = a.x + b.x, y = a.y + b.y) }

def c = a + b   -- resolves to Vector.`+` because `a` is a Vector
```

The operator is found by the type of its left operand, exactly as any other attached function is resolved by the type of its first argument.

`-x` negates: it binds tighter than every infix operator, so `-x * y` is `(-x) * y`, and it is written against its operand. `-5` is a literal. On an `int` or a `float` it is the built-in negation (`-0.0` is negative zero); on any other type it is that type's `negate`, the member of `Add`. In a [shorthand lambda](../tour/functions#shorthand-lambdas), a leading `-` is still the infix operator, so `{ -x }` is `{ n -> n - x }`.

Generic code that needs an operator on an unknown type declares a context requirement instead, such as `[Add(a)]`, and the compiler synthesises the instance from the type's attached definitions (see [Contexts](../advanced/contexts)). Loading an `Add` with `use` changes the instance that such generic code sees for that type; making it change a concrete `1 + 2` too, as it will, is [not done yet](../work-in-progress/numeric-contexts#what-is-implemented).

# Pipe and composition

Piping uses `\`, and composition uses `>>` and `<<`:

```nanyx
def normalize = trim >> toLowerCase

def result = data
  \filter { .isActive }
  \map { .name }
  \sort
```

# Valid operator names

An operator name is any sequence of one or more of the following characters: `+`, `-`, `*`, `/`, `%`, `^`, `~`, `<`, `>`, `=`, `!`. For example, `++`, `>>>` and `<+>` are valid operator names, but `+a` is not.

The characters `|`, `&`, `?`, `#`, `@`, `$`, `:` and `\` are not operator characters; they are syntax (see below).

Boolean operations are the keywords `and`, `or` and `not`, not operators. Likewise, testing a value against a pattern in an `if` condition uses the keyword `is` (`if x is #some(v) then ...`), not an operator.

## Reserved spellings

Some sequences built from operator characters are syntax, not operators, and cannot be defined or redefined:

| Token | Meaning |
|---|---|
| `--`, `---` | Line and block comments |
| `->` | Lambda, match arm, function type |
| `=` | Definition, named field |
| `\` | Pipe |
| `..` | Range |
| `...` | Spread and rest |
| `:=` | Assignment |
| `=>` | Map entry |
| `\|` | Match arm, or-pattern, union type |
| `&` | And-pattern, intersection type, context combination |
| `#` | Tag prefix |
| `@` | Attribute prefix |
| `$` | Nominal prefix |
| `:` | Type annotation |
| `?` | Optional marker |
| `???` | Unimplemented placeholder |

The following operators are built in. You can attach them to your own types, but you cannot redefine them globally:

| Operators | Meaning |
|---|---|
| `+`, `-`, `*`, `/`, `%`, `**` | Arithmetic |
| `==`, `!=`, `<`, `<=`, `>`, `>=` | Comparison |
| `>>`, `<<` | Composition |
| `++` | Concatenation |