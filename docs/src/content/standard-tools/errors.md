---
title: "Compiler errors"
description: "Every error the compiler reports, by code: when it is reported and how to fix it"
order: 4
---

Every error the compiler reports has a code, such as `NYX0201`, and a short title, such as "Type mismatch". The
command line shows both before the message, and an editor links the code to its section on this page.

The codes are grouped by what the compiler is doing when it finds the error:

- `NYX00xx`: reading a file, its directives and its imports.
- `NYX01xx`: resolving the names a program uses.
- `NYX02xx`: checking types.
- `NYX03xx`: generating WebAssembly.

Each section shows a short program that reports the error, with the message in a comment on the line it is reported
at. Where an example takes more than one file, each file starts with a `-- file:` line naming it, and the first
file is the one compiled.

# NYX0001: Syntax error

The file could not be parsed: the text at the position shown is not what the grammar allows there. The message
names what the parser expected.

```nanyx
def total = (1 + 2  -- error NYX0001: the closing parenthesis is missing
```

To fix it, write what the message says is expected. When the position is at the end of the file, a bracket or brace
is usually left open further up.

# NYX0002: File not found

The file given to `nanyx` does not exist. Nothing is compiled, so this is the only error reported.

To fix it, check the path given to the command, which is read from the directory the command is run in.

# NYX0003: Import not found

An import names a file or a module that does not exist. For a file-path import, the path is read from the importing
file's directory, and `.nyx` is added to it.

```nanyx
import "./helpers" as helpers  -- error NYX0003: Import not found: .../helpers

def total = 1
```

To fix it, correct the path or the module's name, or create the file.

# NYX0004: Import cycle

A module imports itself, through the modules it imports. Modules are checked one after another, each after the
modules it imports, so they cannot import each other.

```nanyx
-- file: main.nyx
import "./a" as a

def total = a.one
-- file: a.nyx
import "./b" as b

export def one = 1
-- file: b.nyx
import "./a" as a  -- error NYX0004: Import cycle detected for .../a.nyx

export def two = 2
```

To fix it, move what the modules share into a module that neither of them imports.

# NYX0005: Unknown standard library module

An import that starts with `nanyx/` names a module the standard library does not have. The message lists the
modules it has.

```nanyx
import nanyx/strings  -- error NYX0005: Unknown standard library module 'nanyx/strings' (available: ...)

def total = 1
```

To fix it, import one of the modules the message lists. The [standard library](../stdlib/standard-library) describes them.

# NYX0006: Invalid standard library module

A module of the standard library is broken: it declares a different module name from the one it is imported by, it
imports something other than the standard library, or the prelude's contexts share a member in a way that leaves an
operator with no one context to be read through
([Operator contexts](../work-in-progress/operator-contexts)).

A program cannot cause this error. It is reported when the standard library itself is changed, and it is fixed there.

# NYX0007: Unknown attribute

A definition is marked with an attribute the compiler does not know. The attributes are listed in
[Attributes](../tour/attributes).

```nanyx
@inline  -- error NYX0007: Unknown attribute '@inline'
def double: int -> int = { x -> x * 2 }
```

To fix it, correct the attribute's name, or remove it.

# NYX0008: Misplaced attribute

An attribute is not followed by a definition, the only thing it can be written on.

```nanyx
def double: int -> int = { x -> x * 2 }

@entry  -- error NYX0008: Attribute '@entry' must be followed by a definition
```

To fix it, write the attribute on the line before the definition it marks.

# NYX0009: Invalid directive

A `::` directive at the top of a script is not one the compiler understands, or its value is not one the directive
takes. Directives say for a script what a package's manifest would say
([Packages](../tour/packages)).

```nanyx
::linters.tidy  -- error NYX0009: unknown linter 'tidy'; the linters are 'default'

def total = 1
```

To fix it, write the directive as the message describes.

# NYX0010: Directive in a package

A file that is part of a package has a `::` directive. Directives are for a script, which has no manifest; in a
package, the manifest says these things.

```nanyx
-- file: main.nyx
::linters.default  -- error NYX0010: ::linters.default is a directive for a script, but this file is in a package: say it in the package's manifest, .../app.nyxp

module main

export def total = 1
-- file: app.nyxp
[package]
name = "app"
version = "0.1.0"
sources = ["*.nyx"]
```

To fix it, move the setting to the package's manifest, and remove the directive.

# NYX0011: Unsupported directive

A `::` directive is one the compiler knows, but does not support in a script yet.

```nanyx
::dependencies.acme.json = "1.2"  -- error NYX0011: ::dependencies is not supported yet: ...

def total = 1
```

To fix it, make the script a package, whose manifest can name its dependencies.

# NYX0012: Code in a module block

A module block (`module name =`) holds code to run. A block can only hold definitions, types and imports; code to run
belongs at the top level of the file, or in a definition.

```nanyx
module money =
  export def zero = 0
  zero + 1  -- error NYX0012: Module 'money' is a block, which can only hold definitions, types and imports, not code to run
```

