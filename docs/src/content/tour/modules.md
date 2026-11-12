---
title: "Modules and imports"
description: "Organizing code with modules and import patterns"
order: 12
---

Modules in Nanyx help organize code into logical units. They provide namespaces for definitions and are the primary unit of encapsulation, controlling visibility.

The common case is that there is a 1:1 relationship between files and modules, but Nanyx also supports nesting modules inside a file or splitting a module across multiple files. The module system is flexible to accommodate different organizational styles.

Every module is inherently one of two kinds, decided entirely by its declaration: a **named module**, declared with `module name`, or a **script (anonymous) module**, with no module name at all. This is a property of the declaration itself, not of how the module happens to be imported elsewhere.

# Module declaration

Every Nanyx file optionally starts with a module declaration.

```nanyx
module main

@entry
def main = {
  println("Hello from MyApp!")
}
```

A named module like `main` above is imported by its module name; it cannot also be imported by file path (see [Importing modules by file path](#importing-modules-by-file-path)).

If the module declaration is omitted, the module is a script: it has no name, and can only be imported by file path, using an alias.

```nanyx
-- No module name means this is a script module: it can only be imported by file path, and only with an alias

println("Hello from MyApp!")
```

# Module naming

Module names should be camel-cased and reflect the purpose of the code contained within:

```nanyx fragment
module userManagement

...
```

It is a commonly-used convention to use dots to create namespaced hierarchies of modules within a package:

```nanyx fragment
module collections.list

...
```

# Importing modules

Import other modules to use their exports:

```nanyx fragment
module main

-- Notice how we can import multiple modules in a single statement
import (
  userManagement
  dataProcessing as d
)

createUser(...) -- from userManagement
d.process(...) -- uses the alias `d` for dataProcessing
```

A plain import like `userManagement` above lifts every exported member of that module directly into scope, unqualified. This is deliberate, and pairs with private-by-default module members ([Exports](#exports)): a module only exports what it explicitly marks with `export`, so the exporting module — not the importer — controls what enters its public API. Because of that, it's safe for a plain import to be open by default; qualification via an alias, and selective imports, remain available whenever you want to be more explicit.

Note that most standard library functions are [attached definitions](../advanced/attached-definitions) such as `list.map` or `string.split`, which are resolved by the type of the value piped into them (`xs \map(f)`) rather than imported as bare names. This is why importing several standard library modules at once rarely produces ambiguous names.

## Importing modules by file path

Script modules have no module name, so they can only be imported by file path. When importing a file path, you must use an alias to give the module a name in your current scope.

```nanyx fragment
-- If importing a file the module name must be qualified
import "./utils" as utils

utils.someHelper(...)
```

A named module cannot be imported this way, even if you know its file path:

```nanyx fragment
-- userManagement.nyx
module userManagement

export def createUser = { ... }
```

```nanyx fragment
-- Error: `userManagement` declares a module name, so it must be imported as `userManagement`, not by file path
import "./userManagement" as um
```

## Importing modules from other packages

When importing external packages (such as those installed from a package manager), the import includes the package name as a prefix. Dots separate levels within a module's own hierarchy; a single `/` separates the package name from the module name:

```nanyx fragment
-- Notice how the package name and module name are separated by a slash
-- We import the `http` and `json.deserialization` modules from the `nanyx.web` package
import (
  nanyx.web/http
  nanyx.web/json.deserialization
)
```

The standard library is available under the `nanyx` package and is available in all Nanyx programs; there's no need to install it:

```nanyx fragment
module main

import nanyx/collections
...
```

## Qualified imports

Access module members with the module name:

```nanyx
import nanyx/math as m

def result = m.sqrt(16.0)  -- 4.0
def pi = m.pi
```

An aliased import brings nothing into scope unqualified, types included: a type the module exports is written with the alias too, as `m.Shape` or `m.$Email`, and so are the definitions attached to it, as `m.$Email.parse(...)`.

## Selective imports

Import specific items from a module using deconstruction syntax on the import:

```nanyx skip=#289
import nanyx/math as (sqrt, pi, abs)

def result = sqrt(16.0)  -- No need for an m. prefix
```

It's even possible to both deconstruct and alias an import, although the need to do this is rare:

```nanyx skip=#289
import nanyx/math as m & (sqrt, pi)

def a = sqrt(16.0)
def b = m.abs(-1)
```

## Ambiguous imports

If two imported modules both export the same unqualified name, referring to that name directly is ambiguous and is an error — Nanyx does not use import order, or any kind of overload resolution, to pick one:

```nanyx fragment
import (
  csvParser
  jsonParser
)

-- Error: `parse` is ambiguous between `csvParser.parse` and `jsonParser.parse`
def data = parse(input)
```

Resolve the clash explicitly, either by aliasing one or both imports, or by selectively importing only the name(s) you need from each:

```nanyx fragment
import (
  csvParser as csv
  jsonParser as json
)

def data = csv.parse(input)
```

# Exports

Types and definitions are private to a module by default — this is what makes it safe for a plain import to lift a module's members directly into scope (see [Importing modules](#importing-modules)). Use `export` to make a definition part of a module's public API:

```nanyx fragment
module utils

-- Public
export def processData: Data -> Result = { data ->
  data \validate \transform
}

-- Private helpers
def validate: Data -> Data = { data ->
  ...
}

def transform: Data -> Data = { data ->
  ...
}
```

Modules themselves do not need to be exported; they are available for import by other modules within the same package as long as they contain at least one exported definition. Visibility to other packages is controlled separately, by the `public-modules` list in the package manifest (see [Public modules](./packages#public-modules)).

## Re-exporting

Export items from other modules:

```nanyx fragment
module collections

-- Re-export from sub-modules
export collections.list (map, filter, fold)
export collections.map (empty, insert, lookup)
```

# Declaration order

Inside a named module, textual declaration order has no semantic significance. Every declaration in the module — public or private, wherever it sits in the file — participates in the same module-wide scope, so a definition may refer to another definition declared later in the same module:

```nanyx fragment
module shapes

export def describe: Shape -> string = { shape ->
  "area = {area(shape)}" -- refers to `area`, declared below
}

def area: Shape -> float = { shape -> ... }
```

## Dependency cycles

Order-independent name resolution is not the same as unrestricted recursion. The compiler analyses the dependency graph between declarations separately from name resolution, and rejects cycles where a value would need itself to be constructed:

```nanyx skip=#955
module invalid

-- Error: `a` and `b` form a cycle; neither can be built without the other
def a = b + 1
def b = a + 1
```

Being visible everywhere in the module (as `b` is to `a` above) doesn't excuse a genuine initialization cycle — this is a distinct check on top of ordinary name lookup. A recursive *function* is not a cycle of this kind: its body is not evaluated until it is called, so a top-level function may refer to itself freely. Only recursive definitions local to a function body need the `rec` keyword, as described in [Recursive functions](./functions#recursive-functions).

# Nested modules

You can declare module blocks inside a file. The block name is appended to the outer module name (if one exists):

```nanyx fragment
-- myModule.nanyx
module myModule

-- The full name of this module is `myModule.functions`
module functions =
  export def f = { x -> x * 2 }
  export def g = { x -> x ** 2 }

  module helpers =
    def helper1 = { ... }
    def helper2 = { ... }

    -- If any of the definitions in this module were exported, the full name would be `myModule.functions.helpers`
```

A nested module encapsulates just like a file-level one: what it does not `export` can only be named inside the block, even by code elsewhere in the same file. Above, `functions.f` can be used anywhere in the file, but `helpers.helper1` only within `helpers`.

If the file has no file-level module declaration, the nested module names are not prefixed:

```nanyx fragment
-- looseCode.nanyx

-- The full name of this module is simply `functions`
module functions =
  export def f = { x -> x * 2 }
  export def g = { x -> x ** 2 }

-- And the full name of this module is `utils`
module utils =
  def helper1 = { ... }
  def helper2 = { ... }
```

## Using a module block in its own file

A block's exports can always be named with the block's name in front, as above (`functions.f`). A block can also be imported by the rest of its file, as a file module would be, by its short name:

```nanyx
import nanyx/console

module money =
  -- Prices are whole pence, so no rounding ever creeps in.
  export type $Money = (pence: int)

  export def $Money.new: int -> $Money = { pence -> $Money(pence = pence) }

  export def $Money.toString: $Money -> string = { m ->
    def pounds = m.pence / 100
    def rest = m.pence % 100
    if rest < 10 then "£{pounds}.0{rest}" else "£{pounds}.{rest}"
  }

import money

def price: $Money = $Money.new(250)

println(price \toString)
```

This gives a single file the encapsulation of a separate module: only `money` can build a `$Money` (see [Nominal and protected types](../advanced/nominal-and-protected-types)), so the rest of the file has to go through `$Money.new`. Moving the block to a file of its own later, `money.nyx` beside this one and starting `module money`, changes nothing that uses it: `import money` then finds the file.

A block is imported exactly as a file module is:

- `import money` brings its exports in unqualified; `import money as m` brings them in as `m.$Money`, `m.$Money.new` and so on.
- Only exports can be used, however the block is reached.
- The import can come before or after the block, since declaration order doesn't matter in a module.
- The name is the block's short name (`money`), even when the file declares a module of its own and the block's full name is `myModule.money`. A block can import the blocks nested directly inside it the same way.

A block is also isolated as a file module is. It sees its own definitions and types, the blocks nested in it, the prelude, and what it imports itself, but not the rest of its file. So a block that needs `nanyx/console` imports it inside the block, and an import inside a block is that block's alone. A block is a module rather than a program, so it can't have top-level code: everything in it is a definition, a type or an import.

Create hierarchies with dot notation:

```nanyx fragment
module users.validation

def validateEmail: string -> Result(Email, ValidationError) = { ... }
def validateAge: int -> Result(Age, ValidationError) = { ... }
```

# Partial modules

A module can be split across multiple files by giving each file the same module name. Semantically this is exactly one module whose declarations happen to live in several files: every fragment contributes to the same module-wide scope described above, a declaration in one fragment may refer to a declaration in another, and there is no ordering between the files themselves.

The compiler conceptually assembles all fragments of a partial module before doing name resolution or type checking, as if they had been concatenated into a single file — file order, like declaration order, carries no meaning.

Because sharing a module name across files is easy to do by accident, each fragment must opt in explicitly with the `@partial` attribute; without it, declaring the same module name in two files is an error.

```nanyx fragment
-- validation.nyx
@partial module utils

-- `isNonEmpty` is declared in the other fragment
export def validate: string -> bool = { s -> s \isNonEmpty }
```

```nanyx fragment
-- format.nyx
@partial module utils

def isNonEmpty: string -> bool = { s -> s.length > 0 }

export def format: string -> string = { s -> s \trim }
```

# Top-level initialization

Top-level definitions in a named module must be context-free: they cannot require a context such as `[Console]` or `[Random]`, since a top-level initializer has no place to declare — or receive — a context requirement of its own.

```nanyx skip=#956
module config

def maxRetries = 3              -- Ok: context-free
def timeout = maxRetries * 10   -- Ok: context-free, and free to reference other definitions in any order

def greeting = println("Hi")    -- Error: `println` requires [Console], which a top-level initializer cannot have
```

Because ordinary effectful initialization isn't permitted, named modules have no observable source-order initialization semantics: there's no effectful step whose order could ever be seen, which is what makes the order-independence described in [Declaration order](#declaration-order) sound rather than just convenient.

Context-free is not the same as total: a top-level definition can still diverge, panic, or fail at runtime, and this rule doesn't by itself account for intrinsics or any future FFI boundary.

Effects enter a program through its entry *function*, the one marked `@entry` (see [Attributes](./attributes)). The runtime provides the `[Console]` context when it calls that function, and every other effectful function is reached from there. There is no exception for the module that contains it: it is an ordinary named module and follows the rules above.

# Script execution order

The rules above are specific to named modules. A script module — one with no module name — may contain top-level executable statements, and for those, execution order is meaningful and preserved, exactly as written:

```nanyx fragment
-- No module name: this is a script, and these statements run top to bottom
println("starting")
def result = compute()
println("done: {result}")
```

Not every source file is order-independent — only the declarations inside a named module are. A script's top-level statements behave the same way they would in any imperative language.

# Circular dependencies

Avoid circular module dependencies. If module A imports B, then B cannot import A. (This is about dependencies between modules; for cycles between declarations inside a single module, see [Declaration order](#declaration-order).)

```nanyx fragment
-- a.nyx (bad: circular dependency)
module a
import b  -- a imports b
```

```nanyx fragment
-- b.nyx
module b
import a  -- b imports a (circular!)
```

Instead, extract the shared code to a third module:

```nanyx fragment
-- shared.nyx
module shared
-- Common definitions
```

```nanyx fragment
-- a.nyx
module a
import shared
```

```nanyx fragment
-- b.nyx
module b
import shared
```

# Modules in the standard library

Nanyx's standard library appears as a package called `nanyx` and is organized into modules:

```nanyx skip=#870
import (
  nanyx/collections
  nanyx/map
  nanyx/set
  nanyx/string
  nanyx/math
  nanyx/option
  nanyx/result
)
```

One module is never imported by name: the **prelude**, `nanyx/prelude`, is imported into every module automatically, so what it defines is in scope in a file with no imports at all. It holds `proc`, [`panic`](./error-handling), [`ignore`](../stdlib/standard-library#discarding-a-value), the `Result(a, e)` type and the [`Result`](../stdlib/result) and [`Option`](../stdlib/option) helpers. A module's own definitions and its explicit imports take precedence over the prelude's, so defining your own `proc` is fine.

# Example: User management module

```nanyx fragment
module userManagement

export type User = (
  id: UserId
  name: string
  email: Email
  role: #admin | #user
)

export type ValidationError =
  | #invalidEmail
  | #nameTooShort
  | #emailTaken

export def createUser
  : [Random] (string, Email) -> Result(User, ValidationError) 
  = { name, email ->
    if name.length < 3 then
      #error(#nameTooShort)
    else
      def id = generateId()
      #some(id = id, name = name, email = email, role = #user)
  }

def validateEmail: Email -> Result(Email, ValidationError) = { email ->
  if email \contains("@") then
    #some(email)
  else #error(#invalidEmail)
}

def generateId: [Random] () -> UserId = {
  -- Implementation detail, not exported
  randomInt()
}
```

# Module Organization Best Practices

1. **One module per file**: Keep modules focused and manageable
2. **Clear naming**: Use descriptive names that reflect the module's purpose
3. **Hide implementation**: Mark helpers as private
4. **Cohesion**: Group related functionality together
5. **Minimal coupling**: Reduce dependencies between modules
6. **No circular deps**: Keep dependency graph acyclic
