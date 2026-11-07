# Formatter: workstream

> **Status: proposal.** This page frames the formatter workstream and proposes how `nanyx format` is built. The
> user's side is already described in the docs; the rest is open for discussion until the questions at the end are
> settled. Work is tracked under the `formatting` label.

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
  formatter only moves whitespace and comments, adds or removes parentheses, and never renames or reorders anything.
- Reading `.nyxpack` itself: the packaging workstream. The formatter reads its `[formatter]` table once manifests can
  be read.

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

Blank lines between two statements or top-level items are kept, but a run of them becomes one. Blank lines anywhere
else (inside an expression) are removed.

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

After formatting, it parses its own output and compares the tree with the original (equality ignores spans). If they
differ, or a comment was dropped, it leaves the file alone and reports an internal error with the file's name. This
check runs on every file, always: it costs one extra parse, and a formatter that silently changes a program is worse
than none.

Formatting is idempotent: formatting formatted code changes nothing. Both properties are tested over every `.nyx`
file in the repository and every Nanyx code block in the docs.

### The style

The docs' examples are the starting point. Proposed rules, each to be confirmed by the maintainer (see the tracking
issue):

- Two spaces of indent; a line width of 100. Indent is measured from the start of the line, never from a keyword's
  column.
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
- A pipeline that does not fit puts each stage on its own line, indented once.
- A list, tuple or record that does not fit puts each element on its own line.
- One blank line between top-level definitions is kept, never added or removed; runs of them become one.

### The command

```bash
nanyx format [--check | --write] [paths...]
```

With no path it formats every `.nyx` file under the current directory; a directory means every `.nyx` file under
it. `--write` is the default. `--check` writes nothing, lists each file that would change, and exits 1 if any would.
Exit code 2 means a file could not be formatted (it does not parse, or the safety check failed). Until manifests can
be read, the line width and indent are the defaults.

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
7. **The repository** ([#223](https://github.com/nanyx-lang/nanyx/issues/223)). Format `examples/`, `stdlib/` and
   the docs' code blocks, and run `nanyx format --check` in CI.

Later: the `[formatter]` table from `.nyxpack` (after packaging can read manifests), range formatting, formatting
standard input for other editors (`nanyx format -`).

## Open questions

- **Configuration.** The docs promise `lineWidth` and `indentSize`. Should `indentSize` exist at all? One indent
  everywhere is a stronger guarantee than one indent per project, and Go, Elm and Black allow none.
- **Blank lines.** Keep the author's single blank lines (proposed), or impose exactly one between top-level
  definitions?
- **Trailing commas.** Does the language allow a trailing comma in a broken list or record, and should the formatter
  add one?
- **Imports.** Should the formatter sort imports? This page proposes not: it reorders code, so it belongs to an
  analyzer with a fix.
- **Alignment.** Aligning `=` or `->` across consecutive lines looks tidy but makes one edit change many lines in a
  diff. This page proposes no alignment beyond the `|`s of match cases.
- **Unparseable files.** Refuse them (proposed), or format the top-level items that do parse and leave the rest?
