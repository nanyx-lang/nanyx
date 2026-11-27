---
title: "Compiler errors"
description: "Every error the compiler reports, by code: when it is reported and how to fix it"
order: 4
---

Every error the compiler reports has a code, such as `NYX0201`, and a headline. The headline is the code's title, such
as "Type mismatch", the same for every error with that code, and for some codes the one name the error is about, in
single quotes: `Missing context 'Console'`, `Unknown identifier 'totl'`. Most of the time you have seen the error
before, and the headline is enough to know it. The body after it gives the details, and any advice after a colon. An
editor shows the headline, then the body on the next line, and links the code to its section on this page.

The command line shows the code and headline first, then the file, line and column the error is at, the line of source
with the code it is about underlined, and then the body:

```text
error NYX0201: Type mismatch
  --> shapes.nyx:3:39
   |
 3 | def perimeter: int -> int = { side -> side * "4" }
   |                                       ^^^^^^^^^^
   = 'string' is not a number, so it cannot be used with *
```

On a terminal, the headline and the underline are coloured, unless the `NO_COLOR` environment variable is set.

Some errors are explained by a second place in the code, a related location: the first definition of a name defined
twice, or the declaration of a name that is private. The command line shows each one after the body, under a note
that says what it is, with its own line of source underlined:

```text
error NYX0113: Duplicate definition 'total'
   --> main.nyx:10:5
    |
 10 | def total = 2
    |     ^^^^^
    = The name 'total' is already defined in this module
    = note: 'total' is first defined here
   --> main.nyx:1:5
    |
  1 | def total = 1
    |     ^^^^^
```

An editor links to it from the error, as related information.

Where the compiler can tell what was meant, the body says so. An unknown name is followed by the names in scope closest
to it (`'totl' is not in scope: did you mean 'total'?`), and a character that only looks like one Nanyx reads, such as
U+2212 for `-`, is named. In an editor, these and other errors come with a fix that makes the edit for you: writing the
name suggested, importing the standard library module that exports an unknown name, adding the arms a match is
missing, adding a context a definition needs to its type, naming the arguments of a call that passes named parameters
by position, or writing the character a look-alike stands for.

