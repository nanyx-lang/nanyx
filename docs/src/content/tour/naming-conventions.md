---
title: "Naming conventions"
description: "Recommended casing and identifiers"
order: 15
---

Nanyx uses camel case for values, functions, modules and tags, and Pascal case for types.

| Kind | Case | Examples |
|------|------|----------|
| Values and functions | camelCase | `message`, `sayHello`, `userCount` |
| Modules | camelCase | `userManagement`, `collections.list` |
| Types and contexts | PascalCase | `Greeter`, `Console`, `$UserId` |
| Tags | camelCase after `#` | `#ok`, `#notFound` |
| Custom pattern tags | PascalCase after `#` | `#Hsl`, `#Even` |

```nanyx
-- Value

def message = "Hello world"

-- Function

def sayHello = { println("Hello!") }

-- Type

type Greeter = (greet: () -> ())
```

Because a record type alias is callable as a constructor (see [Constructing records from a type alias](./records#constructing-records-from-a-type-alias)), a Pascal-cased name in expression position is almost always such a constructor: `Point(x = 1, y = 2)`.

Custom patterns use Pascal case after the `#` so that they are visually distinct from ordinary tags; see [Custom patterns](../advanced/custom-patterns).

# Names in backticks

A name that is not an identifier is written in backticks, wherever a name goes: an operator, a keyword, or a whole sentence.

```nanyx
def `adds two points` = { ... }
def `is it empty?`: string -> bool = { s -> s == "" }

def total = `adds two points`(p, q)
def person = (`first name` = "Ada")
def first = person.`first name`
```

The text in backticks can be anything but a backtick, a line break or a dot, which separates the parts of a qualified name, and it cannot be blank. An operator is named the same way; see [Operators](./operators). Sentences are mostly for names that are never called, such as the names of tests.

Backticks around an identifier change nothing: `` `total` `` is `total`, and the formatter writes it without them.
