---
title: "Foreign functions"
description: "How hosts reach their embedder: typed @extern imports that only a host can call, and host access to linear memory"
order: 3
---

This page is about how Nanyx code reaches what's outside the WebAssembly module: the WASI calls a console program
makes, and later the JavaScript a page provides. It is part of the [hosts](./hosts) work. The maintainer decided the
design; the [plan](#plan) lists the issues, and [What's built](#whats-built) says how far they have got.

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

Nanyx keeps intrinsics private, and gates foreign functions by a capability rather than by who wrote the code.

# `@extern`

A foreign function is a `def` with the `@extern` attribute, a written type and no body:

```nanyx fragment
@extern("wasi_snapshot_preview1", "args_sizes_get")
def argsSizesGet: [HostRuntime] (I32, I32) -> I32
```

- **The import.** The two strings are the WebAssembly import's module and name. Each extern becomes one import of the
  compiled module, declared once however often it is called, and a call is a direct call. Two externs naming the
  same import with different types is an error.
- **Types.** Parameters and the result are WebAssembly's value types, written as Nanyx's sized numbers (`I32`, `I64`,
  `F32`, `F64`), or `()` for no result. Strings, records and lists don't cross the boundary themselves: a host passes
  addresses and lengths, using [host memory](#memory).
- **Who can call it.** The type must require `HostRuntime`. Any module may declare an extern, but a call requires
  `HostRuntime`, and the toolchain gives `HostRuntime` only to the host (requiring it anywhere else is NYX0238). So
  only a host's code calls foreign functions, and a library can only reach the outside world through the contexts the
  host chose to provide. This is the capability rule Nanyx already has, not a list of privileged modules, so a host
  written by anyone, in any package, works the same way as the console host.
- **The CLI's runtime** provides the WASI imports it provides today. An extern it doesn't provide fails when the
  module is instantiated, with an error naming the import.

# Memory

A foreign function deals in addresses in the module's linear memory: `args_get` writes the arguments into a buffer
the caller allocates, and `fd_write` reads the bytes to write from one. `nanyx/host.memory` gives a host typed access
to that memory. Every function in it requires `HostRuntime`:

- `allocate` and `free` a block of bytes;
- read and write a byte, an `I32` and an `I64` at an address;
- a string's address and byte length, valid while the string is alive;
- a new string copied from an address and a length.

These are the only unchecked operations a host performs. Reading or writing outside the module's memory traps, as
WebAssembly does, and a string whose address is taken is kept alive for the call.

# The console host

The console host is written in Nanyx, but today it calls intrinsics:

- for `Env`: `__nanyx_arguments`, `__nanyx_environment` and `__nanyx_exit`;
- for `HostRuntime`'s members: `__nanyx_write_stdout` and the others;
- for text: string and list intrinsics.

That ties it to the standard library, which is why it can't become a [shipped package](./built-ins-as-packages) yet.
Written on `@extern` and `nanyx/host.memory`, it uses no intrinsics:

- `Env` is built on `args_get`, `environ_get` and `proc_exit`;
- `HostRuntime`'s members are built on `fd_write` and `fd_read`;
- its text handling uses the public `nanyx/text` and `nanyx/list` functions.

`HostRuntime` stays as the capability that marks the host.

# Plan

1. **`@extern`** ([#1424](https://github.com/nanyx-lang/nanyx/issues/1424)): the attribute, its type rules, the
   `HostRuntime` requirement, and the module's imports. Done (see [What's built](#whats-built)).
2. **Host memory** ([#1425](https://github.com/nanyx-lang/nanyx/issues/1425)): `nanyx/host.memory`.
3. **The console host on them** ([#1426](https://github.com/nanyx-lang/nanyx/issues/1426)): no intrinsics in
   `console.host`, `env` or `host.runtime`, with every program printing what it did and no bigger code.
4. Then the console host ships as a package ([#1342](https://github.com/nanyx-lang/nanyx/issues/1342),
   [#1344](https://github.com/nanyx-lang/nanyx/issues/1344)).
5. **Later:** the browser host's imports, which a page's JavaScript provides ([Hosts](./hosts#a-browser-host)).

# What's built

- **Declaring one.** A top-level `def` with a type and nothing after it parses as a def whose value is a call to the
  built-in `__extern` (`ExternSyntax`, `parseDefOrDeclarationWith`), and the formatter prints it as it is written, with
  no `=`. `Attributes.applyAttributes` reads the `@extern` before it (`Externs.declared`) and makes the value
  `__extern("module", "name")`, so the import travels with the def into every module that inlines it. A def with no
  value and no `@extern` is `NYX0030`. A value, a missing type, a part of the type other than `I32`, `I64`, `F32`,
  `F64` or `()`, a context other than `HostRuntime`, or an argument other than two strings is `NYX0241`; a type
  without `HostRuntime` is `NYX0242`. Two externs in a module that name one import with different types are
  `NYX0243`.
- **Checking it.** The type checker takes an extern's value to have the def's written type. `Hosts.runtimeErrors`
  doesn't report `NYX0238` at an extern, so any module may declare one, but a def that calls one requires
  `HostRuntime` and is reported as any other def that does.
- **Compiling it.** `CodeGen` takes the externs out of the module before anything else is compiled from it, with each
  one's value types read from its type (`Externs.signatureOfType`), and reports two that name one import with
  different types, wherever they came from, as `NYX0243`. A call to one is a `CallImport` in the core IR: its
  arguments as the value types the import takes (a float out of its box), then a direct `call` of the import, and a
  float result boxed; an import that returns nothing leaves unit. The module declares each import a call reaches once
  (`CoreIrFacts.Imports`, `CoreIrEmit.importDeclaration`), as `$import_<module>_<name>`, before every other import,
  and exports its memory.
- **Running it.** `nanyx run` links Wasmtime's WASI, so an extern of a WASI preview 1 function runs. Any other import
  fails when the module is instantiated, with Wasmtime's message naming it.
- **Not yet.** An extern named as a value, rather than called, isn't compiled. No host in the standard library uses
  an extern yet ([#1426](https://github.com/nanyx-lang/nanyx/issues/1426)).

# Open questions

- **JavaScript imports.** How a browser host package carries the JavaScript that implements its externs, and how the
  build writes it out beside the module ([Hosts](./hosts#open-questions) asks the same).
- **Pure foreign functions.** Should an extern that does no input or output, such as a math routine, be callable
  without `HostRuntime`? The safe answer is no, until there's a case for it.
- **WASI preview 2.** The `wasi:http` world the [HTTP host](./hosts#serving-http) needs is a component, not a core
  module. Externs over the component model's types may need more than the value types above.
