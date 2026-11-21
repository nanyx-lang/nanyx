---
title: "Foreign functions"
description: "Why foreign functions are gated by HostRuntime, and what is still open"
order: 3
---

Foreign functions (`@extern`) and host memory (`nanyx/host.memory`) are built, and described in
[Hosts](../advanced/hosts#foreign-functions), and how the compiler builds them in
[Foreign functions](../deep-dives/foreign-functions) in the Deep dives section. This page keeps the
reasoning behind them, what is planned, and the open questions. It is part of the [hosts](./hosts) work.

# Two kinds of outside call

- **Intrinsics** are operations the compiler implements itself, such as `__nanyx_string_length` or
  `__nanyx_write_stdout`. They are private to the standard library and change whenever the compiler does. The standard
  library exposes stable functions over them, such as `nanyx/text`'s `string.length`.
- **Foreign functions** are imports the embedder provides: WASI's `args_get` and `fd_write` under the CLI, a page's
  JavaScript under a browser host. This is the boundary a host lives on.

Other languages draw the same line:

- **Rust:** keeps `core::intrinsics` private to its standard library, behind unstable feature gates. `extern "C"`
  functions are open to everyone, behind `unsafe`.
- **GHC:** keeps primops inside its base library; `foreign import` is open to everyone, in `IO`.
- **Roc:** lets only a platform declare "hosted" functions, implemented in another language.
- **Elm:** allows kernel JavaScript only in packages from its own organisations. That stopped anyone else writing a
  platform, and it was resented.

Nanyx keeps intrinsics private, and gates foreign functions by a capability, `HostRuntime`, rather than by who wrote
the code. That is the capability rule Nanyx already has, not a list of privileged modules, so a host written by anyone,
in any package, works the same way as the console host. `HostRuntime` has no members: requiring it costs nothing.

# Plan

Done: `@extern` ([#1424](https://github.com/nanyx-lang/nanyx/issues/1424)), host memory
([#1425](https://github.com/nanyx-lang/nanyx/issues/1425)), the console host on them with no intrinsics
([#1426](https://github.com/nanyx-lang/nanyx/issues/1426)), and the console host as a package
([#1342](https://github.com/nanyx-lang/nanyx/issues/1342), [#1344](https://github.com/nanyx-lang/nanyx/issues/1344)),
and a foreign function named as a value ([#1781](https://github.com/nanyx-lang/nanyx/issues/1781)).

1. **Later:** the browser host's imports, which a page's JavaScript provides ([Hosts](./hosts#a-browser-host)).
2. **Components:** a host's component root lowers WASI 0.3's canonical ABI by hand, on externs of `i32` and `i64`
   named for the interface and function, such as `@extern("wasi:cli/stdout@0.3.0", "[stream-write-0]write-via-stream")`.
   The compiler generates the canonical ABI for Nanyx types only for user components, later
   ([Components](./components#the-backend)).

# Open questions

- **JavaScript imports.** How a browser host package carries the JavaScript that implements its externs, and how the
  build writes it out beside the module ([Hosts](./hosts#open-questions) asks the same).
- **Pure foreign functions.** Should an extern that does no input or output, such as a math routine, be callable
  without `HostRuntime`? The safe answer is no, until there's a case for it.
