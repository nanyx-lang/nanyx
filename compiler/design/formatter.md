# Formatter: workstream

> **Status: proposal, partly decided.** This page frames the formatter workstream and proposes how `nanyx format` is
> built. The user's side is already described in the docs. Configuration, blank lines, import sorting and
> unparseable files were decided on 2026-11-07 (see [Decided](#decided)); the style rules are still open. Work is
> tracked under the `formatting` label.

## Summary

Nanyx should ship with one official formatter, `nanyx format`, so that there is one layout for Nanyx code and nobody
argues about it. The [Formatter](../../docs/src/content/standard-tools/formatter.md) page already describes what
users see: a write mode (the default) and a `--check` mode, files or directories as arguments, a line width of 100
and an indent of 2, and a `[formatter]` table in `.nyxpack` with those two settings and nothing else.

This page is about what that page leaves out: how the formatter keeps comments the parser throws away, how it
decides where lines break, and how it guarantees it never changes what a program means.

## Scope

| Area | Where |
|------|-------|
| Comments and blank lines | Kept by the parser alongside the syntax tree, so the formatter can put them back |
| Printing | A new `compiler/Nanyx.Formatter` project: a syntax tree in, Nanyx source out |
| Style | The layout rules for each construct, written down here and tested |
| CLI | `nanyx format` and `nanyx format --check` |
| Editor | Formatting a document from the language server, and format-on-save in the VS Code extension |
| The repository | Formatting the Nanyx code in `examples/`, `stdlib/` and the docs, and checking it in CI |

Out of scope here:

- Style diagnostics (naming, unused code, preferring one construct to another): the
  [official analyzer](../../docs/src/content/standard-tools/official-analyzer.md), in the analyzers workstream. The
  formatter only moves whitespace and comments, adds or removes parentheses and blank lines, and sorts imports. It
  never renames anything or reorders anything else.
- Reading the rest of `.nyxpack`: the packaging workstream. The formatter reads only its `[formatter]` table.

## Where the compiler is today

- **Comments are discarded.** The parser (FParsec, `compiler/Nanyx.Parser/Program.fs`) skips `--` comments as
  whitespace (`ws`, `comment`, `inlineCommentNoNl`). Nothing records where they were. Blank lines are skipped the
  same way. `---` block comments do not parse yet (#205).
- **The syntax tree has spans.** Every expression, pattern, type expression, statement and top-level item carries a
  `SourceSpan`, and equality on them compares `Kind` only, ignoring spans. That makes "the formatted file parses to
  the same tree" a one-line check.
- **Some source text is lost.** Parentheses are not kept (`tupleOrParenExpr` returns the inner expression), and
  literals are stored by value (`IntLit of int`, decoded `StringLit`), so `0xFF`, `1_000` or a string's escapes
  cannot be printed back from the tree. The spans can recover them from the source text.
- **The CLI** (`compiler/Nanyx.Compiler.Cli/Program.cs`) has `parse`, `check`, `wasm`, `run`, `hello` and `lsp`.
  There is no `format`.
- **The language server** does not offer formatting.

## Proposal

### Keeping comments

The parser records every comment and every run of blank lines it skips, with its span, in a side list returned with
the syntax tree. The tree itself does not change, so the type checker and code generator are untouched. The formatter
attaches each comment to a node:

- A comment on the same line after code is a *trailing* comment of the last node that ends on that line, and stays
  on that line.
- A comment on a line of its own is a *leading* comment of the next node that starts after it, and is printed on its
  own line, at that node's indent, before it.
- A comment with no following node in its block (the end of a file, a match or a lambda body) is printed at the end
  of that block.

Blank lines are covered under [Blank lines](#blank-lines) below.

### Printing

The formatter is a document printer in the style of Wadler's "prettier printer" (as in Prettier, `ocamlformat` and
Elm's formatter): each construct is turned into a document of text, possible line breaks, nesting and groups, and the
printer lays out each group on one line if it fits in the line width and breaks it otherwise. The rules for each
construct are written once, as documents, and the printer decides where to break.

- Parentheses are printed where precedence needs them, and nowhere else.
- Literals are printed from their source text, through their spans, so `0xFF`, `1_000` and escapes survive.
- Identifiers, operators and keywords are printed as written; the formatter never renames.

### Never changing meaning

`nanyx format` refuses a file that does not parse, and says where the parse error is.

After formatting, it parses its own output and compares the tree with the original (equality ignores spans), with
the original's imports sorted the same way first. If they differ, or a comment was dropped, it leaves the file alone
and reports an internal error with the file's name. This
check runs on every file, always: it costs one extra parse, and a formatter that silently changes a program is worse
than none.

Formatting is idempotent: formatting formatted code changes nothing. Both properties are tested over every `.nyx`
file in the repository and every Nanyx code block in the docs.

### The style

The docs' examples are the starting point. Proposed rules, each to be confirmed by the maintainer (see the tracking
issue):

- Two spaces of indent and a line width of 100, unless [configured](#configuration). Indent is measured from the
  start of the line, never from a keyword's column.
- One space around binary operators and `->`, after commas, and inside a lambda's braces: `{ a, b -> a + b }`.
- A definition that fits on one line stays on one line. One that does not breaks before its `:` and its `=`, each
  indented once:

  ```nanyx
  def Option.map
    : (#some(a) | r), (a -> b) -> (#some(b) | r)
    = { | #some(a), f -> #some(f(a))
        | other, _ -> other }
  ```

- A lambda with match cases puts each case on its own line, the `|`s aligned under the first.
- A lambda whose body is on the `->`'s line but spans lines indents the body once, and puts its `}` back at the
  lambda's line's indent, so two closing braces never line up (decided 2026-11-07):

  ```nanyx
  def checkOrder = { products, lines -> proc {
      match lines
        | [] -> (return [])
        | [first, ...rest] -> ...
    }
  }
  ```

- A pipeline that does not fit puts each stage on its own line, indented once.
- A record, list, tuple or call's arguments that does not fit puts each element on its own line, separated by newlines
  instead of commas, as the docs write them, with the closing bracket on a line of its own; trailing commas therefore
  never arise. The outermost brackets break first.
- An `if` with expressions for branches that does not fit puts `then` and `else` at the start of lines of their own,
  indented once (decided 2026-11-07):

  ```nanyx
  if line.qty > p.stock
    then #outOfStock(p.name)
    else #some(Priced(name = p.name, qty = line.qty, each = p.price, cost = p.price * line.qty))
  ```

- A chain of operators of one precedence that does not fit puts as many operands on a line as fit, each line ending in
  an operator and the lines after the first indented once. The parser takes a line break after an operator but not
  before one, and not at all in a statement, an arm's body or a top-level expression, where a chain stays on one line
  unless it is inside brackets.
- A signature that does not fit on the def's line goes on the next line after the name, as does the `=`, and the value
  is indented under them (shown above).
- A def with a type annotation whose value is a lambda puts the lambda's body on the line after the `->`, unless the
  body is only a name or a literal (decided 2026-11-07):

  ```nanyx
  def heading: [Log] string -> () = { title ->
    log("== " ++ String.uppercase(title) ++ " ==")
  }

  def id: a -> a = { x -> x }
  ```

- A record, tuple or call's arguments with an element that spans lines (a lambda with a block) also puts each
  element on its own line, with the closing bracket on a line of its own:

  ```nanyx
  use Log(
    log = { message ->
      println(message)
    }
  )
  ```

- A type or context definition whose body is a record of more than three fields puts each field on its own line
  (decided 2026-11-07):

  ```nanyx
  type Product = (
    sku: string
    name: string
    price: int
    stock: int
  )
  ```

### Blank lines

A construct that spans more than one line, once laid out, gets a blank line before and after it, at every level: top
level, module blocks, and the statements of a lambda or of an indented block (a def's value written on the lines
after it, an arm's body, an `if` branch). The first and last statements of a body are the
exception: there is no blank line after the opening `{` or before the closing `}`.

Between constructs that each fit on one line, the author's choice is kept: a single blank line stays, a run of them
becomes one, and none is added.

```nanyx
def zero = 0
def one = 1

def add
  : int, int -> int
  = { a, b -> a + b }

def two = 2
```

Blank lines inside an expression (between a lambda's match cases, between record fields) are removed.

Two exceptions. Imports in a row stay together, however many lines a bracketed `import (...)` takes, and an
attribute such as `@entry` stays on the line before its item.

Whether a construct spans more than one line depends on its layout, which depends on where it starts, so the renderer
decides it (`Doc.Stack`): a construct is multi-line if it always breaks, or if it does not fit flat in the width left at
its indent.

### Sorting imports

The imports in each run of consecutive `import` statements are sorted, and the names within one `import (...)` are
sorted. A blank line or an own-line comment ends a run, so authors can group imports and keep the groups apart.
Comments attached to an import move with it.

The order: standard library modules (`nanyx/...`) first, then other packages' modules, then imports by path, each
group alphabetical by its source as written, ignoring case, with ties broken by case.

Reordering imports is safe because an import only brings names into scope, and a name exported by two imports is an
error whatever their order (#155). If imports ever gain effects, this rule changes with them.

### Configuration

The `[formatter]` table of the package's `.nyxpack` (TOML), as the docs describe, with two settings:

```toml
[formatter]
lineWidth = 100   # default 100
indentSize = 2    # default 2
```

`nanyx format` finds the nearest `.nyxpack` in the file's directory or above it, and reads only that table, so it
does not wait for the packaging workstream to read whole manifests. With no `.nyxpack`, or no table, the defaults
apply. An unknown key or a value that is not a positive integer is an error naming the file and key. The language
server uses the same settings for the document it formats.

### The command

```bash
nanyx format [--check | --write] [paths...]
```

With no path it formats every `.nyx` file under the current directory; a directory means every `.nyx` file under
it. `--write` is the default. `--check` writes nothing, lists each file that would change, and exits 1 if any would.
Exit code 2 means a file could not be formatted (it does not parse, or the safety check failed).

### The editor

The language server answers `textDocument/formatting` with one edit replacing the document, or no edits if it does
not parse. The VS Code extension registers Nanyx as formattable, so "Format Document" and `editor.formatOnSave` work.
Range formatting comes later: formatting the smallest enclosing top-level items is enough.

## Plan

Tracked in [#215](https://github.com/nanyx-lang/nanyx/issues/215). Items in order. Each produces something usable on
its own:

1. **Keep comments and blank lines** ([#217](https://github.com/nanyx-lang/nanyx/issues/217)). The parser returns
   them alongside the tree.
2. **The document printer** ([#218](https://github.com/nanyx-lang/nanyx/issues/218)). `compiler/Nanyx.Formatter`
   with the document type and the layout algorithm, tested on its own.
3. **Print every construct** ([#219](https://github.com/nanyx-lang/nanyx/issues/219)). A printer for the whole
   syntax tree, with comments, parentheses and literals, checked by the round-trip and idempotence tests. Its layout
   can be crude.
4. **The style** ([#220](https://github.com/nanyx-lang/nanyx/issues/220)). The layout rules above, construct by
   construct, with tests taken from the docs' examples.
5. **`nanyx format`** ([#221](https://github.com/nanyx-lang/nanyx/issues/221)). The command, its modes and exit
   codes, and the safety check.
6. **The editor** ([#222](https://github.com/nanyx-lang/nanyx/issues/222)). Document formatting in the language
   server; format-on-save in the extension.
7. **Blank lines** ([#225](https://github.com/nanyx-lang/nanyx/issues/225)). Blank lines around multi-line
   constructs, at every level.
8. **Sorting imports** ([#226](https://github.com/nanyx-lang/nanyx/issues/226)).
9. **Configuration** ([#227](https://github.com/nanyx-lang/nanyx/issues/227)). The `[formatter]` table of
   `.nyxpack`.
10. **The repository** ([#223](https://github.com/nanyx-lang/nanyx/issues/223)). Format `examples/`, `stdlib/` and
    the docs' code blocks, and run `nanyx format --check` in CI.

Later: range formatting, formatting standard input for other editors (`nanyx format -`).

## Decided

On 2026-11-07, in [#215](https://github.com/nanyx-lang/nanyx/issues/215):

- **Configuration.** `lineWidth` and `indentSize`, for now, in the `[formatter]` table of `.nyxpack`.
- **Blank lines.** A construct that spans several lines gets a blank line before and after it, at every level;
  between single-line constructs, the author's single blank lines are kept.
- **Trailing commas.** A non-issue: Nanyx separates the elements of a multi-line record or list with newlines, not
  commas.
- **Imports.** Sorted, within each run of consecutive imports.
- **Unparseable files.** Refused.

## Open questions

- **The style rules** in [The style](#the-style), starting from the docs' examples.
- **Alignment.** Aligning `=` or `->` across consecutive lines looks tidy but makes one edit change many lines in a
  diff. This page proposes no alignment beyond the `|`s of match cases.
