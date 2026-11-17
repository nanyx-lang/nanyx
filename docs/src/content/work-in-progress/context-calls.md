---
title: "Context calls"
description: "How the compiler can make a call to a function that requires contexts cheaper"
order: 25
---

This page keeps what is still to do about what a call to a function that requires contexts compiles to. How it
compiles today is in [Context calls](../compiler-internals/context-calls) in the Compiler internals section, and what a
user sees in [Contexts](../advanced/contexts). It is part of the [compilation](./compilation) workstream, and its issues
carry the `compilation`, `wasm backend` and `performance` labels.

# Still to do

- **A `use` of a lambda that captures nothing** is still passed as a closure. The callee could be specialised on the
  lifted function and passed only its environment ([#1877](https://github.com/nanyx-lang/nanyx/issues/1877)).
