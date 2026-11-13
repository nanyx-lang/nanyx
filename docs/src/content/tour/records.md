---
title: "Records and tuples"
description: "Structural records, tuples, and field rules"
order: 2.5
---

Records and tuples are a core way to model data in Nanyx; they allow you to group related values together with fields on a single object. 

Fields can be either _positional_ or _named_, meaning that you can mix and match named and positional fields in the same value, as long as all the positional fields come first.

Records and tuples are actually one unified feature in Nanyx; a tuple is just a special case of record where all the fields are positional.

# Record values

Create a record by listing its fields and values inside parentheses. For named fields, use `name = value`:

```nanyx
-- Record with named fields
def alice = (
  name = "Alice"
  age = 30
  email = "alice@example.com"
)
```

You can also use positional fields:

```nanyx
-- Record with positional fields (aka a tuple)
def point = (10, 20)
```

Because records and tuples are unified, both styles can be combined in one value:

```nanyx
-- Mixed record with both positional and named fields
def mixed = (1, 2, label = "origin", unit = "px")
```

# Record types

A record type uses the same shape as a record value, but with type annotations on fields:

```nanyx
def config: (host: string, port: int) = (
    host = "localhost"
    port = 8080
)
```

When you use the same structure in multiple places, define a named type alias:

```nanyx
type Person = (
  name: string
  age: int
  email: string
)
```

Then reference that alias in annotations:

```nanyx fragment
def alice: Person = (
  name = "Alice"
  age = 30
  email = "alice@example.com"
)
```

# Constructing records from a type alias

A record type alias is also callable as a constructor. Calling it builds a value of that shape:

```nanyx
type Point = (x: int, y: int)

def p = Point(x = 3, y = 4)
```

The arguments follow exactly the same rule as any other call: positional fields are passed positionally and named fields are passed by name. Since `Point` has named fields, `Point(3, 4)` is an error. For a type with positional fields, such as `type Pair = (int, int)`, the constructor is called positionally: `Pair(3, 4)`.

# Structural typing

Record types are structural, meaning compatibility is based on shape rather than explicit inheritance. If two types have compatible fields, they can be used interchangeably where appropriate.

```nanyx
type Named = (name: string)
type Person = (name: string)

def john: Named = Person(name = "John")
```

You can also compose record requirements with intersections:

```nanyx fragment
type Person = Named & (age: int)
```

# Closed and open records

A record type is _closed_: it has exactly the fields it lists, no more and no fewer. A record with an extra field is a different type, so it can't be passed where the smaller record is expected:

```nanyx
def f: (x: int, y: int) -> int = { x, y -> x + y }

def data = (x = 1, y = 2, z = 3)

f(data) -- ✗ error NYX0201: `f` takes no field `z`
```

This is what makes named arguments safe. A function's parameters are a record, so a misspelled argument such as `f(x = 1, y = 2, verbos = true)` is an error rather than silently ignored.

To accept any record that has _at least_ some fields, end the type with `...`. This makes it _open_:

```nanyx skip=#471
def g: (x: int, y: int, ...) -> int = { x, y -> x + y }

g(data) -- `z` is one of the other fields
```

Give the other fields a name, as in `...r`, when a type needs to say that two records have the same ones. Here, whatever else the input has, the output has too:

```nanyx skip=#471
def moveRight: (x: int, ...r) -> (x: int, ...r) = { p -> p with x = p.x + 1 }

def moved = moveRight((x = 1, y = 2, label = "a"))
-- moved: (label: string, x: int, y: int)
```

`...` only covers named fields. A record's positional fields are always exactly the ones its type lists, so a function of two positional parameters can't be called with three.

You rarely need to write `...` yourself. When the compiler infers a function's type from the fields it reads, the type it infers is already open:

```nanyx
def area = { r -> r.width * r.height }
-- inferred as `(height: int, width: int, …) -> int`

area((width = 2, height = 3, colour = "red")) -- fine
```

An editor writes an inferred record's fields in alphabetical order, and its other fields as an ellipsis, `…`, where you would write `...`. The other fields get a name, such as `…ω`, only where the type uses them twice.

An annotation is taken exactly as written, so annotate with `...` when you mean "at least these fields".

[Record patterns](./pattern-matching#records) follow the same rule. A pattern names every field of the record, or ends in `...` to leave the others out:

```nanyx fragment
match person
  | (name = "Alice", ...) -> "Found Alice"
  | (name = n, age = a) -> "{n} is {a}"
```

# Records vs tuples

In Nanyx, tuples and records are one unified structure. A tuple is just a record whose fields are all positional.

This is why you can mix positional and named fields in the same value:

```nanyx
def mixed = (1, 2, label = "origin", unit = "px")
```

The one ordering rule is: all positional fields must come before any named fields.

```nanyx
-- Valid
def valid = (10, 20, x = 10, y = 20)

-- Invalid (named field before positional field)
-- def invalid = (x = 10, 20)
```

# Fields that may be absent

A field that may have no value says so in its type, with a union of tags. The usual one is `#some(T) | #none`, the same type the standard library gives back when it may find nothing:

```nanyx
type Person = (name: string, petsName: #some(string) | #none)

def alice = Person(name = "Alice", petsName = #some("Rex"))
def bob = Person(name = "Bob", petsName = #none)

def describe = { person: Person ->
  match person.petsName
  | #some(pet) -> "{person.name} has a pet called {pet}"
  | #none -> "{person.name} has no pet"
}

println(describe(alice))
println(describe(bob))
```

The field is always there, so a record of this type is built with a value for it, `#none` when there is nothing to say. Tags of your own can say why a value is missing, as `#unknown | #declined | #some(string)` does.

There is no `?` that marks a field or a type optional: `petsName?: string` is an error, which says to write the union instead.

# Copy and update (non-destructive mutation)

Records are typically treated immutably. To create a modified copy, use `with`.

Update a single field:

```nanyx
def alice = (name = "Alice", age = 30)
def olderAlice = alice with age = 31
```

Update multiple fields at once:

```nanyx
def user = (name = "Alice", age = 30, email = "old@example.com")
def updatedUser = user with (
  age = 31
  email = "alice@example.com"
)
```

You can also chain `with` for nested records:

```nanyx
def line = (
  range = (
    start = (character = 0)
  )
)

def updatedLine = line with (
  range = line.range with (
    start = line.range.start with (
      character = 4
    )
  )
)
```

This preserves the original value and returns a new record with only the specified fields changed. This means that the result of a `with` expression is always of the same type as the input record.

# Record spread (`...`)

Nanyx also supports record spread using the `...` sigil (similar to JavaScript).

Spread can combine any number of source objects, interleaved with explicit field definitions:

```nanyx
def defaults = (host = "localhost", port = 8080)
def env = (port = 9000)
def cli = (logLevel = "debug")

def config = (
  ...defaults
  protocol = "https"
  ...env
  ...cli
)
```

The parts are evaluated in the order they are written, and a later part's field replaces an earlier one's: here `config.port` is `9000`. Each record spread must be one whose fields are known where it is spread; a lambda's parameter whose type nothing has fixed yet can't be spread. The other fields of a record with a spread are written by name.

`with` and spread solve related but different problems:

- `with` has exactly one input object.
- The result of `with` is always the same type as that input object.
- Spread can merge any number of objects and new field definitions.
- The result of spread can be any arbitrary record type.

--- 

For the broader type-system overview, see [Types](./types).
