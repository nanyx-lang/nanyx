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
- A `$` before a type's name, the form before `protected`, is a syntax error at the `$` whose message shows the name
  without it and the declaration with `protected` (`typeNameIdentifier`). It was removed with no transition
  ([#1302](https://github.com/nanyx-lang/nanyx/issues/1302)).
- The language server marks each name of a nominal type as a semantic token with the `nominal` modifier, and `private`
  for a private one (`NominalNames.inModule`, `SemanticTokensHandler`), and hover names the kind and the home module.
  The extension shows nominal types in italics through a default for `editor.semanticTokenColorCustomizations`.

# Open

- **Docs without tooling.** The docs site could render nominal types with the compiler's highlighting, so that the
  distinction survives in a page with no editor behind it. That is a docs-site change, not a language one.
- **The info view** has no list of a module's types yet, so it shows no kind; a library browser that lists them should
  show which are protected and which private.