To fix it, move the code out of the block, or make it a definition.

# NYX0013: Unexpected attribute argument

An attribute that takes no argument is given one.

```nanyx
@entry(main)  -- error NYX0013: Attribute '@entry' takes no argument
def main = { () }
```

To fix it, remove the argument.

# NYX0014: File-path import without an alias

A file is imported by its path without `as` and a name. The names a file exports are reached through its alias.

```nanyx
-- file: main.nyx
import "./helpers"  -- error NYX0014: An import of a file path must have an alias: write import "./helpers" as helpers

def total = 1
-- file: helpers.nyx
export def one = 1
```

To fix it, add the alias the message suggests.

# NYX0015: Named module imported by file path

A file that declares its module's name (`module helpers`) is imported by its path. A named module is imported by its
name.

```nanyx
-- file: main.nyx
import "./helpers" as h  -- error NYX0015: './helpers' declares the module name 'helpers', so it must be imported by its name, not by file path: write import helpers

def total = h.one
-- file: helpers.nyx
module helpers

export def one = 1
```

To fix it, import the module by its name, as the message suggests.

# NYX0016: Entrypoint imported

An imported file has top-level statements, which makes it a program to run rather than a module to share.

```nanyx
-- file: main.nyx
import "./setup" as setup  -- error NYX0016: './setup' has top-level statements, so it is an entrypoint and cannot be imported: ...

def total = setup.one
-- file: setup.nyx
import nanyx/console

export def one = 1

println("setting up")
```

To fix it, move the definitions to share into a file of their own, with no top-level statements, and import that.

# NYX0017: Script in a package

A file of a package is written as a script: it has no `module` line, or it has top-level statements. In a package,
every file is a module, and the program starts at the package's `@entry` definition.

```nanyx
-- file: main.nyx
def total = 1  -- error NYX0017: 'main.nyx' is in the package 'app', so it must declare a module name: start it with module main
-- file: app.nyxp
[package]
name = "app"
version = "0.1.0"
sources = ["*.nyx"]
```

To fix it, start the file with the `module` line the message suggests, and move top-level statements into a
definition.

# NYX0018: Module declared by more than one file

Two files of a package declare the same module. A module can be split across files only when each file says so.

```nanyx
-- file: main.nyx
module main

import shapes

@entry
def run = { () }
-- file: a.nyx
module shapes  -- error NYX0018: 'a.nyx' declares the module 'shapes', which 'b.nyx' declares too: ...

export def one = 1
-- file: b.nyx
module shapes

export def two = 2
-- file: app.nyxp
[package]
name = "app"
version = "0.1.0"
sources = ["*.nyx"]
```

To fix it, rename one of the modules, or start each file with `@partial module shapes` to make them parts of one
module ([Modules](../tour/modules)).

# NYX0101: Unknown identifier

A name is used that nothing in scope defines. When a name in scope is spelled closely enough, the message suggests
it, and an editor offers to write it. When a standard library module exports the name, an editor offers to import
it.

```nanyx
def total = 1
def doubled = totl * 2  -- error NYX0101: Unknown identifier 'totl': did you mean 'total'?
```

To fix it, correct the name, define it, or import the module that exports it.

# NYX0102: Unknown function

A function is called that nothing in scope defines, or a value is piped to a name that is neither a function in
scope nor attached to the value's type ([Piping](../tour/piping)).

```nanyx
def loud = shout("hi")  -- error NYX0102: Unknown function 'shout'
```

To fix it, correct the name, define the function, or import the module that exports it.

# NYX0103: Unknown member

A record is read or updated with a field it does not have.

```nanyx
def point = (x = 1, y = 2)
def moved = point with (z = 3)  -- error NYX0103: Record has no member 'z' to update
```

To fix it, use one of the record's fields, or add the field to its type.

# NYX0104: Not exported

A name is used from a module that does not export it. Only what a module marks `export` can be used outside it.

```nanyx
module money =
  export def zero = 0
  def secret = 42

def leaked = money.secret  -- error NYX0104: 'money.secret' is not exported from module 'money'
```

To fix it, mark the definition `export` in its module, or use something the module exports.

# NYX0105: Ambiguous name

A name is exported by more than one of the modules a file imports, so the compiler cannot tell which is meant.

```nanyx
-- file: main.nyx
import english
import french

def greeting = hello  -- error NYX0105: 'hello' is ambiguous between 'english.hello' and 'french.hello'; import all but one of them with 'as'
-- file: english.nyx
module english

export def hello = "hello"
-- file: french.nyx
module french

export def hello = "bonjour"
```

To fix it, import all but one of the modules with `as`, and reach their names through the alias.

# NYX0106: Ambiguous attached function

A value is piped to a name that more than one imported module attaches to its type
([Attached definitions](../advanced/attached-definitions)).

