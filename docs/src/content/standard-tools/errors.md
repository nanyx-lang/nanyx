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

# NYX0116: Invalid type name

A type or a context is defined with a name that does not start with a capital letter, or a lowercase name longer than
one letter is used in a type. In a type, a lowercase name is a type variable if it is one letter, such as `a`, or a
built-in type, such as `int` or `list`; longer lowercase names are reserved for associated types
([Type names](../tour/types#type-names)).

```nanyx
type point = (x: int, y: int)  -- error NYX0116: 'point' cannot be a type's name, which starts with a capital letter: write 'Point'
```

To fix it, start the type's name with a capital letter: `Point`, `Money`.

# NYX0201: Type mismatch

A value's type is not the type its place needs: an argument is not what the function takes, a definition's value is
not the type its annotation says, or two branches give back different types. The message names the two types.

```nanyx
def count: int = "three"  -- error NYX0201: Type mismatch: string vs int.
```

To fix it, change the value, or the annotation, so that the two types agree. Where the types come from far apart, an
annotation on a definition in between shows which of them is wrong.

# NYX0202: Annotation too general

A definition's annotation promises more than its body gives: it says a type variable could be any type, but the body
needs a particular type, or the same type as another variable.

```nanyx
def same: a -> b = { x -> x }  -- error NYX0202: Annotation of 'same' is more general than the inferred type: `b` would have to be `a`
```

To fix it, write the type the body has (here `a -> a`), or leave the annotation out and let the type be inferred.

# NYX0203: Non-exhaustive match

A `match` has no arm for some of the values it can be given. The message names what is missing, and an editor offers
to add the missing arms ([Pattern matching](../tour/pattern-matching)).

```nanyx
def describe: #some(int) | #none -> string = { o ->
  match o  -- error NYX0203: Non-exhaustive match, missing tags: none
    | #some(n) -> "some"
}
```

To fix it, add an arm for each missing case, or an arm `_` for everything not matched yet.

# NYX0204: Missing catch-all arm

A `match` is on a tag union with a rest, such as `#some(a) | r`, which stands for tags the function does not know.
No list of arms can name them all, so the match needs an arm that takes anything.

```nanyx
def mapSome: (#some(a) | r), (a -> b) -> #some(b) | r = { o, f ->
  match o  -- error NYX0204: Tag union rest parameters require a catch-all match arm
    | #some(v) -> #some(f(v))
}
```

To fix it, add a last arm with a name or `_` as its pattern, such as `| other -> other`.

# NYX0205: Missing context

An operation needs a context that the type of its value does not have: two values are compared with `<` but their
type has no order, are compared with `==` but contain functions, or are used as a map's keys but cannot be hashed.
Where the type is a type variable of the definition, the message says which context to add to its type, and an
editor offers to add it.

```nanyx
def less = ["a" => 1] < ["a" => 1]  -- error NYX0205: 'map(string, int)' cannot be ordered with <: maps have no order
```

To fix it, use values whose type has the context, give a nominal type the definition it needs (such as `==`), or add
the context to the definition's type, as the message says ([Contexts](../advanced/contexts)).

# NYX0206: Invalid workflow

A builder block, such as `opt { ... }`, is not one its builder can run: the builder lacks a member the block needs,
or a workflow is called without the trailing lambda that is its block ([Workflows](../advanced/workflows)).

```nanyx
def opt = (
  try = {
    | #some(value), continue -> continue(value)
    | failure, _ -> failure
  }
  return = { value -> #some(value) }
)
def half: int -> #some(int) | #odd = { n -> if n % 2 == 0 then #some(n / 2) else #odd }
def show = { n -> opt {
  def h = try half(n)
  h + 1  -- error NYX0206: This `opt` block can end here without `return`, and `opt` has no `zero` to be its value then: ...
} }
```

To fix it, do what the message says: here, end the block with `return`, or give the builder a `zero`.

# NYX0207: Workflow keyword outside a workflow

A workflow keyword, such as `try` or `return`, is used outside a builder block. It means something only to the
builder that runs the block.

```nanyx
def halve: int -> #some(int) | #none = { n -> if n % 2 == 0 then #some(n / 2) else #none }

def quarter = { n ->
  def h = try halve(n)  -- error NYX0207: `try` is a workflow keyword, so it can only be used inside a builder block, such as `proc { ... }`
  halve(h)
}
```

To fix it, put the code in a builder block, such as `proc { ... }`, or match on the value instead.

# NYX0208: Invalid built-in call

A built-in, such as `dbg`, `not`, `with` or a loop, is given the wrong number or kind of arguments.

```nanyx
def x = dbg()  -- error NYX0208: dbg expects a single argument
```

To fix it, give the built-in what the message says it expects.

# NYX0209: Invalid record update

A `with` update cannot be made: the record's fields are not known where it is written, or a field is given by
position rather than by name ([Records](../tour/records)).

```nanyx
def point = (x = 1, y = 2)
def moved = point with (3)  -- error NYX0209: `with` updates fields by name
```

To fix it, name the fields to update, as in `point with (x = 3)`, and annotate the record's type if it is not known.

# NYX0210: Misplaced typeof

`typeof` is used in the definition of a type. It can only be used in the type of a value.

```nanyx
def defaults = (port = 8080)
type Config = typeof defaults  -- error NYX0210: 'typeof' cannot be used in the definition of the type 'Config', only in the type of a value
```

To fix it, write the type out in the type definition.

# NYX0211: Undeclared context

A definition's body needs a context that its type does not declare: it calls a function that requires one, such as
`println`, which requires `Console`. An editor offers to add the context to the type.

```nanyx
import nanyx/console

def greet: string -> () = { name -> println("Hello, {name}") }  -- error NYX0211: 'println' requires the context 'Console', which 'greet' does not declare: ...
```

To fix it, add the context to the definition's type, as in `[Console] string -> ()`, or load an instance with `use`
([Contexts](../advanced/contexts)).

# NYX0212: Nominal type constructed outside its module

A nominal type, one declared `protected` or `private`, is constructed, or a private one taken apart, outside the
module that defines it. Only its own module can make its values ([Nominal types](../advanced/nominal-and-protected-types)).

```nanyx
module money =
  export type Money = protected (pence: int)

import money

def price = Money(pence = 250)  -- error NYX0212: The nominal type 'Money' can only be constructed in the module that defines it; ...
```

To fix it, make the value with a function the module exports, such as `Money.new`.

# NYX0213: Wrong number of patterns

An arm of a match on several values, such as a lambda's parameters or a loop over several lists, has a different
number of patterns from the number of values.

```nanyx
def first = { a, b -> | x -> x }  -- error NYX0213: This arm has 1 pattern, but the match is on 2 values: ...
```

To fix it, give the arm a pattern for each value, as in `| x, _ -> x`.

# NYX0214: Guard pattern is not a predicate

A guard pattern, a lambda written as a pattern, does not take exactly one parameter. A guard is a test of the value
matched.

```nanyx
def check: int -> int = { n ->
  match n
    | { a, b -> a > b } -> 1  -- error NYX0214: A guard pattern is a predicate on the value matched, ...
    | _ -> 0
}
```

To fix it, write a lambda of one parameter, such as `{ x -> x > 0 }` or `{ > 0 }`.

# NYX0215: Name bound twice in a pattern

Two parts of an and-pattern (`p & q`) bind the same name.

```nanyx
def pick: (int, int) -> int = { p ->
  match p
    | (a, _) & (_, a) -> a  -- error NYX0215: The name 'a' is bound by more than one part of this pattern; ...
}
```

To fix it, give each part its own names.

# NYX0216: Misplaced type hole

`_`, a type hole that asks the compiler to infer that part of a type, is used in the definition of a type. A type
hole can only be used in the type of a value.

```nanyx
type Names = list(_)  -- error NYX0216: '_' cannot be used in the definition of the type 'Names', only in the type of a value
```

To fix it, write the type out.

# NYX0217: Endless for loop

A `for` loop walks only `gen`s, such as the open range `0..`, which never end, so the loop would never end either
([Loops](../tour/loops)).

```nanyx
def count = for 0.. | i -> i  -- error NYX0217: A `for` loop over a `gen` alone may never end: ...
```

To fix it, walk the `gen` in step with a list or a bounded range, as in `for xs, 0.. | x, i -> ...`.

# NYX0218: Named parameter passed by position

A function whose parameters are named, as in `(width: int, height: int) -> int`, is called with its arguments by
position. When each argument fits just one parameter, an editor offers to name them.

```nanyx
def area: (width: int, height: int) -> int = { width, height -> width * height }

def size = area(3, 4)  -- error NYX0218: 'area' takes 'height' and 'width' by name, not by position: write 'height = ...' and 'width = ...'
```

To fix it, name the arguments, as in `area(width = 3, height = 4)`.

# NYX0219: Definition cannot be attached

A definition is attached to a type that cannot have it: `hash`, `compare` or `==` attached to a structural type, whose hash,
order and equality are always those of its structure, or `!=`, which is always `not (a == b)`.

```nanyx
type Point = (x: int, y: int)
def Point.hash: Point -> int = { p -> p.x }  -- error NYX0219: 'Point.hash' cannot be defined: 'Point' is a structural type, ...
```

To fix it, make the type nominal (`Point`) to give it its own definition, or attach `==` instead of `!=`.

# NYX0220: Block without a result

A block ends with a definition, so it has no value to give back.

```nanyx
def total = {
  def x = 1  -- error NYX0220: This block ends with the definition of 'x', so it has no value to give back. ...
}
```

To fix it, end the block with the expression whose value it gives back, or with `()` if it gives back nothing.

# NYX0221: mut outside a memory block

A cell is declared with `mut` outside a `memory { ... }` block. A cell needs the context `Memory`, which only a
memory block provides ([Memory](../advanced/memory)).

```nanyx
def count = {
  mut x = 0  -- error NYX0221: `mut` needs the context 'Memory', which only a `memory { ... }` block provides: ...
  x
}
```

To fix it, write the code inside a `memory { ... }` block.

# NYX0222: Assignment to a non-cell

`:=` changes a name that is not a cell. Only a name declared with `mut` can be changed.

```nanyx
def count = memory {
  def x = 0
  x := 1  -- error NYX0222: 'x' is not a cell, so `:=` cannot change it: only a name declared with `mut` is one
  x
}
```

To fix it, declare the name with `mut`, or define a new name for the new value.

# NYX0223: Cell assignment type mismatch

A value of one type is assigned to a cell of another. A cell holds values of one type.

```nanyx
def count = memory {
  mut x = 0
  x := "one"  -- error NYX0223: Type mismatch: string vs int.
  x
}
```

To fix it, assign a value of the cell's type.

# NYX0226: Unbound context variable

A context variable, which stands for any contexts, as `c` does in `[c] int -> int`, is used in a definition's type
but is not among the contexts of the definition itself.

```nanyx
def g: [d] ([c] int -> int) -> int = { f -> 1 }  -- error NYX0226: The context variable 'c' is not bound: add it to the contexts of 'g', as in '[c] ...'
```

To fix it, add the variable to the definition's own contexts, as in `[c] ([c] int -> int) -> int`.

# NYX0227: Function requires contexts not allowed here

A function that requires a context is given where a function that requires none is expected: passed to a function
whose parameter type writes no contexts, or returned from a definition whose result type writes none.

```nanyx
import nanyx/console

def runPure: (int -> int) -> int = { g -> g(5) }

def pass: ([Console] int -> int) -> int = { f -> runPure(f) }  -- error NYX0227: A function that requires the context 'Console' is given where 'int -> int' is expected, ...
```

To fix it, write the contexts the function may require on the type expected, as in `([Console] int -> int)`, or a
context variable for any, as in `[c] (int -> int)`.

# NYX0228: Type alias refers to itself outside a tag

A type alias names itself other than in the payload of one of its tags, so the type would have no end.

```nanyx
type Pair = (int, Pair)  -- error NYX0228: The type 'Pair' refers to itself outside a tag: ...
```

To fix it, put the recursion inside a tag, as in `type List = #empty | #cons(int, List)`.

# NYX0229: Literal out of range

A number literal does not fit in its type, such as `300` for a `U8` ([Sized numbers](../advanced/sized-numbers)).

```nanyx
def small: U8 = 300  -- error NYX0229: '300' does not fit in U8 (0 to 255)
```

To fix it, use a value in the type's range, or a larger type.

# NYX0230: Option-like right operand of ??

The right operand of `??` is itself option-like. `??` only unwraps: the right operand is the value to use when the
left is not `#some` ([Option-like types](../advanced/option-like-types)).

```nanyx
def lookup: string -> #some(int) | #missing = { k -> #missing }
def x = lookup("a") ?? lookup("b")  -- error NYX0230: `??` only unwraps: ...
```

To fix it, fall back to another option with `orElseSome`, as in `lookup("a") \orElseSome { lookup("b") }`.

# NYX0231: Invalid record spread

A spread, `...r`, is written of a value whose fields are not known where it is written.

```nanyx
def f = { r -> (...r, a = 1) }  -- error NYX0231: a spread (`...`) needs a record whose fields are known here
```

To fix it, annotate the value's type, so that its fields are known.

# NYX0232: Scoped context is not nominal

A `scoped context` is not declared `protected`. A scoped context must be nominal, so that only its own module can
make its instances.

```nanyx
scoped context Tx = (commit: () -> ())  -- error NYX0232: The scoped context 'Tx' must be nominal, declared `scoped context Tx = protected (...)`, ...
```

To fix it, declare the context `protected` after its `=`, as in `scoped context Tx = protected (...)`.

# NYX0233: Scoped context escapes the call that supplies it

Something that requires a scoped context, such as `Memory`, leaves the call that supplies the context: a memory
block returns its instance, or a function over one of its cells.

```nanyx
def leak = memory { m -> m }  -- error NYX0233: The result of this 'memory' call requires its 'Memory' through 'm', so it cannot leave the call: ...
```

To fix it, give back what the cells hold, rather than the cells or a function over them.

# NYX0234: mut argument is not a cell

An argument is passed with `mut`, as a cell, but it is not one.

```nanyx
def inc = { mut cell -> cell := cell + 1 }
def total = memory {
  def count = 0
  inc(mut count)  -- error NYX0234: 'count' is not a cell, so `mut count` cannot pass it: ...
  count
}
```

To fix it, declare the name with `mut`.

# NYX0235: Cell type inside another type

A cell's type, `mut T`, is written inside another type, such as `list(mut int)`. For now a cell's type can only be a
function's parameter: a cell can be passed to a function, but nothing can hold one.

```nanyx
def bad: list(mut int) -> () = { xs -> () }  -- error NYX0235: A cell's type, `mut T`, can only be a function's parameter for now: ...
```

To fix it, pass the cell itself, or the value it holds.

# NYX0236: Context on a type no parameter or result has

A definition's type requires a context of a type variable, as `ToString(a)` does, but no parameter and no result has
that type, so no call could say which type it is.

```nanyx
context Log = (log: string -> ())

def greet: [Log & ToString(a)] string -> () = { name -> log("hello {name}") }  -- error NYX0236: 'a' in 'ToString(a)' is not the type of any parameter of 'greet' or of its result, ...
```

To fix it, remove the context, or give a parameter the type it is on.

# NYX0301: Not supported by the WebAssembly backend

The program type-checks, but uses something the WebAssembly backend cannot compile yet. The message names what it
is.

```nanyx
type Nest(a) = #leaf(a) | #deeper(Nest(list(a)))

def depth: Nest(int) -> int = { t -> 1 }  -- error NYX0301: The WebAssembly backend cannot compile a recursive type that holds itself at other type arguments yet
```

To fix it, write the code another way until the backend supports it.

# NYX0302: Internal error in the WebAssembly backend

The WebAssembly backend found something it should never be given. This is a bug in the compiler, not in the program.

Please [report it](https://github.com/nanyx-lang/nanyx/issues), with the program that causes it.