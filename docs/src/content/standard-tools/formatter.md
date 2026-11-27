---
title: "Formatter"
description: "Formatting Nanyx code with the standard formatter"
order: 2
---

Nanyx includes a standard formatter for producing consistent code layout across teams and projects.

The formatter focuses on whitespace, indentation, line wrapping, and other presentational concerns. It does not change program behavior. It only formats code that parses, so it can't add the spaces an operator needs: `x+y` is an error to fix by hand, though `x  +   y` becomes `x + y` ([Operators](../tour/operators#the-operators)).

The formatter is available as a command line tool and can be integrated into editor workflows.

The formatter is minimally configurable by design, with the goal of keeping style consistent across contributors and reducing formatting-only diffs in reviews. It also works well with linter guidance and editor tooling to help you write clean, idiomatic Nanyx code.

# Why use it

- Keeps style consistent across contributors
- Reduces formatting-only diffs in reviews
- Works well with linter guidance and editor tooling

# Usage modes

The formatter is available as a command within the Nanyx CLI. You can run it in different modes:

## Check

Check mode verifies that files are already formatted according to the standard style. It exits with a non-zero status if any files would be changed by formatting.

```bash
# Check all files in the current working directory
nanyx format --check

# Check all files in the `src/` directory
nanyx format --check src/

# Check specific files
nanyx format --check src/file1.nyx src/file2.nyx
```

## Write

Write mode applies formatting changes to the specified files. This is the default mode if no flags are provided.

```bash
# Format all files in the current working directory
nanyx format
# or
nanyx format --write

# Format all files in the `src/` directory
nanyx format src/

# Format specific files
nanyx format src/file1.nyx src/file2.nyx
```

## Standard input

`--stdin` formats what it reads from standard input and writes the result to standard output, so an editor can format a buffer without saving it first. The input is formatted as a file in the current directory, with that directory's settings; run it from the file's directory. If the input can't be formatted, nothing is written to standard output and the error goes to standard error.

```bash
nanyx format --stdin < src/file1.nyx
```

## Files and exit codes

A directory means every `.nyx` file under it, leaving out `bin`, `obj` and `node_modules`, whatever their case, and hidden directories. Each file keeps its line endings.

A file that does not parse is left as it is and its syntax error is reported. The formatter also parses its own output before writing it, and leaves a file alone if the result would not mean exactly the same as before.

`nanyx format` exits with `0` when it has nothing to report, `1` when `--check` finds a file that is not formatted, and `2` when a file could not be formatted.

# The style

