---
title: "Protected types without the sigil"
description: "What is left of replacing the $ on nominal type names with protected"
order: 1
---

A nominal type is declared with a keyword after its `=`, `type Email = protected string` or
`type UserId = private int`, and its name is written like any other type's. The language is described on
[Nominal types](../advanced/nominal-and-protected-types), and what the editor shows in
[Nominal types](../standard-tools/vs-code-extension#nominal-types); the work was
[#1221](https://github.com/nanyx-lang/nanyx/issues/1221). Without the `$`, only tooling tells a nominal type's name from
a structural one's. This page keeps what is still open.

# Open

- **Docs without tooling.** The docs site could render nominal types with the compiler's highlighting, so that the
  distinction survives in a page with no editor behind it. That is a docs-site change, not a language one.
- **The info view** has no list of a module's types yet, so it shows no kind; a library browser that lists them should
  show which are protected and which private.
