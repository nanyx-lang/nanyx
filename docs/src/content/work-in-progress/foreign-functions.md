---
title: "Foreign functions"
description: "What is still planned for foreign functions, and the open questions"
order: 3
---

Foreign functions (`@extern`) and host memory (`nanyx/host.memory`) are built. They are described in
[Hosts](../advanced/hosts#foreign-functions), and how the compiler builds them, and why they are gated by
`HostRuntime`, in [Foreign functions](../deep-dives/foreign-functions) in the Deep dives section. This page keeps what
is planned and the open questions. It is part of the [hosts](./hosts) work.

# Plan

1. **The browser host's imports**, which a page's JavaScript provides ([Hosts](./hosts#a-browser-host)).
2. **The canonical ABI for Nanyx types.** A host's component root lowers WASI 0.3's canonical ABI by hand, on externs
   of `i32` and `i64` ([Components](../deep-dives/components)). Generating it from Nanyx types is for user components,
   later ([Components](./components#the-backend)).

# Open questions

- **JavaScript imports.** How a browser host package carries the JavaScript that implements its externs, and how the
  build writes it out beside the module ([Hosts](./hosts#open-questions) asks the same).
- **Pure foreign functions.** Should an extern that does no input or output, such as a math routine, be callable
  without `HostRuntime`? The safe answer is no, until there's a case for it.
