---
title: "Inlining"
description: "What is planned and open about the WebAssembly backend's inliner"
order: 25
---

This page keeps what is planned and open about the inliner of the WebAssembly backend. How it works today is in
[Inlining](../compiler-internals/inlining) in the Compiler internals section, and what it means for users in
[Inlining](../foundations/compiler-optimisations#inlining). It is part of the [compilation](./compilation) workstream.

# Plan

1. **Tune the size limits** (`sizeLimit`, `handlerLimit`, `copyLimit`) against the generated-code benchmarks.
2. **Lower only what a program reaches**
   ([#1848](https://github.com/nanyx-lang/nanyx/issues/1848)). Inlining already goes over only what the start
   reaches. Lowering still lowers every definition that is not generic, and emits each once to find out whether it
   can be, which is most of hello world's emit now. Lowering on demand from the start would mean a definition the
   program never reaches no longer reports that it can't be compiled, which is to be decided.
3. **Later:** inlining across modules, and the other builders and handlers in the standard library.

# Open questions

- **The size limit,** and whether a function can ask to be inlined, or not, with an attribute.
- **Code size.** Aggressive inlining grows the module. A function stops taking more at 4,000 nodes, and a handler
  called more than once is held to `handlerLimit`; a budget relative to the function's own size may do better. Nodes
  undercount some code: an `Interpolate` is one node however much WAT it writes.
- **Across modules.** A function from another module, including the standard library, can only be inlined if its core
  IR is available where it is called. That is true today, since a program compiles as one module, but not once
  packages compile separately.
