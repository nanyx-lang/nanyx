---
title: "Operators"
description: "Operators as functions, and the list of them"
order: 14
---

Operators are ordinary functions with special names. Nanyx has a fixed list of them, each with its own precedence ([The operators](#the-operators)): you can give your own types any operator on the list, but you can't make up new ones.

# Defining operators

You define an operator by naming a function after it, encased in backticks:

```nanyx
def `<>`: string, string -> string = { a, b -> a ++ " " ++ b }

def r = "one" <> "two"
```

Operators can take more than two arguments. Extra arguments are passed as a record, [in the same way as piping](../tour/piping#piping-with-extra-arguments):

```nanyx skip=#965
def `<+>` = { a, b, c -> a + b * c }

def r = 1 <+> (2, 3)
```

# Using operators as functions

Since operators are just specially-named functions, you can also use them in a non-operator way:

```nanyx
def `<>`: string, string -> string = { a, b -> a ++ " " ++ b }

def r1 = "one" <> "two"
def r2 = `<>`("one", "two")
def f = `<>`
```

Also, lambda syntax supports an [operator-based shorthand](../tour/functions#shorthand-lambdas) for using operators:

```nanyx
[1, 2, 3] \fold(0) { + }
```

# Arithmetic and comparison

Core arithmetic (`+`, `-`, `*`, `/`, `%`, `**`) and comparison (`==`, `!=`, `<`, `<=`, `>`, `>=`) are sugar for the members of contexts of the prelude: `x + y` and `x - y` read through [`Add`](../advanced/contexts#adda), `x == y` through [`Eq`](../advanced/contexts#eqa), and `x < y` through [`Ord`](../advanced/contexts#orda). On the built-in types they are the built-in operators: `1 + 2` adds two ints, and calls nothing. To give your own type an operator, attach a definition to it:

```nanyx fragment
def Vector.`+` = { a, b -> Vector(x = a.x + b.x, y = a.y + b.y) }

def c = a + b   -- resolves to Vector.`+` because `a` is a Vector
```

The operator is found by the type of its left operand, exactly as any other attached function is resolved by the type of its first argument.

`-x` negates: it binds tighter than every infix operator, so `-x * y` is `(-x) * y`, and it is written against its operand. `-5` is a literal. On an `int` or a `float` it is the built-in negation (`-0.0` is negative zero); on any other type it is that type's `negate`, the member of `Add`. In a [shorthand lambda](../tour/functions#shorthand-lambdas), a leading `-` is still the infix operator, so `{ - x }` is `{ n -> n - x }`, and `{ -x }` is an error, as `n -x` would be.

`x ** n` is `x` to the power `n`: it binds tighter than every other infix operator and groups to the right, so `2 * 3 ** 2` is `2 * 9` and `2 ** 3 ** 2` is `2 ** 9`, 512. As prefix `-` binds tighter still, `-x ** 2` is `(-x) ** 2`. It is not a member of a context: `x ** n` is the prelude's `pow(x, n)`, `[Num(a)] (a, int) -> a`, so it works on any number type, and the exponent is an `int`. A negative exponent is `1 / x ** -n`, which truncates on an `int`. For a fractional exponent, use `float.pow(x, y)`.

Generic code that needs an operator on an unknown type declares a context requirement instead, such as `[Add(a)]`, and the compiler synthesises the instance from the type's attached definitions (see [Contexts](../advanced/contexts)). A function that does not write its type has the requirement inferred, so `def add = { x, y -> x + y }` is `[Add(a)] a, a -> a`, and a type can require an operator by its member, as `` [`+`: a, a -> a] `` (see [Operators find their context](../advanced/contexts#operators-find-their-context)). Loading an `Add` with `use` changes the instance that such generic code sees for that type; making it change a concrete `1 + 2` too, as it will, is [not done yet](../work-in-progress/numeric-contexts#what-is-implemented). Equality already works that way: `a == b` is always a lookup of `Eq` at the operands' type, so after `use ignoringCase`, an `Eq(string)`, a plain `a == b` on strings in that block calls its `==`.

Comparison is read through the standard library's contexts instead: `==` and `!=` through [`Eq(a)`](../advanced/contexts#eqa), and `<`, `<=`, `>` and `>=` through [`Ord(a)`](../advanced/contexts#orda), whose one member is `compare`. Numbers, strings, tuples and lists are ordered already; a nominal type is ordered by attaching a `compare`, and records and tags have no order. A function that orders values of one of its type variables requires `[Ord(a)]`, and an `Ord` loaded with `use` changes what `<` means in its block, on built-in types too.

# Pipe and composition

Piping uses `\`, and composition uses `>>` and `<<`. `f >> g` is the function that applies `f` and then `g` to what `f` returns, and `g << f` is the same function:

```nanyx
def normalize = string.trim >> string.lowercase

def result = [" Ada ", "BOB"]
  \map(normalize)
  \map { s -> s ++ "!" }

@entry
def main = {
  println(result \string.join(" "))
}
-- ada! bob!
```

# The operators

An operator is written with a space on each side: `x + y`, not `x+y` or `x +y`. Without the spaces it is an error, which shows where the space is missing. A line break counts as a space, and so does the `{` of a shorthand lambda that starts with the operator, as in `{+ 1}` (which the formatter writes `{ + 1 }`). Prefix `-` and `not` are not binary operators: `-x` is written against its operand. Keeping operators apart from what is around them leaves their characters free for other uses later, such as a `?` at the end of a name.

These are all of Nanyx's operators, tightest first. An operator binds tighter than those below it, so `a + b * c` is `a + (b * c)`, `f >> g == h` is `(f >> g) == h`, and `a ?? 0 < n` is `(a ?? 0) < n`.

| Operators | Groups | Meaning |
|---|---|---|
| `-x` (prefix), `not` | | negation (`Add`'s `negate`), logical not |
| `**` | right | power: `2 ** 3 ** 2` is `2 ** 9` |
| `*` `/` `%` | left | multiplication, division, remainder (`Num`) |
| `^` `<*>` | left | spare |
| `+` `-` | left | addition, subtraction (`Add`) |
| `<+>` | left | spare |
| `++` | right | concatenation |
| `<>` | right | spare |
| `>>` | left | forward composition |
| `<<` | right | backward composition |
| `??` | right | [unwrapping with a fallback](../tour/error-handling#falling-back-with-): `a ?? b` |
| `<` `<=` `>` `>=` | none | ordering (`Ord`) |
| `~` | none | spare |
| `..` | none | [range](../tour/ranges) |
| `==` `!=` | none | equality (`Eq`) |
| `>>=` | left | spare |
| `=<<` | right | spare |
| `and` | left | logical and |
| `or` | left | logical or |

A **spare** operator has no built-in meaning: it means only what a definition of it gives it, one attached to the type of its left operand or one in scope, so `1 ^ 2` is an error. The others are built in, and you can attach them to your own types, as `` Vector.`+` `` above, but not redefine them globally. Defining an operator that is not on the list, such as `` `+++` ``, is an error.

An operator that groups to the left reads `a - b - c` as `(a - b) - c`, and one that groups to the right reads `a ++ b ++ c` as `a ++ (b ++ c)`. One that does not group can't be written twice in a row: `a < b < c` is an error, which asks for `a < b and b < c`, and so is `a == b == c`. Two operators of one precedence that group different ways can't be written side by side either: `f >> g << h` needs brackets around one of them.

Boolean operations are the keywords `and`, `or` and `not`. `&&` and `||` are not operators, and neither is a prefix `!`: the compiler tells you to write the keyword instead. `<<` and `>>` are composition only; bitwise operations on integers are named functions. Likewise, testing a value against a pattern in an `if` condition uses the keyword `is` (`if x is #some(v) then ...`), not an operator.

The characters `|`, `&`, `?`, `#`, `@`, `$`, `:` and `\` are not operator characters; they are syntax (see below). `??` is built in, like `and` and `or`: its right operand runs only when it is needed, so it is not a function, and it cannot be defined or attached to a type.

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
