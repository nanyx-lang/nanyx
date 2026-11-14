---
title: "Nominal Types"
description: "Protected and private types: nominal identities and opaque boundaries"
order: 3
---

Nanyx is—by default—a structurally-typed language, but sometimes a type's name matters more than its structure. To support these scenarios, Nanyx programs can define _nominal_ types, declared with `protected` or `private` after the `=`.

Nominal types have an identity that is distinct from their shape, which helps prevent accidental misuse when multiple values share a similar structure. They are very commonly used to wrap primitives—like strings and numbers—to create domain-specific types with invariants or validation.

There are two kinds, named by their keywords. A _protected_ type has a name that matters and a shape anyone can use. A _private_ type has a name and nothing else outside its module.

`protected` applies to `context` declarations as well as to `type` declarations. A nominal context can only be instantiated by its home module; the standard library's `Memory` context is the main example, which is why `memory { ... }` is the only way to obtain one (see [Memory](./memory)).

A nominal type's name is written like any other type's, so plain text doesn't show which types are nominal. The editor does: the VS Code extension shows nominal types in italics, and hovering a type's name says which kind it is and where it is declared (see [VS Code extension](../standard-tools/vs-code-extension#nominal-types)).

# Protected types

A protected type is declared like any other type, with `protected` after the `=`. The declaration gives the type a shape, but the name is what matters: two nominal types with the same shape are still different types, and a plain value of that shape is not a nominal value until it has been wrapped.

A nominal type belongs to its home module, the module that declares it. Two modules that each declare a `Person` declare two different types, so a module can declare its own `Person` and still use another module's as `p.Person`, and a value of one is never accepted where the other is expected.

```nanyx
module email

export type Email = protected string
```

## Constructors

Declaring `Email` also declares a constructor of the same name. Applying `Email` to a value of the underlying shape wraps it, and matching on `Email(e)` unwraps it again:

```nanyx
def wrapped = Email("ada@example.com")   -- Email
def Email(raw) = wrapped                 -- raw: string
```

For record shapes the constructor takes the record's fields by name:

```nanyx
export type User = protected (
  name: string
  email: Email
)

def user = User(name = "Ada", email = wrapped)
```

The constructor (and the ability to clone a value) is only available inside the type's home module. Everywhere else, `Email("...")` is a compile error. This is the whole point: the home module is the only place a nominal value can come into existence, so it is the only place that needs to uphold the type's invariants. A type declared in a `module name =` block has that block as its home module, not the whole file, so one file can keep a nominal type sealed away from the rest of it (see [Using a module block in its own file](../tour/modules#using-a-module-block-in-its-own-file)).

The usual pattern is to export a _smart constructor_—an ordinary function that validates its input and calls the real constructor only when the input is acceptable:

```nanyx
module email

export type Email = protected string

export def Email.parse: string -> Result(Email, #invalidEmail) = { value ->
  if value \contains("@") then
    #some(Email(value))
  else
    #error(#invalidEmail)
}
```

Callers in other modules obtain an `Email` by going through `Email.parse`; there is no other way to get one. Because the function is an [attached definition](./attached-definitions) on `Email`, it reads naturally at the call site: `Email.parse("ada@example.com")` or, in a pipeline, `input \Email.parse`.

The smart constructor can return whatever is appropriate: a `Result` when there is something useful to say about why the input was rejected, an [option-like type](./option-like-types) when there isn't, or the nominal type itself when construction cannot fail.

## Assignability

Nominal types are not assignable to each other, even if their structures are identical. A protected type is, however, assignable to its underlying shape for compatibility with existing code.

So, given the definition of `Email` above, `Email` is assignable to `string` but `string` is not assignable to `Email`. In practice this means an `Email` can be used anywhere a `string` is expected, including with every attached definition on `string`:

```nanyx
def email: Email = ...

email.length                        -- 15
email \contains("example")          -- true
email \substring(start = 0, length = 3)  -- "ada"
email \split(on = "@")              -- ["ada", "example.com"]
```

Note that these all produce plain `string`s (or `int`s, `bool`s and so on), not `Email`s. Taking a substring of an email address does not give you another email address, and the type system agrees.

The same applies to record shapes. A protected record is still a record, so its fields can be read with ordinary dot access from any module, even though only the home module can construct one:

```nanyx
def user: User = ...

user.name          -- "Ada"
user.email.length  -- 15
```

This is often the sweet spot for domain types: construction is controlled, but reading is free, and callers need no accessor functions just to look at the data.

If you wish a nominal type to be completely opaque and not assignable to its underlying shape, you can use a private type instead.

# Private (opaque) types

Private types go further by hiding their structure completely outside their home module: they are declared with `private` after the `=`. Outside that module the type has a name and nothing else: no shape, no constructor, and none of the operations of the underlying type. A private type is nominal already, so it is never also declared `protected`.

```nanyx
module ids

export type UserId = private int

export def UserId.new: int -> Result(UserId, #invalidUserId) = { value ->
  if value <= 0 then
    #error(#invalidUserId)
  else
    #some(UserId(value))
}
```

Outside the `ids` module, callers can pass `UserId` values around and store them in records, but they cannot construct one directly, pattern-match on `UserId(n)` to get at the integer, or treat the value as an `int`:

```nanyx
def a: UserId = ...
def b: UserId = ...

a + b              -- error: `+` is not defined for UserId
a < b              -- error: `<` is not defined for UserId
a \math.sqrt       -- error: UserId is not assignable to float
a * 2              -- error: UserId is not assignable to int
```

## Accessors

Because private types hide their representation, the home module is also the only place that can look inside one. If callers legitimately need the underlying value—to put it in a URL, say, or write it to a database—export an accessor that returns it:

```nanyx
module ids

-- expose a read-only view of the underlying int
export def UserId.value: UserId -> int = { UserId(n) -> n }
```

The accessor is a deliberate choice about which operations make sense for the type. `UserId.value` says "you may read the number out"; it does not say "you may add two of them together", and the type system will continue to reject that.

# Choosing between them

The difference comes down to one question: is a value of your type still a value of the underlying type?

An email address is a particular kind of string. It makes sense to ask how long it is, whether it contains a substring, or what comes after the `@`. A protected `Email = protected string` captures this exactly: it cannot be confused with any other string, it can only be created by `Email.parse`, and yet it still behaves as the string it is.

A user ID is not a particular kind of integer. It happens to be represented as one, but no operation on integers is meaningful on it: adding two user IDs together, taking the square root of one, or asking whether one is less than another are all mistakes. A private `UserId = private int` captures this exactly: the integer is an implementation detail, and the only things you can do with a `UserId` are the things the `ids` module chooses to export.

Put another way:

- Use a **protected** type when you want identity and explicit construction but the operations of the underlying shape are still meaningful on your type. Examples: `Email`, `Name`, `Path`, `Metres`.
- Use a **private** type when you want a hard abstraction boundary, or a guarantee that changing the internal representation will not affect external users. Examples: `UserId`, `OrderId`, `Token`, `Handle`.

Combine nominal types with contexts and workflows to make safe domain-specific APIs.
