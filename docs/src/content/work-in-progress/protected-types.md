---
title: "Protected types without the sigil"
description: "What is left of replacing the $ on nominal type names with protected"
order: 1
---

A nominal type is declared with a keyword after its `=`, `type Email = protected string` or
`type UserId = private int`, instead of a `$` on its name, and its name is written like any other type's. The language
is described on [Nominal types](../advanced/nominal-and-protected-types); the work was
[#1221](https://github.com/nanyx-lang/nanyx/issues/1221). This page keeps what is still open.

# How it works

- The parser reads `protected` or `private` after a type's or context's `=` (`TypeDefModifier.Protected` and
  `Private`); writing both is an error that says to keep `private`. Nominality is read from the declaration,
  `TypeDefModifier.isNominal` and `TypeDefInfo.IsNominal`, not from the name.
- The language server marks each name of a nominal type as a semantic token with the `nominal` modifier, and `private`
  for a private one (`NominalNames.inModule`, `SemanticTokensHandler`), and hover names the kind and the home module.
  The extension shows nominal types in italics through a default for `editor.semanticTokenColorCustomizations`.

# Migration

For now the parser still reads the `$`: before a declared name it declares a protected type (`declaredTypeName`), and
before a name where it is used it is dropped, so `$Email` is `Email`. The formatter writes the new form, and the
official linter's `dollarPrefixedType` rule reports each `$` with a fix. The standard library, the examples and the
docs are written in the new form. The Wasm backend's tests still write `$`, which keeps the old form exercised until it
goes.

# Open

- **Ending the migration.** The release after the one that brought `protected`, a `$` before a name becomes a parse
  error whose message shows the declaration without it, and `dollarPrefixedType` and `declaredTypeName`'s `$` go.
  The Wasm tests' Nanyx sources are rewritten first.
- **Docs without tooling.** The docs site could render nominal types with the compiler's highlighting, so that the
  distinction survives in a page with no editor behind it. That is a docs-site change, not a language one.
- **The info view** has no list of a module's types yet, so it shows no kind; a library browser that lists them should
  show which are protected and which private.
- **Spans of a `$` name in a pattern or an applied type.** Where the compiler finds a name's span from its length
  (a record pattern's type, `Box(a)`), a `$` before it shifts the span by one character. It goes with the `$`.