```nanyx
-- file: main.nyx
import loud
import louder

def shouted = "hi" \shout  -- error NYX0106: 'shout' is ambiguous for 'string': 'string.shout' is defined in 'loud' and 'louder'; ...
-- file: loud.nyx
module loud

export def string.shout: string -> string = { s -> s ++ "!" }
-- file: louder.nyx
module louder

export def string.shout: string -> string = { s -> s ++ "!!" }
```

To fix it, import all but one of the modules with `as`.

# NYX0107: Unknown attached type

A definition is attached to a type (`def Shape.area`) that is not defined.

```nanyx
def Shape.area: int -> int = { x -> x }  -- error NYX0107: Unknown attached type 'Shape' for definition 'Shape.area'
```

To fix it, define the type, or correct its name.

# NYX0108: Unknown context

A definition's type requires a context that is not defined, or requires an operator that no context in the prelude
has.

```nanyx
def greet: [Telegraph] () -> () = { () }  -- error NYX0108: Unknown context 'Telegraph' in the signature of 'greet'
```

To fix it, correct the context's name, define it, or import the module that exports it.

# NYX0109: Unknown type

A type is named that is not defined.

```nanyx
def name: Strng = "Ada"  -- error NYX0109: Unknown type 'Strng'
```

To fix it, correct the type's name, define it, or import the module that exports it.

# NYX0110: Name from outside a module block

A module block (`module name =`) uses a name defined or imported outside it. A block sees only its own definitions
and what it imports itself.

```nanyx
def rate = 3

module prices =
  export def price = rate * 100  -- error NYX0110: 'rate' is defined outside module 'prices', which sees only its own definitions and what it imports
```

To fix it, move the definition into the block, or into a block of its own that this one imports.

# NYX0111: Not implemented

The program has `???`, which stands for code not written yet. It type-checks as any type, so the rest of the program
can be checked, but it cannot be run in a release build.

```nanyx
def total: int -> int = { x -> ??? }  -- error NYX0111: Not implemented: `???` stands for code not written yet
```

To fix it, write the code.

# NYX0113: Duplicate definition

A module defines a name twice.

```nanyx
def total = 1
def total = 2  -- error NYX0113: The name 'total' is already defined in this module
```

To fix it, rename or remove one of the definitions.

# NYX0114: Not a built-in type

A type is written `Int`, `Float` or `Bool`. Those are not types: the built-in types are `int`, `float` and `bool`. An
editor offers to write the right name.

```nanyx
def count: Int = 3  -- error NYX0114: 'Int' is not a type: write 'int'
```

To fix it, write the type in lower case.

# NYX0115: Initialisation cycle

Definitions that are values, not functions, need each other's values to be built. Each value is built once, before
the program runs, so one of them would be read before it has a value.

```nanyx
def a: int = b + 1  -- error NYX0115: 'a' needs its own value to be built, through 'b'
def b: int = a + 1
```

To fix it, break the cycle: build one of the values without the other, or make one of them a function.

# NYX0201: Type mismatch

_To be written._

# NYX0202: Annotation too general

_To be written._

# NYX0203: Non-exhaustive match

_To be written._

# NYX0204: Missing catch-all arm

_To be written._

# NYX0205: Missing context

_To be written._

# NYX0206: Invalid workflow

_To be written._

# NYX0207: Workflow keyword outside a workflow

_To be written._

# NYX0208: Invalid built-in call

_To be written._

# NYX0209: Invalid record update

_To be written._

# NYX0210: Misplaced typeof

_To be written._

# NYX0211: Undeclared context

_To be written._

# NYX0212: Nominal type constructed outside its module

_To be written._

# NYX0213: Wrong number of patterns

_To be written._

# NYX0214: Guard pattern is not a predicate

_To be written._

# NYX0215: Name bound twice in a pattern

_To be written._

# NYX0216: Misplaced type hole

_To be written._

# NYX0217: Endless for loop

_To be written._

# NYX0218: Named parameter passed by position

_To be written._

# NYX0219: Definition cannot be attached

_To be written._

# NYX0220: Block without a result

_To be written._

# NYX0221: mut outside a memory block

_To be written._

# NYX0222: Assignment to a non-cell

_To be written._

# NYX0223: Cell assignment type mismatch

_To be written._

# NYX0226: Unbound context variable

_To be written._

# NYX0227: Function requires contexts not allowed here

_To be written._

# NYX0228: Type alias refers to itself outside a tag

_To be written._

# NYX0229: Literal out of range

_To be written._

# NYX0230: Option-like right operand of ??

_To be written._

# NYX0231: Invalid record spread

_To be written._

# NYX0232: Scoped context is not nominal

_To be written._

# NYX0233: Scoped context escapes the call that supplies it

_To be written._

# NYX0234: mut argument is not a cell

_To be written._

# NYX0235: Cell type inside another type

_To be written._

# NYX0236: Context on a type no parameter or result has

_To be written._

# NYX0301: Not supported by the WebAssembly backend

_To be written._

# NYX0302: Internal error in the WebAssembly backend

_To be written._