Most errors, such as type errors and unknown names, don't stop a debug build: see
[Deferred errors](../tour/error-handling#deferred-errors).

One code is a warning, not an error: [`NYX0051`](#nyx0051), a version that has been unlisted, which the build goes on
with. The command line shows it as `warning NYX0051`.

A linter's diagnostics are not on this page. Their codes name the linter and the rule, such as `default/unusedBindings`
or `default/repeatedPulls` (a sequence whose steps require contexts, pulled more than once), and the official linter's
rules are described on the [Official style linter](./official-linter) page.

The codes are grouped by what the compiler is doing when it finds the error:

- `NYX00xx`: reading a file, its directives and its imports.
- `NYX01xx`: resolving the names a program uses.
- `NYX02xx`: checking types.
- `NYX03xx`: generating WebAssembly.

Each section shows a short program that reports the error, with the message in a comment on the line it is reported
at. Where an example takes more than one file, each file starts with a `-- file:` line naming it, and the first
file is the one compiled.

# NYX0001: Syntax error

The file could not be parsed: the text at the position shown is not what the grammar allows there. The headline names
the token the parser found there, as in "Syntax error '`]`'", and the message what it expected instead, as in
"Expected `)`". At the end of the file the message says so, as in "Expected `)`, found the end of the file". Other
messages say what the mistake is and what to write instead.

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
import "./helpers" as helpers  -- error NYX0003: There is no file at this path, which is read from the importing file's directory

def total = 1
```

To fix it, correct the path or the module's name, or create the file. An import of a shipped package's module by the
package's old name, from before the shipped packages were renamed ([`NYX0054`](#nyx0054)), says what to import now:
`import nanyx.console.core/env` is `nanyx.cli/env`.

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
import "./a" as a  -- error NYX0004: '.../a.nyx' imports this module, directly or through others, so the imports make a cycle

export def two = 2
```

To fix it, move what the modules share into a module that neither of them imports.

# NYX0005: Unknown standard library module

An import that starts with `nanyx/` names a module the standard library does not have. The message lists the
modules it has.

```nanyx
import nanyx/strings  -- error NYX0005: The modules are ...

def total = 1
```

To fix it, import one of the modules the message lists. The [standard library](../stdlib/standard-library) describes them.

# NYX0006: Invalid standard library module

A module of the standard library is broken: it declares a different module name from the one it is imported by, it
imports something other than the standard library, or the prelude's contexts share a member in a way that leaves an
operator with no one context to be read through
([Operators find their context](../advanced/contexts#operators-find-their-context)).

A program cannot cause this error. It is reported when the standard library itself is changed, and it is fixed there.

# NYX0007: Unknown attribute

A definition is marked with an attribute the compiler does not know, or a local def with one other than `@allow`,
the only attribute a block can hold. The attributes are listed in [Attributes](../tour/attributes).

```nanyx
@inline  -- error NYX0007: A definition can have '@entry', '@extern', '@component', '@import' or '@allow'
def double: int -> int = { x -> x * 2 }
```

To fix it, correct the attribute's name, or remove it.

# NYX0008: Misplaced attribute

An attribute is written where it can't be. An attribute line must be followed by a definition, and an attribute in a
type can only come before a field of a record type's definition or a member of a context declaration.

```nanyx
def double: int -> int = { x -> x * 2 }

@entry  -- error NYX0008: It must be followed by a definition
```

To fix it, write the attribute on the line before the definition or context member it marks.

# NYX0009: Invalid directive

A `::` directive at the top of a script is not one the compiler understands, or a word after its name is not one the
directive takes: a package's name missing, a word that is neither a version nor a directory, or a word too many.
Directives say for a script what a package's manifest would say ([Packages](../tour/packages)).

```nanyx
::install acme.util latest  -- error NYX0009: 'latest' is neither a version, which starts with a digit, as `1.2.0`, nor a directory, which starts with `.` or `/`, as `../util`

def total = 1
```

To fix it, write the directive as the message describes: here, the directory the package is in, `../util`.

# NYX0010: Directive in a package

A file that is part of a package has a `::` directive. Directives are for a script, which has no manifest; in a
package, the manifest says these things.

```nanyx
-- file: main.nyx
::linter nanyx.lint  -- error NYX0010: `::linter` is a directive for a script, but this file is in a package: say it in the package's manifest, '.../app.nyxp'

module main

export def total = 1
-- file: app.nyxp
(package = (name = "app", version = "0.1.0", sources = ["*.nyx"]))
```

To fix it, move the setting to the package's manifest, and remove the directive.

# NYX0012: Code in a module block

A module block (`module name =`) holds code to run. A block can only hold definitions, types and imports; code to run
belongs at the top level of the file, or in a definition.

```nanyx
module money =
  export def zero = 0
  zero + 1  -- error NYX0012: 'money' is a module block, which can only hold definitions, types and imports, not code to run
```

To fix it, move the code out of the block, or make it a definition.

# NYX0013: Unexpected attribute argument

An attribute that takes no argument is given one, or one that takes an argument is given one it can't take: `@allow`
takes the linter rules it allows, such as `@allow(default/unusedBindings)`.

```nanyx
@entry(main)  -- error NYX0013: It takes no argument
def main = { () }
```

To fix it, remove the argument, or write the one the attribute takes.

# NYX0014: File-path import without an alias

A file is imported by its path without `as` and a name. The names a file exports are reached through its alias.

```nanyx
-- file: main.nyx
import "./helpers"  -- error NYX0014: An import of a file path needs an alias for what the file exports: write `import "./helpers" as helpers`

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
import "./helpers" as h  -- error NYX0015: './helpers' declares the module name 'helpers', so it is imported by its name, not by file path: write `import helpers`

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
export def one = 1

println("setting up")
```

To fix it, move the definitions to share into a file of their own, with no top-level statements, and import that.

# NYX0017: Script in a package

A file of a package is written as a script: it has no `module` line, or it has top-level statements. In a package,
every file is a module, and the program starts at the package's `@entry` definition.

```nanyx
-- file: main.nyx
def total = 1  -- error NYX0017: 'main.nyx' is in the package 'app', so it must declare a module name: start it with `module main`
-- file: app.nyxp
(package = (name = "app", version = "0.1.0", sources = ["*.nyx"]))
```

To fix it, start the file with the `module` line the message suggests, and move top-level statements into a
definition.

# NYX0018: Module declared by more than one file

Two files of a package declare the same module. A module can be split across files only when each file says so.

```nanyx
-- file: main.nyx
module main

import shapes

def run = { () }
-- file: a.nyx
module shapes  -- error NYX0018: 'a.nyx' declares the module 'shapes', which 'b.nyx' declares too: ...

export def one = 1
-- file: b.nyx
module shapes

export def two = 2
-- file: app.nyxp
(package = (name = "app", version = "0.1.0", sources = ["*.nyx"]))
```

To fix it, rename one of the modules, or start each file with `@partial module shapes` to make them parts of one
module ([Modules](../tour/modules)).

# NYX0019: Unknown keyword kind

A workflow keyword's `@customKeywordType` names a kind the compiler does not know, or names none, or marks a def that is
attached to no context, or names a kind other than `#delay` before a def attached to one. The kinds are listed in
[Workflows](../advanced/workflows#keyword-kinds).

```nanyx
context Lazy = (
  @customKeywordType(#later)  -- error NYX0019: The kinds are `#bind`, `#bindF`, `#delay`, `#yield`
  force: [c] (int, ([c] int -> int)) -> int
)
```

A keyword belongs to a context, where it is in scope wherever the context is. A `#delay` keyword rewrites nothing but
itself, so a def attached to a context can be one, as `nanyx/async`'s `Async.start` is; a def attached to none can't:

```nanyx
@customKeywordType(#delay)  -- error NYX0019: A keyword belongs to a context: ...
def step: (() -> int) -> int = { work -> work() }
```

Any other kind needs a workflow's builder, so only a member of the builder's context can be one.

To fix it, write a kind from the list, as `@customKeywordType(#bind)`; attach a `#delay` def to the context it belongs
to, as `def Async.start`; or make the keyword a member of its builder's context.

# NYX0020: Code in a manifest

A package's `.nyxp` manifest holds code, or is empty. A manifest is one record written as data: record, list and
tuple literals, strings, numbers, `true` and `false`, and tags such as `#error`
([Packages](../tour/packages#package-manifests)). A name, a call, an operator, a lambda, an interpolated string, a
definition, an import or a `::` directive is code. A manifest that is not Nanyx at all is a syntax error, `NYX0001`.

```nanyx
-- file: demo.nyxp
(
  package = (name = "demo", version = "0.1.0")
  formatter = (lineWidth = 80 + 20)  -- error NYX0020: The operator `+` is code, but this file holds only data
)
```

To fix it, write the value itself: `lineWidth = 100`.

# NYX0021: Unknown manifest field

A record in the manifest has a field it doesn't take: the manifest itself, its `package`, a linter or host entry, a
linter's `settings`, or its `rules`. The message lists the fields it takes.

```nanyx
-- file: demo.nyxp
(
  package = (name = "demo", version = "0.1.0", src = ["*.nyx"])  -- error NYX0021: Package has no field 'src': its fields are 'name', 'version', 'sources', 'tests', 'repository', 'nanyx', 'host', 'hostLibrary'
)
```

To fix it, correct the field's name, or remove it.

# NYX0022: Missing manifest field

The manifest has no `package`, its `package` has no `name` or `version`, or a linter or host entry has no `package`
or `version`.

```nanyx
-- file: demo.nyxp
(
  package = (name = "demo")  -- error NYX0022: Package has no version, which it needs
)
```

To fix it, add the field: `version = "0.1.0"`.

# NYX0023: Invalid manifest value

A field of the manifest has a value of the wrong kind, such as a string where a list is expected, or a value its rule
does not allow: a package name that isn't lower case with words separated by dots, a version that isn't a semantic
version, a severity that isn't `#error`, `#warning`, `#info`, `#hint` or `#off`, or a linter or host package the
compiler doesn't have. A package listed twice in `dependencies` is one too, at the repeat.

```nanyx
-- file: demo.nyxp
(
  package = (name = "demo", version = "0.1.0", sources = "*.nyx")  -- error NYX0023: Package's sources must be a list of strings, not the string "*.nyx"
)
```

To fix it, write the value the message asks for: `sources = ["*.nyx"]`.

# NYX0024: Manifest field not supported yet

The manifest, or a script's directives, use something the compiler knows but doesn't support yet, such as a dependency
on GitHub where nothing can fetch it.

To fix it, give the directory the package is in instead, as in `::install acme.util ../util`.

# NYX0025: Module not public

A module imports a module of a package it depends on that the package does not make public. Another package can import
only the modules a package declares with `export module`.

```nanyx
-- file: main.nyx
module main

import acme.util/secrets  -- error NYX0025: The module 'secrets' of the package 'acme.util' is not public, ...

def run = { () -> ignore(mark) }
-- file: app.nyxp
(
  package = (name = "app", version = "0.1.0", sources = ["*.nyx"])
  dependencies = [(package = "acme.util", path = "util")]
)
-- file: util/util.nyxp
(package = (name = "acme.util", version = "0.1.0"))
-- file: util/strings.nyx
export module strings

export def shout = { s: string -> s ++ "!" }
-- file: util/secrets.nyx
module secrets

export def mark = "!"
```

To fix it, import one of the package's public modules, or, if you maintain the package, declare the module with
`export module`.

# NYX0026: Package is not a direct dependency

A module imports a package that only one of its package's dependencies depends on. A package can import only the
packages its own manifest lists in `dependencies`.

```nanyx
-- file: main.nyx
module main

import acme.util/strings  -- error NYX0026: The package 'acme.util' is not a dependency of 'app', only of a package it depends on: ...

def run = { () -> ignore(shout("hi")) }
-- file: app.nyxp
(
  package = (name = "app", version = "0.1.0", sources = ["*.nyx"])
  dependencies = [(package = "acme.loud", path = "loud")]
)
-- file: loud/loud.nyxp
(
  package = (name = "acme.loud", version = "0.1.0")
  dependencies = [(package = "acme.util", path = "../util")]
)
-- file: loud/louder.nyx
export module louder

import acme.util/strings

export def louder = { s: string -> shout(shout(s)) }
-- file: util/util.nyxp
(package = (name = "acme.util", version = "0.1.0"))
-- file: util/strings.nyx
export module strings

export def shout = { s: string -> s ++ "!" }
```

To fix it, add the package to `dependencies` in your own manifest, with the same path as the dependency that uses it.

# NYX0027: Packages depend on each other in a cycle

A package depends on itself through the packages it depends on. Packages are built in order of their dependencies,
so there can't be a cycle.

```nanyx
-- file: a/a.nyxp
(
  package = (name = "acme.a", version = "0.1.0")
  dependencies = [(package = "acme.b", path = "../b")]  -- error NYX0027: Packages can't depend on each other in a cycle: 'acme.a' -> 'acme.b' -> 'acme.a'
)
-- file: a/main.nyx
export module main
-- file: b/b.nyxp
(
  package = (name = "acme.b", version = "0.1.0")
  dependencies = [(package = "acme.a", path = "../a")]
)
-- file: b/other.nyx
export module other
```

To fix it, move what both packages need into a third package that both depend on.

# NYX0028: Package reached by two paths

A build reaches the same package, by its name, in two different directories: one package depends on it in one place,
and another in another. A build has one version of each package.

```nanyx
-- file: app/app.nyxp
(
  package = (name = "app", version = "0.1.0")
  dependencies = [
    (package = "acme.util", path = "../util")
    (package = "acme.loud", path = "../loud")
  ]
)
-- file: app/main.nyx
export module main
-- file: loud/loud.nyxp
(
  package = (name = "acme.loud", version = "0.1.0")
  dependencies = [(package = "acme.util", path = "../copy")]  -- error NYX0028: The build reaches the package 'acme.util' by two paths, ...
)
-- file: loud/louder.nyx
export module louder
-- file: util/util.nyxp
(package = (name = "acme.util", version = "0.1.0"))
-- file: util/strings.nyx
export module strings
-- file: copy/util.nyxp
(package = (name = "acme.util", version = "0.1.0"))
-- file: copy/strings.nyx
export module strings
```

To fix it, make every package that depends on it give the same directory.

# NYX0029: Version not shipped

A manifest names a linter, such as `nanyx.lint`, at a version other than the one the compiler ships. The compiler runs
the linters it ships itself, at its own version, so a manifest names them at it exactly. A host or a package the compiler
ships, such as `nanyx.cli.host`, is a package from the package index like any other, named at any version it has.

```nanyx
-- file: demo.nyxp
(
  package = (name = "demo", version = "0.1.0")
  linters = [(package = "nanyx.lint", version = "0.1.0")]  -- error NYX0029: The compiler ships 'nanyx.lint' ..., not 0.1.0: ...
)
```

To fix it, write the version the message names, which `nanyx --version` prints too. `nanyx new` writes it for you.

# NYX0030: Definition without a value

A def at the top level of a module has a type and nothing after it. Only a foreign function, which the embedder
provides, is written that way, marked `@extern` (see [NYX0241](#nyx0241)).

```nanyx
def size: () -> int  -- error NYX0030: 'size' has no value: write `= value` after it, or mark it `@extern("module", "name")` to declare a foreign function
```

To fix it, write the value: `def size: () -> int = { -> 3 }`.

# NYX0031: Entry point in a library

A package whose manifest names no `host` is a library: other packages use its modules, and it has no program of its
own to run. An `@entry` in it is an error, from `nanyx check`, `nanyx run` and `nanyx build` alike, and running a
library is [NYX0045](#nyx0045).

```nanyx
-- file: main.nyx
module main

@entry  -- error NYX0031: 'main' is an entry point, but its package names no host, so it is a library, which has none: ...
def main = { () }
-- file: app.nyxp
(package = (name = "app", version = "0.1.0", sources = ["*.nyx"]))
```

To fix it, name the host the program runs under in the manifest, `host = (package = "nanyx.cli.host", version = "...")`
at the compiler's version, which `nanyx new` writes, or remove the `@entry` if the package is a library.

# NYX0032: Contexts joined with `&` or `+`

The contexts a function requires are separated by `,`: `[Eq(a), Hash(a)]`. An `&` or a `+` between two contexts is an
error.

```nanyx
def both: [Eq(a) & Hash(a)] a, a -> bool = { x, y -> x == y }  -- error NYX0032: Separate contexts with `,`, not `&`: `[Eq(a), Hash(a)]`
```

To fix it, write `,` in its place, which the error's fix does; `nanyx format` writes `,` too.

# NYX0033: Library that exports no module

A package whose manifest names no `host`, and which has no `@entry`, is a library: other packages use it by importing
its public modules. One that declares none with `export module` can't be used at all.

```nanyx
-- file: util.nyxp
(
  package = (name = "acme.util", version = "0.1.0", sources = ["*.nyx"])  -- error NYX0033: 'acme.util' is a library, since its manifest names no host, but it exports no module: ...
)
-- file: strings.nyx
module strings

export def shout = { s: string -> s ++ "!" }
```

To fix it, declare the modules other packages may import with `export module`: `export module strings`. An
application, which names its host, needn't export anything.

# NYX0034: Manifest written as TOML

A package's `.nyxp` manifest is a Nanyx record, not TOML. A manifest whose first line is a TOML
table header, `[package]`, or a key, `name = "shop"`, is reported as TOML, at that line. A source file of the package
reports it too, from `nanyx check`, `nanyx run` and the editor, since the package can't be read without its manifest.

```nanyx
-- file: src/main.nyx
module main

@entry
def main = { println("hi") }
-- file: shop.nyxp
[package]  -- error NYX0034: The manifest is a Nanyx record, `( package = (...) ... )`, not TOML
name = "shop"
version = "0.1.0"
```

To fix it, write the manifest as a record:

```nanyx fragment
(
  package = (name = "shop", version = "0.1.0")
  host = (package = "nanyx.cli.host", version = "...")
)
```

# NYX0035: Directive written in the old form

A script's directive is a name and words: `::host nanyx.cli.host`. A directive written as a manifest's dotted key with a
TOML value, `::host.nanyx.cli.host = "0.28.0"`, is an error.

```nanyx
::formatter.lineWidth = 80  -- error NYX0035: Directives are lines of words now, not a manifest's key and a TOML value: write `::format lineWidth 80`

def total = 1
```

To fix it, write it as the message says, which the error's fix does: `::linters.default` is `::linter nanyx.lint`,
`::linters.default.unusedBindings = "error"` is `::lint unusedBindings #error` under it, `::dependencies.acme.util =
{ path = "../util" }` is `::install acme.util ../util`, and `::formatter.lineWidth = 80` is
`::format lineWidth 80`.

# NYX0036: Lint setting with no linter above it

A `::lint` line sets a rule of the linter the nearest `::linter` above it turns on, and there is none above it.

```nanyx
::lint unusedBindings #error  -- error NYX0036: `::lint` sets a rule of the linter of the nearest `::linter` above it, but there is none: turn one on above it, `::linter nanyx.lint`

def total = 1
```

To fix it, turn the linter on above it, which the error's fix does with the official linter: `::linter nanyx.lint`.

# NYX0037: Character that looks like another

A character outside a string or a comment looks like one Nanyx reads, but isn't it. It is usually pasted from a web
page or a document, which writes a minus sign for `-`, curly quotes for `"` and a no-break space for a space, and in
most fonts it can't be told apart. The parser reports it in place of a syntax error, then reads on as if the character
it looks like were written, so the file's other syntax errors are still reported.

```nanyx
def expected = −7.0  -- error NYX0037: '−' (U+2212 Minus Sign) looks like `-` (Hyphen-Minus), but it isn't one: write `-`
```

To fix it, write the character it looks like, which the error's fix does. A pair of curly quotes is one error, and its
fix writes both. The characters it knows are the dashes and minus signs, curly quotes and primes, spaces other than the
space and characters that can't be seen, `×`, `÷`, `≤`, `≥`, `≠` and `→`, and fullwidth brackets and punctuation.

# NYX0038: GitHub reference not pinned to a commit

A dependency on GitHub, a package or one file, names a tag or a branch, or nothing, without the commit it means. What a
tag or a branch names can move, and two machines with the same manifest must build the same program, so a reference
is pinned by a full commit SHA: its `rev`, or a `commit` beside a tag or a branch.

```nanyx
-- file: demo.nyxp
(
  package = (name = "demo", version = "0.1.0")
  dependencies = [(package = "acme.web", github = "acme/web", rev = "v1.2.0")]  -- error NYX0038: 'acme/web' names "v1.2.0", which is not a full commit SHA, ...
)
```

To fix it, add the commit the tag or branch points at, `commit = "1f8ae8b3…"`, or write the commit as the `rev`. In an
editor, the error's fix looks the commit up and writes it in.

# NYX0039: GitHub reference in a package to publish

A package whose manifest has a dependency on GitHub, a package or one file, is packed or published. A published
package must keep building for as long as anything depends on it, and what is in someone else's repository can
disappear, so it may depend only on published packages. Building and running it is fine.

To fix it, depend on a published version of the package, or copy the file into the package.

# NYX0040: File referenced from GitHub that breaks a file's rules

A file a manifest depends on from GitHub, `(module = "retry", github = "acme/snippets/retry.nyx", ...)`, breaks a rule
for such a file. It declares a module name of its own, which it can't, since the manifest names it and no package can
export it; or it imports something other than the standard library, which it can't, since it has no manifest to say
what it depends on.

To fix it, depend on a version of the file without its `module` line or its other imports, or, if it needs more than
the standard library, on the package it comes from.
# NYX0041: Locked package not in the package cache

A dependency named by its version is the version `nanyx.lock` locks, unpacked in the package cache, and that version
isn't there: the lockfile is new, or the cache was cleared. The same goes for a dependency on GitHub, at the commit it
is pinned to, and for the version a script's `::install` pins, as in `::install acme.util 1.2.0`: a build reads
it from the cache, and never downloads.

```nanyx
-- file: demo.nyxp
(
  package = (name = "demo", version = "0.1.0")
  dependencies = [(package = "acme.util", version = "1.2")]  -- error NYX0041: 'nanyx.lock' locks 'acme.util' 1.2.0, but it isn't in the package cache, ...
)
-- file: nanyx.lock
(
  version = 1
  packages = [
    (package = "acme.util", version = "1.2.0", sha256 = "9f2c41aa9f2c41aa9f2c41aa9f2c41aa9f2c41aa9f2c41aa9f2c41aa9f2c41aa", dependencies = [])
  ]
)
-- file: demo.nyx
export module demo

export def one = 1
```

To fix it, run `nanyx pull`, which downloads each package the lockfile locks, and each GitHub reference, into the cache.
For a script, `nanyx pull script.nyx` downloads what its directives pin, and `nanyx run` does so before it runs it.

# NYX0042: Sources that match no file

A package's `sources` select its source files, and no file matches them, so the package has no code. When a manifest
names no `sources`, they are `**/*.nyx`, every Nanyx file under it.

```nanyx
-- file: main.nyx
::install acme.util ./util

def total = 1
-- file: util/util.nyxp
(package = (name = "acme.util", version = "0.1.0", sources = ["src/*.nyx"]))
```

This reports `'.../util.nyxp' selects its sources with 'src/*.nyx', which match no file`. To fix it, write `sources` that
match the package's files, or move the files to where they match.

# NYX0043: More than one entry point in a package

More than one source file of a package declares an `@entry`, and a package runs from one.

```nanyx
-- file: main.nyx
::install acme.tool ./tool

def total = 1
-- file: tool/tool.nyxp
(package = (name = "acme.tool", version = "0.1.0"))
-- file: tool/build.nyx
module build

@entry
def main = { () -> () }

-- file: tool/release.nyx
module release

@entry
def main = { () -> () }
```

This reports `More than one file of '.../tool.nyxp' declares an `@entry`, 'build.nyx' and 'release.nyx', but a package
has one: keep one of them`. To fix it, keep the `@entry` of the program the package is, and make the others modules it
imports, or packages of their own.

# NYX0044: Lockfile that can't be read

The package's `nanyx.lock` is not a lockfile this `nanyx` reads: it is not a record, a locked package lacks a field,
or it was written in another format. `nanyx` writes the lockfile and keeps it up to date, so this happens when it was edited by hand or written by
another version of `nanyx`.

```nanyx
-- file: demo.nyxp
(package = (name = "demo", version = "0.1.0"))  -- error NYX0044: The lockfile's format is the int 2, but this nanyx reads format 1
-- file: nanyx.lock
(
  version = 2
  packages = []
)
-- file: demo.nyx
export module demo

export def one = 1
```

To fix it, delete the lockfile and run `nanyx pull`, which writes it again, choosing the newest versions the
dependencies accept.

# NYX0045: Package with nothing to run

`nanyx run` is given a package with no `@entry`, so there is nothing to run: a library, whose manifest names no host,
or an application whose files declare no `@entry`. `nanyx run` and `nanyx build` report it for a
[test package](../tour/packages#test-packages) too, and `nanyx run` for a test file, one the manifest's `tests` selects,
which only a test build has. No example can show it, since it is reported by `nanyx run`, not by checking the package.

To fix it, run an application, or add an `@entry` to one of its files and name the host it runs under in its manifest,
as `host = (package = "nanyx.cli.host", version = "...")`. Run a test package's tests, or a test file's, with
`nanyx test`.

# NYX0046: Keyword member whose type doesn't fit its kind

A `#delay` or `#bindF` keyword passes the expression after it to its member as a function of no parameters (see
[Keyword kinds](../advanced/workflows#keyword-kinds)), but the member's type doesn't take one: a `#delay` member's type
must be of the form `(() -> a) -> r`, and a `#bindF` member's `(() -> a, a -> r) -> r`.

```nanyx
context Lazy = (
  @customKeywordType(#delay)  -- error NYX0046: 'later' is a `#delay` keyword, which passes the expression after it as a function of no parameters, so its type must be of the form `(() -> a) -> r`
  later: int -> int
)
```

To fix it, make the member take a function of no parameters, as `later: (() -> int) -> int`, or give it the kind that
takes a value, `#bind`.

# NYX0047: Version conflict

Resolution chooses a version of each package a build needs from the package index, the newest that every dependency
accepts, and there is none: two packages need versions of a third that no version satisfies both
of, a dependency accepts only versions the index doesn't have, or every version it accepts is unlisted or needs a newer
compiler. A build has [one version of each package](../tour/packages#versions), so the conflict can't be settled by
using two. The message explains it a step at a time, from the dependencies that cause it:

```text
Because acme.json 2.0.0 depends on acme.core 2.0 and acme.web 1.0.0 depends on acme.core 1.0, acme.json 2.0.0 is incompatible with acme.web 1.0.0.
And because app depends on acme.web 1.0 and acme.json 2.0, the dependencies can't all be satisfied.
```

A step that later steps refer to is numbered, `(1)`, and they say `(1)` where they use it. To fix it, change the
requirement of one of the packages the message names: depend on a version of `acme.web` that accepts `acme.core` 2, say,
or on an older `acme.json`.

# NYX0048: Dependency not locked

A dependency named by its version is the version `nanyx.lock` locks, and the lockfile doesn't lock it: it is new, or
nothing has chosen its version yet. `nanyx build` and `nanyx run` lock it themselves, as `nanyx pull` does, unless
`--locked` keeps the lockfile as it is; the editor reports it until one of them has.

```nanyx
-- file: demo.nyxp
(
  package = (name = "demo", version = "0.1.0")
  dependencies = [(package = "acme.util", version = "1.2")]  -- error NYX0048: The dependency 'acme.util' 1.2 is not locked in 'nanyx.lock': run `nanyx pull` to choose its version and lock it
)
-- file: demo.nyx
export module demo

export def one = 1
```

To fix it, run `nanyx pull`, which chooses the newest version the dependencies accept, writes it to `nanyx.lock` and
downloads it.

# NYX0049: Package file that isn't UTF-8

`nanyx pack` and `nanyx publish` pack a package's manifest and source files as UTF-8 text with LF line endings, whatever
line endings and byte-order mark the checkout gives them, so that the same commit packs to the same bytes on every
machine ([`nanyx pack`](./command-line#nanyx-pack)). A file that isn't UTF-8 can't be written that way, and the
package isn't packed:

```text
error NYX0049: Package file that isn't UTF-8
  'src/strings.nyx' isn't UTF-8 text, so 'acme.util' can't be packed: save it as UTF-8
```

To fix it, save the file as UTF-8.

# NYX0050: Keywords in the wrong order

An exported scoped context is written `export scoped context`: `export` comes first, as on every definition, and
`scoped` beside `context`. `scoped export context` is an error.

```nanyx
scoped export context Tx = protected ()  -- error NYX0050: Write `export` before `scoped`: `export scoped context`
```

To fix it, write `export scoped`, which the error's fix does; `nanyx format` writes it too.

# NYX0051: Unlisted version

A warning, not an error: the build goes on. Its author has unlisted the version a lockfile locks, or a script's
`::install` pins, with [`nanyx unlist`](./command-line#nanyx-unlist), so no new resolution chooses it, but what
already names it keeps it. A command that reads the package index for the version, `nanyx pull`, `install`, `build` or
`run`, warns; one that reads it only from the package cache doesn't:

```text
warning NYX0051: Unlisted version 'acme.json 0.1.1'
  = acme.json 0.1.1, which 'nanyx.lock' locks, is unlisted, so no new resolution chooses it: to move to a listed version, delete its entry from 'nanyx.lock' and run `nanyx pull`, first changing the version the manifest asks for if it accepts no listed one, or keep it if you need it
```

To move to a listed version in a package, delete the version's entry from `nanyx.lock`: `nanyx pull`, or the next
build, locks the newest listed version the manifest accepts. If it accepts none, change the version it asks for first.
In a script, change the version its `::install` names. Or keep the version, if you need it.

# NYX0303: Shipped package that differs from the index's

A warning, not an error: the build goes on. A package the compiler ships, such as `nanyx.cli`, is seeded into the
package cache and locked as any package from the index is, with the SHA-256 of its `.nyxpkg`. This compiler's copy of it
packs to another SHA-256 than the one the package index records for the same version, the copy its release attached:

```text
warning NYX0303: Shipped package that differs from the index's 'nanyx.cli 0.51.0'
  = This compiler's nanyx.cli 0.51.0 isn't the one the package index has: the copy it ships packs to the SHA-256 3f1c…, and the index records 9a0e…. …
```

The compiler builds with its own copy, which a lockfile it writes locks, so another machine that downloads the version
from the index gets other bytes. A compiler built from source whose `packages/` have changed since its release does this.
A released compiler doesn't, since its copies are the ones its release published: report it if yours does.

# NYX0052: Package that needs another compiler

The package's manifest says, with `nanyx` in its `package`, that it builds with a compiler newer than the one running,
or with one compiler only, as `nanyx = "=0.51.0"`, which isn't the one running. The message names both versions:

```nanyx
-- file: demo.nyxp
(
  package = (name = "demo", version = "0.1.0", nanyx = "99.0")  -- error NYX0052: The package builds with nanyx 99.0 or later
)
```

To fix it, install a newer compiler, or the one the package names. If the package does build with this one, lower its
`nanyx`, or use a version of the package made for this compiler.

# NYX0053: Host package not named for a host

A host's name ends in `.host`, and only a host's does: the library a host runs programs against takes the short name,
`nanyx.cli`, and the host is `nanyx.cli.host` ([Hosts](../advanced/hosts)). A package whose manifest declares a `host`
under another name is an error at its name, as is a package named `.host` that declares none:

```nanyx
-- file: demo.nyxp
(
  package = (name = "acme.server", version = "0.1.0", host = (root = "server.host"))  -- error NYX0053: The package 'acme.server' declares a `host`, so its name must end in `.host`
)
```

To fix it, rename the package: a host to `acme.server.host`, or a library to a name without `.host`. If a package
named `.host` is a host, declare its `host`.

# NYX0054: Shipped package named by its old name

The packages the compiler ships were renamed when every host came to be named `<name>.host`: `nanyx.console.core` is
`nanyx.cli`, the console host `nanyx.console` is `nanyx.cli.host`, `nanyx.http.core` is `nanyx.http`, the HTTP host
`nanyx.http` is `nanyx.http.host`, and `nanyx.playground` is `nanyx.playground.host`. A manifest's `host` or
`dependencies`, a script's `::host` or `::install`, or `--host` that names one by its old name is an error that gives
the new one. `nanyx.http` is the library's name now, so it is an error only where a host is named:

```nanyx
-- file: demo.nyxp
(
  package = (name = "demo", version = "0.1.0")
  dependencies = [(package = "nanyx.console.core", version = "0.51.0")]  -- error NYX0054: 'nanyx.console.core' is now 'nanyx.cli'
)
```

To fix it, write the new name, and change the imports of its modules to match: `nanyx.console.core/console` is
`nanyx.cli/console`, and `nanyx.http.core/request` is `nanyx.http/request`. An import that still names the old package
is [`NYX0003`](#nyx0003), whose message gives the import by the new name.

# NYX0101: Unknown identifier

A name is used that nothing in scope defines. When a standard library module exports the name, the message says which
import brings it in, and an editor offers to add it. Otherwise, when a name in scope is spelled closely enough, the
message suggests it, and an editor offers to write it.

```nanyx
def total = 1
def doubled = totl * 2  -- error NYX0101: 'totl' is not in scope: did you mean 'total'?
```

To fix it, correct the name, define it, or import the module that exports it.

# NYX0102: Unknown function

A function is called that nothing in scope defines, or a value is piped to a name that is neither a function in
scope nor attached to the value's type ([Piping](../tour/piping)).

```nanyx
def loud = shout("hi")  -- error NYX0102: 'shout' is not in scope
```

When a standard library module exports the function, the message says which import brings it in, and an editor
offers to add it. A name qualified by the module's own name is imported under that name; a name more than one module
exports lists each of them.

```nanyx
def larger = max(1, 2)  -- error NYX0102: 'max' is not in scope, and 'max' is defined in 'nanyx/math': add `import nanyx/math`
def size = math.abs(-1)  -- error NYX0102: 'math.abs' is not in scope, and 'abs' is defined in 'nanyx/math': add `import nanyx/math as math`
```

To fix it, correct the name, define the function, or import the module that exports it.

# NYX0103: Unknown member

A record is read or updated with a field it does not have.

```nanyx
def point = (x = 1, y = 2)
def moved = point with (z = 3)  -- error NYX0103: The record has no member 'z' to update
```

When one of the record's fields has a name close to the one written, the error suggests it, and the editor offers a
fix that writes it:

```nanyx
def person = (name = "Ada", age = 36)
def greeting = person.nmae  -- error NYX0103: The record 'person' has no member 'nmae': did you mean 'name'?
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

def greeting = hello  -- error NYX0105: It could be 'english.hello' or 'french.hello': import all but one of them with `as`
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

def shouted = "hi" \shout  -- error NYX0106: For a 'string', it is 'string.shout', which is defined in 'loud' and 'louder': ...
-- file: loud.nyx
module loud

export def string.shout: string -> string = { s -> s ++ "!" }
-- file: louder.nyx
module louder

export def string.shout: string -> string = { s -> s ++ "!!" }
```

A bare name that two contexts in scope attach a definition by is ambiguous too
([Attached to a context](../advanced/attached-definitions#attached-to-a-context)):

```nanyx
context Greeter = protected ()

def Greeter.hello: [Greeter] string -> string = { s -> "hello {s}" }

context Waver = protected ()

def Waver.hello: [Waver] string -> string = { s -> "wave {s}" }

def both = { _ ->
  use Greeter()
  use Waver()
  hello("x")  -- error NYX0106: 'hello' could be 'Greeter.hello' and 'Waver.hello', ...
}
```

To fix it, import all but one of the modules with `as`, or, for a context's definition, write its whole name, as
`Greeter.hello`.

# NYX0107: Unknown attached type

A definition is attached to a type (`def Shape.area`) that is not defined.

```nanyx
def Shape.area: int -> int = { x -> x }  -- error NYX0107: 'Shape.area' is attached to 'Shape', which names no type in scope
```

To fix it, define the type, or correct its name.

# NYX0108: Unknown context

A definition's type requires a context that is not defined, or requires an operator that no context in the prelude
has, or a `use` loads a context whose name is not in scope, as in `use Countr(...)`; there, the message suggests the
contexts with the closest names.

```nanyx
def greet: [Telegraph] () -> () = { () }  -- error NYX0108: The signature of 'greet' names it, but it is not in scope
```

When a standard library module exports the context, the message says which import brings it in, and an editor offers
to add it.

To fix it, correct the context's name, define it, or import the module that exports it.

# NYX0109: Unknown type

A type is named that is not defined. When a standard library module exports the type, the message says which import
brings it in, and an editor offers to add it.

```nanyx
def name: Strng = "Ada"  -- error NYX0109: 'Strng' is not in scope
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

The program has `???`, which stands for code not written yet. It has the type `undefined`, which fits wherever a value
of any type is expected, so the rest of the program can be checked, and nothing that uses it is another error. It cannot
be run in a release build.

```nanyx
def total: int -> int = { x -> ??? }  -- error NYX0111: `???` stands for code not written yet
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

A type is written `Int`, `Float`, `Bool` or `Char`, or `unit` or `Unit`. Those are not types: the built-in types are
`int`, `float`, `bool` and `char`, and the type of `()` is written `()`. An editor offers to write the right name.

```nanyx
def count: Int = 3  -- error NYX0114: A built-in type's name is in lower case: write 'int'
```

```nanyx
def nothing: unit -> int = { _ -> 0 }  -- error NYX0114: The type of `()` is written '()', not 'unit'
```

To fix it, write the type in lower case, or write `()`.

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

# NYX0117: Duplicate field

A record names the same field twice: a record type, a record value, a call's named arguments, the updates of a
`with`, or a record pattern. The error is at the second, and the module is checked no further until it is fixed.
Fields after a spread may replace the spread record's own, as in `(...defaults, port = 80)`, but not one written
before them.

```nanyx
def user = (name = "Ada", name = "Grace")  -- error NYX0117: 'name' is named twice in this record
```

To fix it, rename or remove one of the two.

# NYX0118: Capitalised tag that names no pattern

A capitalised tag, such as `#Even`, is a [custom pattern](../advanced/custom-patterns)'s, and no pattern in scope
returns this one. An ordinary tag is written in lower case.

```nanyx
def answer = #Some(42)  -- error NYX0118: No pattern in scope returns `#Some`
```

To fix it, write the tag in lower case, `#some(42)`, which the editor offers to do; or declare or import the pattern
that returns it.

A pattern a module block declares without `export` is in scope only within the block. Its tag outside the block is this
error, which names the block and says to write `export pattern` there.

# NYX0119: Tag that more than one pattern returns

Two [custom patterns](../advanced/custom-patterns) in scope return the same tag, so a match on the tag couldn't tell
which pattern to run. The error is at the second of two patterns a module declares, or, for two that it imports, where
the tag is used.

```nanyx
pattern int -> #Even | #Odd = { n -> if n % 2 == 0 then #Even else #Odd }
pattern int -> #Odd | #Big = { n -> if n > 100 then #Big else #Odd }  -- error NYX0119: Another pattern in this module returns `#Odd` too
```

To fix it, give one of the patterns a tag of its own, or import only one of them.

# NYX0120: Name a whole-module re-export gives twice

A [whole-module re-export](../tour/modules#re-exporting), `export shapes`, gives a name that the module writing it
defines too, or that a re-export before it gives from another module. An importer couldn't tell which of the two it
means. The error is at the whole-module re-export, and names both.

```nanyx
-- file: geometry.nyx
module geometry

export shapes  -- error NYX0120: 'area' is re-exported from 'shapes', but this module defines 'area' too

def area: int = 1
-- file: shapes.nyx
module shapes

export def area: int -> int = { n -> n * n }
export def perimeter: int -> int = { n -> 4 * n }
```

To fix it, re-export only the rest of what the module gives: the fix rewrites `export shapes` as
`export shapes (perimeter)`. Or rename the definition.

# NYX0201: Type mismatch

A value's type is not the type its place needs: an argument is not what the function takes, a definition's value is
not the type its annotation says, or two branches give back different types. The message names the two types.

```nanyx
def count: int = "three"  -- error NYX0201: 'string' vs 'int'
```

To fix it, change the value, or the annotation, so that the two types agree. Where the types come from far apart, an
annotation on a definition in between shows which of them is wrong.

Where the type expected is a protected or private [nominal type](../advanced/nominal-and-protected-types) of another
module, a second line says that only that module makes values of it, and names the definitions attached to the type that
make one, up to three, or says that none does. A value of the type's shape is the error NYX0260 instead.

Where a `match`'s arms, an `if`'s branches or a list's items give different types, the error is at the first one that
differs from those before it, and says what it gives and what they give. Each one before it is a related location,
saying what it gives, which an editor links to and the command line shows under the error:

```nanyx
def describe = { n: int ->
  match n
    | 0 -> "zero"
    | _ -> 2  -- error NYX0201: This arm gives 'int', but the arms before it give 'string'
}
```

An `if` with no `else` gives `()` when its condition is false, so one whose `then` branch gives something else is
reported at the `if`, saying that it has no `else`:

```nanyx
def size = { n: int -> if n > 10 then "big" }  -- error NYX0201: This `if` has no `else`, so it gives '()' when its condition is false: add an `else` of type 'string'
```

A match arm for a tag that the value's union doesn't have can never match, so it is reported too, with the union's
tags and the one it most likely means, of those no other arm handles:

```nanyx
def o: #some(int) | #none = #none

def r =
  match o
    | #some(n) -> n
    | #nothing -> 2  -- error NYX0201: This arm can never match: the value is a '#some(int) | #none', whose tags are #some and #none, so it has no #nothing: did you mean #none?
```

A value whose type is a single tag, as `def simple = #nil` is, has that tag alone, so an arm for any other is reported
the same way. A union with a rest, such as `#some(a) | r`, can have any tag, so a match on one accepts an arm for any
tag.

A tag given where a union without a rest is expected, that the union doesn't name, is reported with the union's tags, and
the one it most likely means:

```nanyx
type Color = #red | #green | #blue

def c: Color = #gren  -- error NYX0201: '#gren' is not one of the tags of 'Color': '#red', '#green' or '#blue': did you mean '#green'?
```

A lambda with `yield` in it is a lazy sequence, and a builder such as `list.select` or `list.build` takes a function
that calls `emit` for each value instead ([Building a collection](../advanced/sequences-and-queries#building-a-collection)).
Giving it a lambda that yields is reported at the first `yield`:

```nanyx
def positives = [1, -2, 3] \list.select { x -> if x > 0 then yield x }  -- error NYX0201: This `yield` makes the lambda a lazy sequence, but it is given where a function that calls `emit` is expected, as a builder such as `list.select` or `list.build` takes: write `emit(x)` for `yield x`
```

A keyword call such as `start! e` is a call of the def named by the keyword, `start({ e })`, so a local, a parameter or a
pattern's variable of that name is called instead. When it doesn't take a block, the error is reported at the keyword
and names it:

```nanyx
import nanyx/async as (Async)

def work = { _ ->
  def start = 5
  def t = start! 1  -- error NYX0201: 'start!' calls the local 'start' defined at line 4, of type 'int', not 'Async.start': ...
  start
}
```

To fix it, rename the local, or call the keyword's def by its whole name, `Async.start({ 1 })`.

# NYX0202: Annotation too general

A definition's annotation promises more than its body gives: it says a type variable could be any type, but the body
needs a particular type, or the same type as another variable.

```nanyx
def same: a -> b = { x -> x }  -- error NYX0202: The annotation of 'same' is more general than its inferred type, where 'b' would have to be 'a'
```

To fix it, write the type the body has (here `a -> a`), or leave the annotation out and let the type be inferred.

# NYX0203: Non-exhaustive match

A `match` has no arm for some of the values it can be given. The message names what is missing, and an editor offers
to add the missing arms ([Pattern matching](../tour/pattern-matching)).

```nanyx
def describe: #some(int) | #none -> string = { o ->
  match o  -- error NYX0203: Missing tags: none
    | #some(n) -> "some"
}
```

To fix it, add an arm for each missing case, or an arm `_` for everything not matched yet.

# NYX0204: Missing catch-all arm

A `match` is on a tag union with a rest, such as `#some(a) | r`, which stands for tags the function does not know.
No list of arms can name them all, so the match needs an arm that takes anything.

```nanyx
def mapSome: (#some(a) | r), (a -> b) -> #some(b) | r = { o, f ->
  match o  -- error NYX0204: This match is on a union with a rest, which stands for tags it doesn't name: add a catch-all arm, `_ -> ...`
    | #some(v) -> #some(f(v))
}
```

To fix it, add a last arm with a name or `_` as its pattern, such as `| other -> other`.

# NYX0205: Missing context

An operation needs a context that the type of its value does not have: two values are compared with `<` but their
type has no order, are compared with `==` but contain functions, or are used as a map's keys but cannot be hashed.
Where the type is a type variable of the definition, the message says which context to add to its type, and an
editor offers to add it. The headline names the context with the type it's needed at, as in
`Missing context 'Ord(map(string, int))'` or `Missing context 'ToString(Point)'`.

```nanyx
def less = ["a" => 1] < ["a" => 1]  -- error NYX0205: 'map(string, int)' cannot be ordered with <: maps have no order
```

To fix it, use values whose type has the context, give a nominal type the definition it needs (such as `==`), or add
the context to the definition's type, as the message says ([Contexts](../advanced/contexts)).

A call also needs a context that nothing in scope supplies. A plain context is supplied by any context with its
members, but a `protected` or `private` one only by an instance of itself, which only its own module can make:

```nanyx
context Tx = protected (commit: () -> ())
context Fake = (commit: () -> ())

def needsTx: [Tx] () -> () = { commit() }

def main = {
  use Fake(commit = { () })
  needsTx()  -- error NYX0205: 'needsTx' requires the context 'Tx', which is not available here: 'Tx' is protected, ...
}
```

To fix it, load an instance of the context itself, or require it in the type of the definition around the call.

A call to a library's function that requires a context the program's host doesn't provide is this error too, at the
call: a library that writes `[Console]` or `[FileSystem]` can't be used under a host without them
([Hosts](../advanced/hosts)).

```text
error NYX0205: Missing context 'Console'
  --> main.nyx:6:1
   |
 6 | greet("Ada")
   | ^^^^^
   = 'greet' requires the context 'Console', which the host 'acme.quiet/quiet.host' does not provide
```

To fix it, run the program under a host that provides the context, or don't call the function.

# NYX0206: Invalid workflow

A builder block, such as `opt { ... }`, is not one its builder can run: the context the builder runs it under lacks a
member the block needs (a keyword it uses, `zero` for a block that can end without `return`, or `for` for a loop whose
body uses a keyword), or a keyword is somewhere the rest of the block cannot follow it ([Workflows](../advanced/workflows)).

```nanyx
context Opt = (
  try: [c] ((#some(a) | r), ([c] a -> #some(b) | r)) -> #some(b) | r
  return: a -> #some(a) | r
)
def opt: [c] ([Opt, c] () -> t) -> t = { block ->
  use Opt(
    try = {
      | #some(value), continue -> continue(value)
      | failure, _ -> failure
    }
    return = { value -> #some(value) }
  ) in block()
}
def half: int -> #some(int) | #odd = { n -> if n % 2 == 0 then #some(n // 2) else #odd }
def show = { n -> opt {
  def h = try half(n)
  h + 1  -- error NYX0206: This `opt` block can end here without `return`, and the context `opt` runs it under has no `zero` to be its value then: ...
} }
```

To fix it, do what the message says: here, end the block with `return`, or give the context a `zero`.

# NYX0207: Workflow keyword outside a workflow

A workflow keyword, such as `try` or `return`, is used outside a builder block. It means something only to the
builder that runs the block.

```nanyx
def halve: int -> #some(int) | #none = { n -> if n % 2 == 0 then #some(n // 2) else #none }

def quarter = { n ->
  def h = try halve(n)  -- error NYX0207: `try` is a workflow keyword, so it can only be used inside a builder block, such as `proc { ... }`
  halve(h)
}
```

To fix it, put the code in a builder block, such as `proc { ... }`, or match on the value instead.

# NYX0208: Invalid built-in call

A built-in, such as `dbg`, `not`, `with` or a loop, is given the wrong number or kind of arguments.

```nanyx
def x = dbg()  -- error NYX0208: `dbg` takes one argument, not 0
```

To fix it, give the built-in what the message says it expects.

# NYX0209: Invalid record update

A `with` update cannot be made: the record's fields are not known where it is written, or a field is given by
position rather than by name ([Records](../tour/records)).

```nanyx
def point = (x = 1, y = 2)
def moved = point with (3)  -- error NYX0209: This field has no name, and `with` updates fields by name: write `name = value`
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
def greet: string -> () = { name -> println("Hello, {name}") }  -- error NYX0211: 'println' requires the context 'Console', which 'greet' does not declare: ...
```

To fix it, add the context to the definition's type, as in `[Console] string -> ()`, or load an instance with `use`
([Contexts](../advanced/contexts)).

A module-level def that starts a task without naming `start` gets this error too, at the call that starts it: only a
def whose body names `start` is the scope of its tasks ([Async](../advanced/async#each-def-is-a-scope)). Start the task
with `start` in the def itself.

# NYX0212: Nominal type constructed outside its module

A nominal type, one declared `protected` or `private`, is constructed, updated with `with`, or a private one taken
apart (by a pattern, a field read or a spread), outside the module that defines it. Only its own module can make its values ([Nominal types](../advanced/nominal-and-protected-types)).

```nanyx
module money =
  export type Money = protected (pence: int)

import money

def price = Money(pence = 250)  -- error NYX0212: The nominal type 'Money' can only be constructed in the module that defines it: ...
```

To fix it, make the value with a function the module exports, such as `Money.new`. The message names the definitions
attached to the type that make one, when there are any: `Make one with 'Money.new'.` A value of the type's shape given
where the type is expected, without the constructor, is NYX0260.

# NYX0213: Wrong number of patterns

An arm of a match on several values, such as a lambda's parameters or a loop over several lists, has a different
number of patterns from the number of values. In a `match`, an arm can also have a single pattern, which matches all
the values as a tuple: `| _ ->` matches whatever they are, and `| t ->` binds their tuple.

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
    | (a, _) & (_, a) -> a  -- error NYX0215: More than one part of this and-pattern binds it: bind it in one part only
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

A `for` loop walks only ranges with no end, such as `0..`, which never end, so the loop would never end either
([Loops](../tour/loops)).

```nanyx
def count = for 0.. | i -> i  -- error NYX0217: A `for` loop over a range with no end alone never ends: ...
```

To fix it, walk the range in step with a list or a range with an end, as in `for xs, 0.. | x, i -> ...`.

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
order and equality are always those of its structure; `!=`, which is always `not (a == b)`; or a comparison such as
`<` attached to a built-in type, whose order is the compiler's own.

```nanyx
type Point = (x: int, y: int)
def Point.hash: Point -> int = { p -> p.x }  -- error NYX0219: 'Point.hash' cannot be defined, because 'Point' is a structural type, ...
```

To fix it, make the type nominal (`Point`) to give it its own definition, or attach `==` instead of `!=`.

# NYX0220: Block without a result

A block ends with a definition, so it has no value to give back.

```nanyx
def total = {
  def x = 1  -- error NYX0220: This block ends with the definition of 'x', so it has no value to give back: ...
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
  x := 1  -- error NYX0222: `:=` changes only a cell, a name declared with `mut`
  x
}
```

To fix it, declare the name with `mut`, or define a new name for the new value.

# NYX0223: Cell assignment type mismatch

A value of one type is assigned to a cell of another. A cell holds values of one type.

```nanyx
def count = memory {
  mut x = 0
  x := "one"  -- error NYX0223: 'string' vs 'int'
  x
}
```

To fix it, assign a value of the cell's type.

# NYX0226: Unbound context variable

A context variable, which stands for any contexts, as `c` does in `[c] int -> int`, is used in a definition's type
but is not among the contexts of the definition itself, or of the function it returns (as `c` is in
`([c] a -> b), ([c] b -> d) -> [c] a -> d`, where whoever calls that function binds it).

```nanyx
def g: [d] ([c] int -> int) -> int = { f -> 1 }  -- error NYX0226: Nothing binds it: add it to the contexts of 'g', as in '[c] ...'
```

To fix it, add the variable to the definition's own contexts, as in `[c] ([c] int -> int) -> int`.

# NYX0227: Function requires contexts not allowed here

A function that requires a context is given where a function that requires none is expected: passed to a function
whose parameter type writes no contexts, or returned from a definition whose result type writes none.

```nanyx
def runPure: (int -> int) -> int = { g -> g(5) }

def pass: ([Console] int -> int) -> int = { f -> runPure(f) }  -- error NYX0227: '[Console] int -> int' is given where 'int -> int' is expected, which does not require the context 'Console': ...
```

To fix it, write the contexts the function may require on the type expected, as in `([Console] int -> int)`, or a
context variable for any, as in `[c] (int -> int)`.

The same goes for a value of a type generic in contexts (see
[Types generic in contexts](../advanced/contexts#types-generic-in-contexts)): a `[Console] Thunk(int)` given where a
`Thunk(int)` is expected is an error, as its functions require `Console`. So are two such values that must be the
same type but carry different contexts, as when one's type is still being inferred where the other is passed: the
message names both types and the contexts only one of them carries.

# NYX0228: Type alias refers to itself outside a tag

A type alias names itself other than in the payload of one of its tags, so the type would have no end.

```nanyx
type Pair = (int, Pair)  -- error NYX0228: It refers to itself outside a tag: ...
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
def f = { r -> (...r, a = 1) }  -- error NYX0231: A spread (`...`) needs a record whose fields are known here
```

To fix it, annotate the value's type, so that its fields are known.

A spread of an open record, one whose type ends with `...`, can set the fields it is known to have, `(...p, x = 2)`,
and makes a record of its own type. It can't add another field, or be joined with another record, since the fields it
may have besides aren't known:

```nanyx
def f: (x: int, ...r) -> int = { p ->
  def q = (...p, z = 1)  -- error NYX0231: A spread of a record that may have more fields can only set the fields it is known to have, and 'z' isn't one of them: write the record's type with all its fields, closed, as in `(x: int, y: int)`, to make a record of other fields
  q.x
}
```

# NYX0232: Scoped context is neither protected nor private

A `scoped context` is declared neither `protected` nor `private`. A scoped context must be one or the other, so that
only its own module can make its instances.

```nanyx
scoped context Tx = (commit: () -> ())  -- error NYX0232: The scoped context 'Tx' must be either protected or private, ...
```

To fix it, declare the context `protected` or `private` after its `=`, as in `scoped context Tx = protected (...)`.

# NYX0233: Scoped context escapes the call that supplies it

Something that requires a scoped context, such as `Memory`, leaves the call that supplies the context: a memory
block returns a function over one of its cells.

```nanyx
def leak = memory {
  mut count = 0
  { count }  -- error NYX0233: The result of this 'memory' call requires its 'Memory' through the cell 'count', so it cannot leave the call: ...
}
```

To fix it, give back what the cells hold, rather than the cells or a function over them.

A def is the scope of the tasks it starts ([Async](../advanced/async#each-def-is-a-scope)), so a task, or a lambda
that starts one, can't leave the def either:

```nanyx
def leaks = { _ -> start! 1 }  -- error NYX0233: A task can't leave the def that started it: ...
```

To fix that, await the task in the def, and give back what it gave back.

# NYX0234: mut argument is not a cell

An argument is passed with `mut`, as a cell, but it is not one.

```nanyx
def inc = { mut cell -> cell := cell + 1 }
def total = memory {
  def count = 0
  inc(mut count)  -- error NYX0234: `mut` passes only a cell, a name declared with `mut` or a `mut` parameter
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

def greet: [Log, ToString(a)] string -> () = { name -> log("hello {name}") }  -- error NYX0236: 'a' in 'ToString(a)' is not the type of any parameter of 'greet' or of its result, ...
```

To fix it, remove the context, or give a parameter the type it is on.

The same error is reported at a `_` a context is applied to, as in `[Foo(_)]`, when the body does not make it a type the
definition's type names: the `_` is the type the body calls the context's members at, and nothing says what it is if the
body never calls them.

```nanyx fragment
def same: [Foo(_)] a -> a = { x -> x }  -- error NYX0236: The body of 'same' does not make this '_' a type its parameters or result name, ...
```

# NYX0237: Entry point does not fit the host

The definition marked `@entry` does not have the type its [host](../advanced/hosts) runs. The host is a
function that takes the entry point, so the entry point must fit the type of its parameter. A command-line program runs
under the console host, `nanyx.cli.host/cli.host`, whose entry point takes nothing and may require `Console`.

```nanyx
module main

@entry
def main = { name: string -> println("Hello, {name}!") }  -- error NYX0237: It doesn't fit the host 'nanyx.cli.host/cli.host' ...
```

The error is reported at `@entry`, and shows the type the host expects and the type the entry point has, with the
contexts it requires, and names each context the entry point requires that the host doesn't provide. To fix it, give the entry point the shape the host
expects: for a command-line program, a function that takes nothing.

# NYX0238: HostRuntime required outside a host

A definition or call requires `HostRuntime`, the context a host is written on. The toolchain provides it only to the
modules of the host's package, the package whose `run` runs the program's entry point, and of the
[host libraries](../advanced/hosts#host-libraries) it depends on, so that a library can only reach the outside world
through the contexts the host provides.

```nanyx
import (
  nanyx/host.memory
  nanyx/host.runtime
)

def peek = { address -> readByte(address) }  -- error NYX0238: 'peek' requires the context 'HostRuntime', which only a host's package, and the host libraries it depends on, may require: ...
```

To fix it, require the context the host provides instead, such as `Console`, and call its members.

# NYX0239: Value the script computes as it runs, named outside it

A script's value that needs a context, such as `def line = readLine()`, is computed when the script runs, in the order
its statements are written, so that it reads its line once. A function can't name it, because the function may be
called where the value doesn't exist, and a statement can't read it before its def.

```nanyx
def line = readLine()

def greet = { name -> println("{name} said {line}") }  -- error NYX0239: 'line' is computed when the script runs, so a function can't name it: pass it as an argument

greet("Ada")
```

To fix it, pass the value to the function as an argument, `greet("Ada", line)`, or move the def above the statements
that read it.

# NYX0240: Contexts written without a type on a value

An annotation that writes only contexts, `def main: [Console] = { ... }`, says the lambda runs in them. A value that is
not a lambda doesn't run: a function written point-free, such as a composition, requires its contexts where it is
called, so they belong on its function type.

```nanyx
def shout: [Console] string -> () = { s -> println(s ++ "!") }

def greet: [Console] = { name -> "hello " ++ name } >> shout  -- error NYX0240: 'greet' is not a lambda, so it does not run in the contexts its annotation writes: write them on its function type, as in '[Console] string -> ()'
```

To fix it, write the function type with its contexts, `def greet: [Console] string -> () = ...`.

# NYX0241: Invalid foreign function

A def marked `@extern("module", "name")` declares a foreign function: a function the embedder provides, which the
compiled module imports. It has a written type and no value, and the type takes and returns only WebAssembly's value
types, written `I32`, `I64`, `F32` and `F64`, or `()` for no result. Strings, records and lists don't cross the
boundary: a host passes addresses and lengths in the module's memory instead.

```nanyx
import nanyx/host.runtime

@extern("env", "log")
def log: [HostRuntime] string -> ()  -- error NYX0241: A foreign function takes and returns only WebAssembly's value types, I32, I64, F32 and F64, not 'string'
```

The same code is reported for a foreign function written with a value, one without a type, and an `@extern` that
doesn't give the import's module and name as two strings.

To fix it, write the type in value types, `def log: [HostRuntime] I32, I32 -> ()`, passing the string's address and
length.

# NYX0242: Foreign function without HostRuntime

A foreign function's type must require `HostRuntime`. Only a host is given `HostRuntime`, so only a host's code can
call a foreign function, and a library reaches the outside world only through the contexts its host provides.

```nanyx
@extern("wasi_snapshot_preview1", "sched_yield")
def schedYield: () -> I32  -- error NYX0242: A foreign function's type must require 'HostRuntime', so that only a host can call it: write [HostRuntime] before it
```

To fix it, import `nanyx/host.runtime` and write `[HostRuntime]` before the type:
`def schedYield: [HostRuntime] () -> I32`.

# NYX0243: Import declared with different types

Two foreign functions name the same import, its module and name, with different types. A WebAssembly module imports
each function once, at one type.

```nanyx
import nanyx/host.runtime

@extern("wasi_snapshot_preview1", "proc_exit")
def procExit: [HostRuntime] I32 -> ()

@extern("wasi_snapshot_preview1", "proc_exit")
def exitWith: [HostRuntime] I64 -> ()  -- error NYX0243: 'exitWith' imports "proc_exit" from "wasi_snapshot_preview1" as (I64) -> (), but 'procExit' imports it as (I32) -> ()
```

To fix it, declare the import once, with the type the embedder gives it, and call that one.

# NYX0244: Private context's member called outside its module

A member of a `private` context is called, or read, outside the module that declares the context. Any module can
require a private context and call the functions that require it, but only its own module can use what an instance
holds ([Contexts](../advanced/contexts)).

```nanyx
module db =
  export context Tx = private (commit: () -> ())
  export def query: [Tx] string -> () = { sql -> commit() }

import db

def f: [Tx] () -> () = {
  query("select 1")
  commit()  -- error NYX0244: 'commit' is a member of the private context 'Tx', which only its own module can call: call a function that module exports instead
}
```

To fix it, call a function the context's module exports, as `query` here, or declare the context `protected`, whose
members any module can call.

# NYX0245: Context instance used as a value

A name a lambda gives a context with `context l` is used other than by `use l` or `use l in ...`. It names the
instance whoever calls the lambda supplies, which is never a value: it can't be passed, returned, stored or compared
([Contexts](../advanced/contexts)).

```nanyx
context Log = (log: string -> ())

def keep: [Log] () -> () = { context l ->
  def saved = l  -- error NYX0245: It names a context instance, which can only be loaded with `use l`
  ()
}
```

To fix it, load the instance with `use l` where its members are needed. To run code under a context elsewhere, pass
that code a lambda, or call a function that requires the context.

# NYX0246: Context name that names no context the lambda is given

A lambda's `context l` doesn't name exactly one context its type requires: the lambda is given none, it is given
several and `l` doesn't say which, or `context l: C` names a context the lambda isn't given.

```nanyx
context Log = (log: string -> ())
context Tick = (tick: () -> ())

def run: [Log, Tick] () -> () = { context l -> () }  -- error NYX0246: 'l' could name any of 'Log' and 'Tick', which this lambda is given: say which, as in `context l: Log`
```

To fix it, say which context the name is for, `context l: Log`, or name one the lambda's type requires. Of two
instances of one parameterised context, say which by its types, `context s: Show(int)`.

A name is for a context the lambda's type, or its def's, writes: one with members, an instance of a parameterised
context, or one with no members, and a `memory` block's `Memory`. A context variable (`[c]`), another scoped context,
and what a def that doesn't write its type was inferred to need can't be named, so naming one is this error too.

# NYX0247: Context written as a type

A context's name is written where a type goes: a parameter's, a result's or a field's type, an annotation, or a type
argument. A context is not a type: its name can be written only in a context list, as in `[Log]`, and as the
constructor in a `use` ([Contexts](../advanced/contexts)).

```nanyx
context Log = (log: string -> ())

def take = { l: Log -> () }  -- error NYX0247: 'Log' is a context, not a type: it can only be required, as in `[Log]`, or loaded with `use`
```

To fix it, require the context, `def take: [Log] () -> () = { ... }`. To give code an instance to run with, write a
handler, a function that runs a lambda under a `use`:
`def withLog: ([Log] () -> a) -> a = { f -> use Log(log = { m -> () }) in f() }`.

# NYX0248: Context instance written outside a use

A context's constructor is called anywhere but as the instance a `use` loads. An instance is never a value, so it
can't be held in a def, passed or returned ([Contexts](../advanced/contexts)).

```nanyx
context Log = (log: string -> ())

def quiet = Log(log = { m -> () })  -- error NYX0248: A context's instance can only be written in a `use`: to reuse one, write a function that runs a lambda under it, as in `{ f -> use Log(...) in f() }`
```

To fix it, write the instance in the `use` that loads it, `use Log(log = { m -> () })`. To reuse one, write a handler,
`def quietly: ([Log] () -> a) -> a = { f -> use Log(log = { m -> () }) in f() }`, and run code with
`quietly { ... }`. A handler writes its type, so that the lambda it is given is given the context.

# NYX0249: use of something other than a context instance

A `use` loads something other than a context's constructor or a name a lambda bound with `context`: a record, a def's
name, a call's result or a field. A call of a name that is not in scope at all is NYX0108 instead. A `use` loads only a declared context, with its instance written there, so loose
members are never in scope ([Contexts](../advanced/contexts#loading-a-context)).

```nanyx
context Log = (log: string -> ())

def quiet = {
  use (log = { m: string -> () })  -- error NYX0249: `use` loads a context's constructor, as in `use Log(...)`, or a name bound with `context`
  log("hi")
}
```

To fix it, write the context's constructor, `use Log(log = { m -> () })`. To reuse an instance, write a handler, a
function that runs a lambda under the `use`, as NYX0248 shows.

# NYX0250: Use of an instance for a type that has its own

A `use` loads an instance of a context for a type that already has its own: one built from its attached definitions,
derived from its parts, or the compiler's own. A type's own instance always wins, so `1 == 2` is false and a set is
read with the `Hash` it was built with wherever they are written
([A type's own instance wins](../advanced/contexts#a-types-own-instance-wins)).

```nanyx
def n = use Eq(int)(`==` = { x, y -> true }) in 1  -- error NYX0250: 'int' already has `==`, which a `use` can't replace: for a different equality, wrap the value in a protected type with its own `==`, or pass the comparison to a `...By` function
```

To fix it, give the values a type that says how they behave, a protected type with its own `==`, `compare`, `hash` or
`toString`, or pass what to do to a function, as `list.sortBy` takes a key and a hole can call a function. A `use` can
still give an instance to a type that has none of its own, such as `Eq` for a record that holds a function.

# NYX0251: Operator required by its member

A context list holds contexts and context variables. An operator required by the member it reads, as in
`` [`+`: a, a -> a] ``, is an error: it was only another way of writing the prelude's context that has the member,
which the compiler finds for a function's operators anyway (see
[Operators find their context](../advanced/contexts#operators-find-their-context)).

```nanyx
def add: [`+`: a, a -> a] a, a -> a = { x, y -> x + y }  -- error NYX0251: An operator is required by its context, not by its member: write 'Add(a)' for '`+`'
```

To fix it, write the context, which the error's fix does: `[Add(a)]` for `` `+` ``, and `[Num(a)]` for `` `+` `` and
`` `*` `` together. Where a function's operators don't fit one context, list the contexts they need: `[Ord(a), Add(a)]`.

# NYX0252: Type in the scope of something other than one scoped context

A type declaration writes a context before its kind, `type Cursor = [Tx] protected (...)`, to say that each value of
the type belongs to the scope of the `Tx` it was made under (see
[Types in a scope](../advanced/contexts#types-in-a-scope)). That context must be a scoped context, and there can be
only one. A context variable written there instead, `type Thunk(a) = [k] protected (...)`, makes the type generic in
contexts ([Types generic in contexts](../advanced/contexts#types-generic-in-contexts)); a type has only one, and not a
scoped context's scope as well.

```nanyx
type Cursor = [Console] protected (row: int)  -- error NYX0252: 'Console' isn't a scoped context: a type can only belong to a scoped context's scope
```

To fix it, name the scoped context whose scope the values belong to, or leave the contexts out.

# NYX0253: Contexts written on a type that is not a function

Only a function type carries contexts: `[Console] string -> ()` requires `Console` where it's called. Written on any
other type, contexts would say nothing, so they're an error, but on a type generic in contexts, as in
`[Console] Thunk(int)` for `type Thunk(a) = [k] protected (...)`, whose declaration says it takes them. A type whose
values belong to a scoped context's scope says so on its declaration, not where it's used.

```nanyx
def count: [Console] int = 3  -- error NYX0253: Only a function type carries contexts, and 'int' isn't one: write them on the function that requires them
```

A type variable carries no contexts either: `(val: [ToString(a)] a)` doesn't make a value that brings its own
`ToString(a)`. A function that needs `ToString(a)` requires it, as in `[ToString(a)] (a) -> string`.

```nanyx
type Printable(a) = protected (val: [ToString(a)] a)  -- error NYX0253: 'a' is a type variable, and a type variable doesn't carry contexts: write them on the function that requires them
```

To fix it, write the contexts on the function that requires them, or leave them out.

# NYX0254: Attached definition already in scope for its type

An attached definition has a name the type already has in scope: from an import, or, for a built-in type, from the
standard library, whether or not its module is imported. An attached definition can extend a type with a new name,
but never redefine one it has, so `"abc" \length` is always the string's length
([Extending a type](../advanced/attached-definitions#extending-a-type)).

```nanyx
def string.length: string -> int = { s -> 42 }  -- error NYX0254: 'string.length' is already in scope for 'string', from nanyx/text, and an attached definition can extend a type but not redefine what it has: give it another name
```

To fix it, give the definition a name of its own, or, to change how a value behaves, wrap it in a protected type
with its own definitions.

# NYX0255: Operator that does not take two arguments

An operator is defined by a lambda that takes other than two parameters. An operator takes exactly two arguments, its
left and right operands ([Defining operators](../tour/operators#defining-operators)).

```nanyx
def `<+>` = { a, b, c -> a + b * c }  -- error NYX0255: An operator takes exactly two arguments, its left and right operands, but the lambda that defines it takes 3
```

To fix it, take two parameters. To pass an operator more values, make one operand a tuple or a record, as in
`1 <+> (2, 3)`, and take it apart in the operator's body; or define a named function instead.

# NYX0256: Type holding a scoped value without declaring its context

A protected or private type's representation, as its declaration writes it, holds a value of a type in a scoped
context's scope, such as an `MRef`, an `MArray` or a user's `[Tx]` type, but the declaration doesn't write that
context. Like a function type, a type never hides a context: the declaration says which scope its values belong to
(see [Types in a scope](../advanced/contexts#types-in-a-scope)).

```nanyx
import nanyx/mref

type Stack(a) = private MRef(list(a))  -- error NYX0256: 'Stack' holds a value of type 'MRef', which belongs to a 'Memory' scope: declare it `type Stack(a) = [Memory] private ...`
```

To fix it, write the context before `private` or `protected`, which the error's fix does:
`type Stack(a) = [Memory] private MRef(list(a))`.

# NYX0257: Square-bracket literal of a type with no fromArray

A square-bracket literal builds the type expected where it's written, or the type written before the brackets, as
`Array[1, 2]` does, through the type's attached `fromArray`, and this type has none
([Collections](../tour/collections#syntax)).

```nanyx
type Bag = protected (items: list(int))

def bag: Bag = [1, 2]  -- error NYX0257: 'Bag' has no `fromArray`, so a square-bracket literal can't build one: attach one to it, as in `def Bag.fromArray: Array(a) -> Bag`
```

To fix it, attach a `fromArray` to the type, which takes an `Array` of the items, or build the value another way.

# NYX0258: Spread of something that can't be iterated

A spread in a literal, `[...xs, 4]`, copies the elements of something that can be iterated: a list, an array, a set, a
map, a sequence, or a type that attaches `toSeq`, which makes it [`Iterable`](../stdlib/prelude-sequences). A record, a
tag or a function has no elements to spread.

```nanyx
def p = (x = 1, y = 2)
def a = Array[...p, 3]  -- error NYX0258: A spread in a square-bracket literal takes something that can be iterated, such as a list, an array, a set or a sequence, not a '(x: int, y: int)'
```

To fix it, spread a collection of the values, such as `[p.x, p.y]`. A named type that has no `toSeq` is reported as
NYX0205 instead, saying the `toSeq` is missing: attach one to spread it.

# NYX0259: Union with more than one rest variable

A union has at most one rest variable, which stands for the tags it has besides those it names
([Tags](../tour/tags#polymorphic-tag-unions)). A second one would stand for the same other tags, so it is an error where
it is written rather than dropped.

```nanyx
def both: (#some(a) | r), (a -> #some(b) | s) -> #some(b) | r | s = { x, f -> bindOption(x, f) }
-- error NYX0259: A union has at most one rest variable, and this one already has 'r': 's' would stand for the same other tags
```

To fix it, write one rest, and let the unions it is joined from share it: `(#some(a) | r), (a -> #some(b) | r) -> #some(b) | r`.

# NYX0260: Value of a nominal type's shape outside its module

A value of a protected or private type's shape is given where the type is expected, outside the module that defines
the type. Only that module makes values of it ([Nominal types](../advanced/nominal-and-protected-types)), so a record
with the same fields is still not one. The message names the definitions attached to the type that make one.

```nanyx
module Points =
  export type Point = protected (x: int, y: int)

  export def Point.new: int, int -> #some(Point) | #negative = { x, y ->
    if x < 0 or y < 0
      then #negative
      else #some(Point(x = x, y = y))
  }

import Points

def p: Point = (x = 3, y = 4)  -- error NYX0260: 'Point' is protected, so only the module 'Points' makes values of it, and a record of its shape isn't a 'Point' here: make one with 'Point.new'
```

To fix it, make the value with one of the definitions the message names. When there is one, it takes the fields in
order, and it returns the type itself, an editor offers to rewrite the record as a call to it: `Point.at(3, 4)`. A value
of another shape is a type mismatch (NYX0201), whose second line says the same of who makes values of the type.

# NYX0261: Custom pattern inside another pattern

A [custom pattern](../advanced/custom-patterns)'s tag is written inside another pattern, such as a tag's payload or a
tuple's element. A match runs a custom pattern on one of the values it matches, so its tag goes at the top of an
arm's pattern for that value, alone or as a part of an and-pattern there.

```nanyx
pattern int -> #Even | #Odd = { n -> if n % 2 == 0 then #Even else #Odd }

def f = { x ->
  match x
    | #some(#Even) -> 1  -- error NYX0261: `#Even` is a custom pattern, which a match runs on one of the values it matches
    | _ -> 2
}
```

To fix it, take the inner value apart first, and match the pattern on it: `| #some(n) -> match n | #Even -> ...`.

# NYX0262: Custom pattern that requires a context

The body of a [custom pattern](../advanced/custom-patterns) calls something that requires a context, such as
`println`. A pattern is pure, so that it never matters how often a match runs it.

```nanyx
pattern int -> #Loud | #Quiet = { n ->
  println("checking")  -- error NYX0262: 'println' requires the context 'Console', but a pattern is pure
  if n > 100 then #Loud else #Quiet
}
```

To fix it, do the work that needs the context where the match is, in an arm, and keep the pattern to working out
its result.

# NYX0263: `_` as a value outside a pattern's body

`_` is written where a value goes, outside the body of a [partial pattern](../advanced/custom-patterns#partial-patterns).
There, it means "no match", but anywhere else it means nothing.

```nanyx
def nothing = _  -- error NYX0263: `_` as a value means "no match", which only a custom pattern's body can return
```

To fix it, write the value meant, such as `()` for the unit value or `#none` for an absent one.

# NYX0264: Custom pattern given arguments wrongly

A [custom pattern](../advanced/custom-patterns#patterns-with-arguments)'s tag is used with arguments the pattern
doesn't take, without the arguments it does take, with a pattern, such as `_`, where an argument goes, or with an
argument that is no literal, name, or tuple, list or tag of those, such as a call. The message says which.

```nanyx
pattern int -> int -> #DivisibleBy | _ = { divisor -> { n -> n % divisor == 0 } }

def isFizz = { (n: int) ->
  match n
    | #DivisibleBy -> true  -- error NYX0264: `#DivisibleBy` is a pattern that takes arguments
    | _ -> false
}
```

To fix it, write the arguments in the first brackets after the tag, `#DivisibleBy(3)`, and the payload to take apart,
if the pattern returns one, in a second pair: `#SplitOn(",")(first, second)`. A pattern that takes no arguments has
only the payload's brackets. Bind any other value with a `def` before the `match` and pass its name, as an editor's
quick fix does.

# NYX0265: Associated type declared wrongly

A context's [associated type](../advanced/contexts#associated-types) is declared with a default, or with a name that
can't be one: a built-in type's, such as `list`, or one that isn't lowercase and longer than one letter. Or `type` is
written as the type of something other than a context's member.

```nanyx
context Container(c) = (elem: type = int, get: c -> elem)  -- error NYX0265: An associated type can't have a default
```

To fix it, leave the default out: each instance fixes the associated type, by its members' types or with an attached
alias, `type Bag.elem = int`. Name it as a type variable is named, but longer: `elem`, `item`, `failure`.

# NYX0266: Associated type named by more than one context

A signature names an associated type bare, as `elem`, where two contexts in its list have one of that name.

```nanyx
context Iter(c) = (elem: type, next: c -> #some(elem, c) | #done)

def pick: [Iter(c), Iter(d)] c, d, elem -> elem = { xs, ys, fallback -> fallback }  -- error NYX0266: 'elem' is an associated type of 'Iter(c)' and 'Iter(d)'
```

To fix it, write which one, as the message shows: `Iter(c).elem` or `Iter(d).elem`.

# NYX0267: Associated type the context does not have

A constraint fixes an associated type the context doesn't have, as `Iter(c, item = int)`, or one of its members that
isn't a type, as `Iter(c, next = int)`; or a signature names the associated type of a context it doesn't require, as
`Iter(d).elem` in a signature whose list has only `Iter(c)`.

```nanyx
context Iter(c) = (elem: type, next: c -> #some(elem, c) | #done)

def total: [Iter(c, item = int)] c -> int = { xs -> 0 }  -- error NYX0267: 'Iter' has none of this name
```

To fix it, name an associated type the context declares, or add the context whose associated type the signature
names to its list.

# NYX0268: Instance that leaves an associated type unfixed

An instance of a context with an [associated type](../advanced/contexts#associated-types) doesn't say what it is: no
member's type mentions it, and nothing else fixes it.

```nanyx
context Sized(c) = (elem: type, size: c -> int)

type Bag = protected (items: list(int))

def Bag.size: Bag -> int = { bag -> bag.items.length }

def total: [Sized(c)] c -> int = { xs -> size(xs) }

def n = total(Bag(items = []))  -- error NYX0268: The instance 'Sized(Bag)' leaves its associated type 'elem' unfixed
```

To fix it, declare it with an alias attached to the type, `type Bag.elem = int`, or, for an instance a `use` loads,
fix it by name: `use Sized(Bag, elem = int)(...)`.

# NYX0269: Instance that fixes an associated type two ways

Two members of an instance fix one of the context's associated types as different types, or a member fixes it as
another type than the attached alias `type Bag.elem = ...` says.

```nanyx
context Pair(c) = (elem: type, first: c -> elem, second: c -> elem)

type Bag = protected (items: list(int))

def Bag.first: Bag -> int = { bag -> 0 }

def Bag.second: Bag -> string = { bag -> "" }

def go: [Pair(c)] c -> () = { xs -> () }

def ran = go(Bag(items = []))  -- error NYX0269: The instance 'Pair(Bag)' fixes its associated type 'elem' two ways
```

To fix it, make the members agree on the associated type.

# NYX0270: Type parameter declared wrongly

A function's named [type parameter](../tour/functions#type-arguments) whose type names its own name, `into: Type(into)`,
has the name of a built-in type, a default, or `mut`. The name is a type variable of the signature, so it can't be
`list` or `string`, and the caller always passes the type.

```nanyx
def count: (string, list: Type(list)) -> int = { text, list -> text.length }  -- error NYX0270: 'list' is a built-in type, so it can't be the name of a type parameter
```

To fix it, give the type parameter another name, such as `into`, or write its type with a type variable,
`list: Type(a)`, and leave out its default.

# NYX0271: Type argument left out

A call leaves out a [type argument](../tour/functions#type-arguments) its function takes. A type argument is never
left out, even where the type the call gives back says what it is.

```nanyx
context Decode(a) = (decode: string -> a)

def int.decode: string -> int = { text -> text.length }

def deserialise: [Decode(a)] Type(a), string -> a = { t, text -> decode(text) }

def n: int = deserialise("abc")  -- error NYX0271: 'deserialise' takes a type argument, which this call leaves out: a type argument is never left out, so pass `type int`
```

To fix it, pass the type, as the message says: `deserialise(type int, "abc")`.

# NYX0272: Type value not known at compile time

A [type argument](../tour/functions#type-arguments) is chosen with `if` or `match`, or a list holds `Type` values of
different types. A `Type` value is known at compile time, so it can't depend on a value the program has only when it
runs.

```nanyx
def chosen = { big -> if big then type int else type string }  -- error NYX0272: A type argument can't be chosen with `if`: a `Type` value is known at compile time, so pass `type X` where it is used
```

To fix it, call the function that takes the type argument in each branch, passing each its own type.

# NYX0273: Match on a type's description with a pattern it can't have

A `match` on a `Type` value has an arm whose pattern isn't one of the tags of a type's description, such as `#int`,
`#list(elem)` or `#record(r)`, or matches a tag's payload with something other than a name or `_`.

```nanyx
def kind: Type(a) -> string = { t ->
  match t
    | #integer -> "a number"  -- error NYX0273: '#integer' is not a tag of a type's description: a match on a `Type` is matched by the tags of the type's description, such as `#int`, `#list(elem)` or `#record(r)`, or `else`
    | else -> "something else"
}
```

To fix it, use the tags the [type's description](../tour/functions#inspecting-a-type) has, and bind a payload to a
name.

# NYX0274: A packaged type leaves the function that opened it

A value whose type is a record field's, a union case's or a collection element's, which the checker never learns,
leaves the code that opened it: a lambda over a record's fields gives back a field's value, or a `#list(elem)` arm
gives back `elem`. Outside, it could be given where another field's type is expected.

```nanyx
def values: a, Type(a) -> int = { value, t ->
  match t
    | #record(r) ->
        def got = r.fields \list.map { f -> f.get(value) }  -- error NYX0274: This gives a value of 'b', whose type is the type of a field, a case or an element that is only known here: use it here, as in `walk(f.get(value), f.type)`, rather than giving it back
        0
    | else -> 0
}
```

To fix it, use the value where it is opened, with the field's `type`.

# NYX0275: Component root provides other contexts than its core root

A host's [component root](../advanced/hosts#writing-a-host), which `nanyx build` builds a WebAssembly component from,
provides a context in its `run` that the host's core root doesn't, or doesn't provide one the core root does. A program
that runs under the one would then not run under the other. It is reported at the component root's `run`, when a program
is built as a component.

```nanyx fragment
export module shout.host.wasi

import (
  nanyx/host.runtime
  shout.log as (Log)
  shout.extra as (Extra)
)

-- error NYX0275: The component root's 'run' provides 'Extra', which the core root 'acme.shout/shout.host' doesn't: ...
export def run: [HostRuntime] ([Extra, Log] () -> a) -> () = { entry ->
  ignore(entry())
}
```

To fix it, provide the same contexts in both roots.

# NYX0276: Foreign function of the compiler's own module

A foreign function imports from `nanyx:component`, whose functions the compiler defines itself in a component's core
module, for a host's component root to take the request it answers and give the response
([Components](../deep-dives/components#the-handler)). Only a host's package, and the host libraries it depends on, may
declare one. It is reported at the declaration.

```nanyx
import nanyx/host.runtime

-- error NYX0276: 'argument' imports from "nanyx:component", which is the compiler's own, for a host's component root: ...
@extern("nanyx:component", "argument")
def argument: [HostRuntime] () -> I32
```

To fix it, import from another module: the one whose function you mean.

# NYX0277: Type that can't cross a component's boundary

A def marked `@component`, which the component `nanyx build` writes exports, has a type that no component can pass:
a function, a context, a type variable, a record or tag union with no name, which WIT names, a union of types other
than tags, or a recursive type, which WIT can't write. Or it isn't a function
at all, and a WIT interface exports only functions and types
([Components](../deep-dives/components#a-programs-own-exports)). It is reported at the def.

```nanyx
@component
export def apply: (int -> int) -> int = { f -> f(1) }  -- error NYX0277: A component can't export 'apply': a function can't cross a component's boundary. ...
```

To fix it, pass the values the function would compute instead, name a record or tag union with a `type`, tag the
members of a union, and write the type a type variable stands for.

# NYX0278: Component import called outside a component

A program calls a def marked `@import`, a function another component exports, in a program that isn't built as a
component: `nanyx run`, `nanyx test`, `nanyx wasm`, or `nanyx build --core`. Only the component `nanyx build` writes imports the
function, and a runtime that runs it gives the other component's
([Components](../deep-dives/components#a-programs-own-imports)). It is reported at the def.

```nanyx
@import("acme:geometry/shapes@1.0.0")
def area: float -> float  -- error NYX0278: 'area' imports 'area' from acme:geometry/shapes@1.0.0, ...

println(area(2.0))
```

To fix it, build the program with `nanyx build` and run the component, or test what calls the import by passing the
function in.

# NYX0279: Task that holds what belongs to a memory block

A task is started with a function that uses a `memory` block's cells or references (`MRef`, `MList`), by `start`, by
`race`, or by anything that starts a task with the function it is given, such as a local `def spawn = { f -> start(f) }`
or `start` under another name. Tasks share only immutable values, so that another task can never change what one holds
while it waits, and a task can't run on once the block has freed what it holds
([Async](../advanced/async#tasks-share-only-values)).

```nanyx
def counts = { _ ->
  memory {
    mut total = 0
    def t = start! total := total + 1  -- error NYX0279: A task can't hold what belongs to a 'memory' block: ...
    await(t)
    total
  }
}
```

To fix it, give the task what the cell holds, and change the cell with what the task gives back:
`def t = start! now + 1`, after `def now = total`, then `total := await(t)`.

# NYX0280: Lambda parameter that names no field

A lambda is given where a function whose input has named fields is expected, as in `(x: int, y: int) -> int`, and one
of its parameters is not one of the fields' names. Such a lambda takes the fields by name, in any order, so
`{ x, y -> ... }` and `{ y, x -> ... }` both work, but `{ a, b -> ... }` takes nothing.

```nanyx
def sum: (x: int, y: int) -> int = { a, b -> a + b }  -- error NYX0280: The parameter 'a' is not a field of the function's input, ...
```

To fix it, name the parameters after the fields, as in `{ x, y -> x + y }`, or rename the fields with a record pattern,
in brackets: `{ (x = a, y = b) -> a + b }`.

# NYX0281: Cell declared at the top level

A cell is declared with `mut` at the top level. A cell needs the context `Memory`, which only a `memory { ... }` block
provides, and no block can be around a top-level definition ([Memory](../advanced/memory#no-global-mutable-state)).

```nanyx
mut count = 0  -- error NYX0281: A cell can't be declared at the top level, where no `memory` block can be around it: ...
```

To fix it, declare the cell inside a `memory { ... }` block, in the body of a def, and give back what it ends up
holding: `def count = memory { mut n = 0 ... n }`.

# NYX0282: Type parameter written the old way

A function's parameter that takes a [type argument](../tour/functions#type-arguments) is written the old way, `type a`
for a positional one or `into: type` for a named one, which earlier releases took with a warning. A parameter that takes a
type is written with its type, `Type(a)`, as any other parameter is, so `name: type` only ever declares a context's
[associated type](../advanced/contexts#associated-types).

```nanyx
context Decode(a) = (decode: string -> a)

def int.decode: string -> int = { text ->
  text.length
}

def deserialise: [Decode(a)] type a, string -> a = { t, text ->
  decode(text)
} -- error NYX0282: A parameter that takes a type is written with its type, `Type(a)`: write `Type(a)`, not `type a`

println(deserialise(type int, "abc"))
```

To fix it, write `Type(a)` for `type a`, and `into: Type(into)` for `into: type`, as the editor's quick fix does. Calls
don't change: they still pass `type int`.

# NYX0283: Value of a type only a later use fixes

A def that writes no type and whose value is not a function calls a function that requires a context of the type it
gives back, and nothing in the def says what that type is. A later use of the value, such as
`def person: Person = obj`, doesn't fix it: a value's type is fixed where it is defined.

```nanyx
type Person = (name: string)

context Decode(a) = (decode: string -> a)

def Person.decode: string -> Person = { s ->
  Person(name = s)
}

def parse: [Decode(a)] string -> a = { json ->
  decode(json)
}

def obj = parse("Ada")  -- error NYX0283: 'parse' requires 'Decode' of a type nothing in the definition of 'obj' fixes, ...
```

To fix it, write the type on the def, `def obj: Person = parse("Ada")`, or give the function a
[type argument](../tour/functions#type-arguments) and pass the type: `parse(type Person, "Ada")`.

# NYX0284: Char literal that is not one character

A string literal is written where a `char` is expected, and it is not exactly one Unicode scalar value. A `char` is
one scalar value, so the literal must be one: not empty, not two characters, not a literal with holes, and not a
letter written with a combining accent after it, which is two scalar values however it looks
([Characters](../tour/types#characters)).

```nanyx
def initial: char = "Ab"  -- error NYX0284: A char is one Unicode scalar value, ...
```

To fix it, write one character, or use a `string` where the value is more than one.

# NYX0286: Dimension whose units aren't defined from one

A dimension's units are defined from one unit of it, the one that is no multiple of another. A dimension has no units,
two of its units are no multiple of another, or a unit of a dimension declared in another module is no multiple of a
unit of it ([Units of measure](../advanced/units-of-measure#dimensions-and-units)).

```nanyx
dimension Time  -- error NYX0286: 'Time' has no units: ...
```

To fix it, declare one unit of the dimension with no multiple, as in `unit ns: Time`, and define every other unit as
a multiple of a unit of the dimension, as `unit ms: Time = 1_000_000ns` is.

# NYX0287: Unit attribute that can't apply

A unit is marked `@baseUnit` or `@storageUnit` where the mark can't apply: its dimension has a unit marked so already,
or its dimension is declared in another module, which decides its default storage and display units.

```nanyx
dimension Time

@baseUnit unit ns: Time
@baseUnit unit s: Time = 1_000_000_000ns  -- error NYX0287: 'Time' has a unit marked `@baseUnit` already: ...
```

To fix it, mark one default display unit and at most one default storage unit for each dimension, in the dimension's
own module.

# NYX0288: Units defined in a circle

A unit is defined as a multiple of itself, directly or through other units, so it is no multiple of the unit the
others are defined from.

```nanyx
dimension Time

@baseUnit unit ns: Time
unit a: Time = 2b  -- error NYX0288: 'a' is defined as a multiple of itself, through 'b', ...
unit b: Time = 3a  -- error NYX0288: 'b' is defined as a multiple of itself, through 'a', ...
```

To fix it, define one of the units as a multiple of another that is not defined through it, such as the one the
others are defined from.

# NYX0289: Unit defined in another dimension's units

A unit is defined as a multiple of a unit of another dimension. A unit of a dimension is a multiple of another unit of
the dimension, so it is defined in that dimension's units.

```nanyx
dimension Time

@baseUnit unit ns: Time

dimension Length(float)

@baseUnit unit m: Length
unit far: Length = 3ns  -- error NYX0289: 'far' is a unit of 'Length', but 'ns' is a unit of 'Time': ...
```

To fix it, define the unit as a multiple of a unit of its own dimension.

# NYX0290: Dimension stored in a type that isn't a number

A dimension is constrained to a storage type, as `dimension Bytes(int)` is, that has no `Num` instance. A dimension's
values are numbers, added, scaled and compared.

```nanyx
dimension Flags(bool)  -- error NYX0290: A dimension is stored as a number, but 'bool' has no `Num` instance: ...

@baseUnit unit f: Flags
```

To fix it, store the dimension as a number type, such as `int` or `float`, or leave it unconstrained, `dimension Flags`,
to store it as any.

# NYX0291: Unknown unit

A number is written with a unit, or a unit is defined as a multiple of one, that the module does not know: it declares
no unit of that name, and imports none.

```nanyx
dimension Time

@baseUnit unit ns: Time
unit ms: Time = 1_000_000ns
unit min: Time = 60_000mss  -- error NYX0291: There is no unit 'mss' here: did you mean 'ms'?
```

To fix it, correct the unit's name, declare it with `unit`, or import the module that exports it.

# NYX0292: Negative power of a type whose fraction is another

`x ** -n` is written on a value of a type whose `/` gives another type, its `fraction`
([Num(a)](../advanced/contexts#numa)). A negative power is `1 / x ** n`, which is a `fraction`, while `**` gives a value
of the type itself, as it does on `float` and `F32`, whose fraction is themselves.

```nanyx
type Count = protected (n: int)

def Count.zero: Count = Count(n = 0)
def Count.`+`: Count, Count -> Count = { a, b -> Count(n = a.n + b.n) }
def Count.`-`: Count, Count -> Count = { a, b -> Count(n = a.n - b.n) }
def Count.negate: Count -> Count = { a -> Count(n = -a.n) }
def Count.`*`: Count, Count -> Count = { a, b -> Count(n = a.n * b.n) }
def Count.`/`: Count, Count -> float = { a, b -> a.n / b.n }
def Count.`//`: Count, Count -> Count = { a, b -> Count(n = a.n // b.n) }
def Count.`%`: Count, Count -> Count = { a, b -> Count(n = a.n % b.n) }
def Count.fromInt: int -> Count = { n -> Count(n = n) }
def Count.isInteger: bool = false

def half = Count(n = 2) ** -1  -- error NYX0292: A negative power of a 'Count' is '1 / x ** n', a 'float', ...
```

To fix it, write the fraction as a division, `Count.fromInt(1) / x ** 1`, whose type is the `fraction`. A negative exponent that is
not a literal stops the program when it runs.

# NYX0293: Dimension's storage that no parameter fixes

A signature names a dimension whose storage, the number type that holds its values, is a type variable that no
parameter's type has, so nothing a call passes says what it is. A dimension written without its storage in a function's
result is of the storage its body makes it ([Units of measure](../advanced/units-of-measure#dimensions-in-types)), but a
foreign function, declared with `@extern`, has no body to say.

```nanyx
dimension Time

@baseUnit unit ns: Time

def make: int -> Time(a) = { n -> make(n) }  -- error NYX0293: The number that stores 'Time' here is not the type of a parameter, ...
```

To fix it, write the storage as a parameter's, as in `Time(a) -> Time(a)`, write a storage, as in `Time(int)`, or, in
a function with a body, leave it out, as in `int -> Time`.

# NYX0294: Storage written for a constrained dimension

A dimension constrained to one storage, as `dimension Bytes(int)` is, is written with a storage. It is always stored as
the one it is constrained to, so it is written without one.

```nanyx
dimension Bytes(int)

@baseUnit unit B: Bytes

def size: Bytes(a) -> Bytes = { b -> b }  -- error NYX0294: 'Bytes' is constrained to one storage, so it is written without one: ...
```

To fix it, write the dimension alone: `Bytes -> Bytes`.

# NYX0295: Parts of a derived dimension stored differently

The parts of a derived dimension, `Length / Time`, are written with two storages. A value of a derived dimension is one
number, so its parts share one storage. A dimension that names a derived one, `dimension Speed = Length / Time`, is
generic in its storage as its parts are, so its parts are written without one.

```nanyx
dimension Length

@baseUnit unit m: Length

def speed: Length(a) / Time(float) -> int = { _ -> 1 }  -- error NYX0295: The parts of 'Length / Time' are stored as 'a' and as 'float', ...
```

To fix it, write one storage on every part, `Length(float) / Time(float)`, or on none, `Length / Time`.

# NYX0296: Part of a derived dimension that is no dimension

A derived dimension multiplies or divides a type that is no dimension. Only dimensions are multiplied and divided in a
type: a number with no unit is a plain number, not a dimension.

```nanyx
dimension Length

@baseUnit unit m: Length

def f: Length / int -> int = { _ -> 1 }  -- error NYX0296: 'int' is no dimension, ...
```

To fix it, write dimensions on both sides of `*` and `/`, or leave the plain number out of the type.

# NYX0297: Unit of a derived dimension that its parts' units don't define

A unit of a derived dimension is declared with no definition, or marked `@baseUnit` or `@storageUnit`. A derived
dimension's values are stored in its parts' default storage units and displayed in their default display units, so it
has no default storage or display unit of its own: each of its units is defined from its parts' units.

```nanyx
dimension Length

@baseUnit unit m: Length

dimension Speed = Length / Time

unit kmh: Speed  -- error NYX0297: 'Speed' is a derived dimension, whose units are multiples of its parts' default storage units: ...
```

To fix it, define the unit from its parts' units, multiplied and divided: `unit kmh: Speed = 1km / 1hr`.

# NYX0298: Unit worth nothing

A unit is defined as nothing, as `0ms` is, or divided by nothing. No number of it would be a quantity.

```nanyx
dimension Length

@baseUnit unit m: Length

dimension Speed = Length / Time

unit instant: Speed = 1m / 0s  -- error NYX0298: `0s` is nothing, so 'instant' would be no quantity at all: ...
```

To fix it, define it from a number of units that isn't zero.

# NYX0299: Dimension with no unit to store it in

A dimension's values are stored as a number of its default storage unit: the largest unit that every unit declared
with the dimension is a whole number of. No declared unit is one, so there is none to store its values in
([Units of measure](../advanced/units-of-measure#dimensions-and-units)). An inch is 25.4 millimetres, so neither is a
whole number of the other.

```nanyx
dimension Distance  -- error NYX0299: No unit of 'Distance' is one that every other is a whole number of, ...

unit mm: Distance
unit inch: Distance = 25.4mm
```

To fix it, declare the unit the message names, which every unit of the dimension is a whole number of:
`unit tenthMm: Distance = 0.2mm`.

# NYX0300: Unit that is no whole number of the storage unit

A unit is no whole number of its dimension's default storage unit, where it must be one: the dimension marks its
default storage unit `@storageUnit`, or is declared in another module, so a unit declared here mustn't change what a
value of it holds ([Units of measure](../advanced/units-of-measure#dimensions-and-units)).

```nanyx
dimension Clock

@storageUnit unit ns: Clock
unit ps: Clock = 0.001ns  -- error NYX0300: 'ps' is 1/1000 of 'ns', the default storage unit of 'Clock' that `@storageUnit` marks, ...
```

To fix it, define the unit as a whole number of the default storage unit, or, in the dimension's own module, mark a
smaller unit `@storageUnit`, or none, to have the compiler work it out.

# NYX0304: Call with the wrong number of arguments

A call gives a function more or fewer arguments than it takes. A pipe's value counts as the first of them.

```nanyx
def add: int, int -> int = { a, b -> a + b }

def three = add(1)  -- error NYX0304: 'add' takes 2 arguments, but this call gives 1
```

To fix it, give the function as many arguments as it takes. A function of several parameters can also be given a
tuple of them, as in `add(pair)` where `pair` is `(1, 2)`.

# NYX0305: Call of a value that isn't a function

A value is called, but its type is known and is no function: a number, a string, a `bool`, a record or tuple, a tag,
or a list. A map and an array can be called, to look up an item.

```nanyx
def limit = 5

def twice = limit(2)  -- error NYX0305: 'limit' is an int, not a function, so it can't be called
```

To fix it, call a function, or use the value without calling it.

# NYX0310: Test that isn't a test's type

A def marked `@test` takes arguments or gives back a value. A test is run with nothing and gives back `()`: it is a
block, as `{ assert(...) }` is, that checks what it is about with `assert`.

```nanyx
@test
def positive: int -> () = { n -> assert(n > 0) }  -- error NYX0310: A test takes no arguments and gives back `()`, ...
```

To fix it, write the test as a block that makes its own values: `def positive = { assert(3 > 0) }`.

# NYX0311: Reference to a test

Code that isn't a test refers to a def marked `@test`. Tests are part of a test build alone, which `nanyx test` makes,
so no other build has them. Another test may call one.

```nanyx
@test
def adds = {
  assert(1 + 1 == 2)
}

def check = { adds() }  -- error NYX0311: 'adds' is a test, which only a test build has, ...
```

To fix it, move what the test and the other code share into a def that isn't marked `@test`, and call that from both.

# NYX0312: Context the test host doesn't provide

A test requires a context that the test host doesn't provide it. A test runs under the test host, which provides
`Async`, `Console` and `Random` only, so any other context a test requires it must provide itself, even one its program's own
host provides.

```nanyx
@test
def readsArguments = {  -- error NYX0312: The test 'readsArguments' requires the context 'Env', which the test host does not provide: ...
  assert(args() == [])
}
```

To fix it, load an instance of the context in the test with `use`, such as a fake that records what the code does
with it, and assert on what it recorded. A test that is meant to read the package's files or the environment can be
marked `@test(#io)` instead, which runs it with the console host's real `Console`, `Env` and `FileSystem`:
where every context a test lacks is one of those, the message says so, and an editor offers to mark it.

# NYX0301: Not supported by the WebAssembly backend

The program type-checks, but uses something the WebAssembly backend cannot compile yet. The message names what it
is.

```nanyx
type Nest(a) = #leaf(a) | #deeper(Nest(list(a)))

def depth: Nest(int) -> int = { t -> 1 }  -- error NYX0301: It can't compile a recursive type that holds itself at other type arguments yet
```

To fix it, write the code another way until the backend supports it.

# NYX0302: Internal error in the WebAssembly backend

The WebAssembly backend found something it should never be given. This is a bug in the compiler, not in the program.

Please [report it](https://github.com/nanyx-lang/nanyx/issues), with the program that causes it.