The formatter uses two spaces of indent and a line width of 100, unless the package [configures](#configuration)
others. Indent is always measured from the start of the line, never from a keyword's column. It puts one space around
binary operators and `->`, after commas, and inside a lambda's braces: `{ a, b -> a + b }`. It aligns nothing but the
`|`s of a match's cases and the columns of a [table literal](../getting-started/basics#table-literals), so that one edit never changes the lines around it,
but for a table, whose columns are the point of writing one.

It never renames anything or reorders anything but imports. It changes whitespace and where lines break, puts back
the brackets precedence needs and drops those it doesn't, and adds or removes blank lines.

## Definitions

A definition whose signature fits on the def's line stays on it, even when its value spans lines. One whose signature
doesn't fit breaks before its `:` and its `=`, each indented once:

```nanyx fragment
def priceLine
  : list(Product), OrderLine -> #some(Priced) | #unknownSku(string) | #outOfStock(string)
  = { products, line ->
    find(products, line)
  }
```

A type that doesn't fit on its `: ` line breaks too, each line after the first starting at the column after the `: `.
The parameters go one per line, each but the last followed by `,`, then `->` and the result on a line of their own. A
result that is a union still too long for its line has one case per line, each after a `|`, as a long union type's
cases are:

```nanyx fragment
def lookup
  : string,
    Index
    -> | #some(Entry)
       | #notFound
       | #expired(Entry, Timestamp)
       | #locked(user: string, until: Timestamp)
       | #failed(string)
  = { key, index ->
    find(index, key)
  }
```

A list of contexts goes on a line of its own, with the rest of the type on the next, and breaks one context per line,
under the first, if it still doesn't fit:

```nanyx fragment
def serve
  : [Console, FileSystem, Http, Clock, Random, Environment, Log, Metrics]
    Request, ServerOptions -> Response
  = { request, options ->
    respond(request)
  }
```

Each part breaks only if it doesn't fit once the parts around it have broken, so above, the parameters stay on one
line; ones that still didn't fit would go one per line under the contexts. A list of contexts still too long for its
line has one context per line, under the first, with the `]` after the last. A union that is the whole type, as a
value's may be, breaks as a result does, its first `|` after the `: `. Only a function's outer arrow breaks: in
`int -> string -> bool`, `-> string -> bool` goes on a line of its own, and only a union result breaks further. A
type inside a parameter, such as a function's, is never broken: the parameter keeps a line of its own. The
[info view](vs-code-extension#info-view) lays a signature out this way at its own width.

A def with a type annotation whose value is a lambda puts the lambda's body on the line after the `->`, unless the
body is only a name or a literal, or a statement couldn't hold it without brackets, as with a range:

```nanyx
def heading: [Console] string -> () = { title ->
  println("== " ++ string.uppercase(title) ++ " ==")
}

def id: a -> a = { x -> x }

def upTo: int -> seq(int) = { n -> 1..n }
```

A type or context whose body is a record of more than three fields, or one too long for the line, puts each field on
its own line:

```nanyx
type Product = (
  sku: string
  name: string
  price: int
  stock: int
)
```

A union written over more than one line, or too long for one, puts each case on its own line, starting with `|` and
indented once. A union written on one line that fits stays on it:

```nanyx
type Shape =
  | #circle(radius: float)
  | #rectangle(width: float, height: float)

type Answer = #yes | #no
```

Any other type too long for the line goes on the line after the `=`, indented once.

## Lambdas and matches

A lambda with match cases puts each case on its own line, the `|`s one under another, indented once from the lambda's
line, and the `}` on a line of its own. A single case that fits stays on the lambda's line: `{ | 0 -> 1 }`. A `for`
loop or an `except` with one arm keeps it on the head's line, `for xs | x -> body`; with several arms, each goes on a
line of its own, indented once.

A lambda whose body starts on the `->`'s line but spans lines indents the body once, and puts its `}` back at the
indent of the line the lambda starts on, so two closing braces never line up:

```nanyx fragment
def checkOrder = { products, lines -> proc {
    match lines
      | [] -> (return [])
      | [first, ...rest] -> ...
  }
}
```

## Long lines

A record, list, tuple or call's arguments that don't fit put each element on its own line, separated by line breaks
rather than commas, with the closing bracket on a line of its own. The outermost brackets break first. A record,
tuple or call's arguments with an element that spans lines, such as a lambda with a block, is laid out the same way:

```nanyx fragment
use Log(
  log = { message ->
    println(message)
  }
)
```

A pipeline written with a stage at the start of a line, or one of two or more stages that doesn't fit, puts each stage
on its own line, indented once from the line the pipeline starts on. One written on one line that fits stays on it.

An `if` whose branches are expressions, and that doesn't fit, puts `then` and `else` at the start of lines of their
own, indented once:

```nanyx fragment
if line.qty > p.stock
  then #outOfStock(p.name)
  else #some(Priced(name = p.name, qty = line.qty, each = p.price, cost = p.price * line.qty))
```

A chain of operators of one precedence that doesn't fit puts as many operands on a line as fit, each line ending in an
operator and the lines after the first indented once. A line can break after an operator only inside brackets, so in
a statement, an arm's body or a top-level expression, a chain stays on one line.

## Blank lines

A construct that spans more than one line gets a blank line before and after it, at the top level, in a module block
and among the statements of a block, except at the start and end of a body. Between constructs that each fit on one
line, your choice is kept: a blank line stays, several become one, and none is added.

```nanyx
def zero = 0
def one = 1

def add: int, int -> int = { a, b ->
  a + b
}

def two = 2
```

Blank lines inside an expression, between a lambda's match cases or a record's fields, are removed. Imports in a row
stay together, and an attribute such as `@entry` stays on the line before its item.

# Imports

Imports in a row go in one bracketed `import (...)`, and their names are sorted: standard library modules (`nanyx/...`) first, then other packages' modules, then imports by path, each alphabetical. A blank line keeps groups of imports apart, and a comment on a line of its own (or a `---` block comment) starts a new group inside the brackets. A comment at the end of an import's line moves with it.

```nanyx unformatted
-- before
import nanyx/text
import "./tokens.nyx" as tokens
import nanyx/math -- for sqrt

-- after
import (
  nanyx/math -- for sqrt
  nanyx/text
  "./tokens.nyx" as tokens
)
```

# Statements separated by `;`

Statements written on one line, separated by `;` ([Variables](../tour/variables#several-statements-on-one-line)), stay on that line while it fits the line width, with one space after each `;`. A line that doesn't fit has its statements put on lines of their own, and a match arm's, a branch's or a lambda's body becomes a block on the lines after it.

```nanyx unformatted
-- before
def f = { x -> log(x) ;x + 1 }

-- after
def f = { x -> log(x); x + 1 }
```

# Comments before a def's `=`

A def's `=` may start the line after its signature, with comments between them: at the end of the signature's line, or on lines of their own. The formatter keeps those comments where they are, and the `=` on a line of its own after them.

```nanyx
def scale: int, int -> int -- times k, plus x
  = { x, k ->
    x * k + x
  }
```

# Comments before a lambda's `}`

A comment before the `}` of a lambda whose body starts on the `{`'s line stays inside the lambda. The body goes on a line of its own, with the comment after it, and the `}` on the next line. A `match` body keeps its arms on lines of their own, with the comment after the last, and a shorthand lambda such as `{ .length }` or `{ \reverse }` keeps its line.

```nanyx unformatted
-- before
def next = { x -> x + 1 -- the next one
}

-- after
def next = { x ->
  x + 1 -- the next one
}
```

# Comments at the end of a def's line

A comment at the end of a def written on one line stays on the def's first line when the formatter puts its body on lines of its own: after the `{` or `->` that ends that line, not after the `}`. A comment written there, after a lambda's `{` or `->`, stays there too.

```nanyx unformatted
context Tx = (commit: () -> ())

-- before
def viaTx: [Tx] () -> () = { commit() } -- a Tx serves

-- after
def viaTx: [Tx] () -> () = { -- a Tx serves
  commit()
}
```

# Typical workflow

Format files before committing changes, and enable format-on-save in your editor where possible.

We also recommend adding a `nanyx format --check` step to your CI pipeline to ensure that all committed code adheres to the standard style when trying to merge a PR.

# Examples

```nanyx unformatted
-- before
def add={a,b->a   +  b}

-- after
def add = { a, b -> a + b }
```

```nanyx unformatted
-- before
def mapSome: (#some(a) | r), (a -> b) -> (#some(b) | r) = {
    | #some(a), f -> #some(f(a))
      | other, _ -> other }

-- after
def mapSome: (#some(a) | r), (a -> b) -> #some(b) | r = {
  | #some(a), f -> #some(f(a))
  | other, _ -> other
}
```

A definition that doesn't fit on one line breaks before its `:` and its `=`:

```nanyx unformatted
-- before
def priceLine: list(Product), OrderLine -> #some(Priced) | #unknownSku(string) | #outOfStock(string) = { products, line -> find(products, line) }

-- after
def priceLine
  : list(Product), OrderLine -> #some(Priced) | #unknownSku(string) | #outOfStock(string)
  = { products, line ->
    find(products, line)
  }
```

A pipeline that doesn't fit puts each stage on its own line, and a record, list, tuple or call's arguments that don't fit put each element on its own line, with no commas between them:

```nanyx unformatted
-- before
def report = orders \list.filter { o -> o.paid and o.total > 100 } \list.map { o -> Summary(id = o.id, customer = o.customer.name, total = o.total, placed = o.placedAt, items = o.lines.length) } \list.take(10)

-- after
def report = orders
  \list.filter { o -> o.paid and o.total > 100 }
  \list.map { o -> Summary(
      id = o.id
      customer = o.customer.name
      total = o.total
      placed = o.placedAt
      items = o.lines.length
    )
  }
  \list.take(10)
```

# Configuration

The formatter has a small set of configuration options that can be set in the `formatter` field of the package's `.nyxp` manifest. These options are intentionally limited to keep style consistent across contributors and projects.

```nanyx fragment
formatter = (
  -- The maximum line width before wrapping occurs (default: 100)
  lineWidth = 100
  -- The number of spaces to use for indentation (default: 2)
  indentSize = 2
)
```

Each file is formatted with the settings of the nearest `.nyxp` in its directory or a directory above it, so one run can format several packages, each in its own style. With no manifest, or no `formatter` field in it, the defaults apply. The field is a record that may hold only these two settings, each a positive integer: anything else, or a manifest that holds code, is an error naming the manifest and the setting, and no file under that manifest is formatted (`nanyx format` exits with 2). The language server reads the same settings for the document it formats, and leaves the document alone if they are not valid.

A script outside any package sets them with a `::format` [directive](../getting-started/files-and-projects#directives) at its top, one per setting: `::format lineWidth 80`.

# In the editor

The VS Code extension formats a document as `nanyx format` does, formats a selection, and marks the lines formatting would change as you type (see [Formatting](./vs-code-extension#formatting)).

# Related

See [Official style linter](./official-linter) for style diagnostics that complement formatting.
