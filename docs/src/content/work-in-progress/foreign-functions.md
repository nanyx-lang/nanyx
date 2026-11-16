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
the caller allocates, and `fd_write` reads the bytes to write from one. [`nanyx/host.memory`](../stdlib/host-memory)
gives a host typed access to that memory. Every function in it requires `HostRuntime`:

- `allocate` and `free` a block of bytes;
- `readByte` and `writeByte`, `readI32` and `writeI32`, `readI64` and `writeI64` at an address;
- `addressOf` and `byteLength`, a string's address and byte length;
- `stringAt`, a new string copied from an address and a length;
- `keepAlive`, which keeps a string alive until it is called, for one whose address is written into memory;
- `hostState`, the address of a block the module sets aside for its host, zero at the start, for state it keeps from
  one call to the next.

These are the only unchecked operations a host performs. Reading or writing outside the module's memory traps, as
WebAssembly does. A string held in a name is kept alive for as long as the host uses its address, or a number made
from it.

# The console host

The console host is written in Nanyx, on `@extern` and `nanyx/host.memory`, and calls no intrinsic:

- `Env` is built on `args_sizes_get` and `args_get`, `environ_sizes_get` and `environ_get`, and `proc_exit`;
- `Console` is built on `fd_write` and `fd_read`;
- its text handling uses the public `nanyx/text` and `nanyx/list` functions.

`HostRuntime` stays as the capability that marks the host. It has no members: the reads and writes it used to have
are the console host's own functions over `fd_write` and `fd_read`, and requiring it costs nothing.
# Plan

1. **`@extern`** ([#1424](https://github.com/nanyx-lang/nanyx/issues/1424)): the attribute, its type rules, the
   `HostRuntime` requirement, and the module's imports. Done (see [What's built](#whats-built)).
2. **Host memory** ([#1425](https://github.com/nanyx-lang/nanyx/issues/1425)): `nanyx/host.memory`. Done (see
   [What's built](#whats-built)).
3. **The console host on them** ([#1426](https://github.com/nanyx-lang/nanyx/issues/1426)): no intrinsics in
   `console.host`, `env` or `host.runtime`, with every program printing what it did and no bigger code. Done (see
   [What's built](#whats-built)).
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
- **Host memory.** `nanyx/host.memory` is written over intrinsics of its own, private to the standard library as the
  others are (`__nanyx_memory_allocate`, `__nanyx_memory_read_i32`, `__nanyx_string_address`, ...): a read or a
  write is the one instruction that does it (`CoreIrEmit`'s `memoryAccesses`), a block comes from the allocator every
  object does, and `stringAt` is a helper that copies the bytes into a new string. `Hosts.mayRequireRuntime` lets it
  require `HostRuntime`, as `nanyx/host.runtime` does. Reference counting keeps a string alive while a value made
  from its address is used (`CoreIrCounting.addressFlow`, see
  [Memory management](./memory-management#things-that-need-care)), with inlining and without.
- **The console host.** `nanyx.console/console.host` declares the WASI functions it calls with `@extern`. It keeps its state
  in the block the module sets aside for its host (`hostState`, 4096 bytes after the data segment, in a module whose
  host uses it, `WasmRuntime.hostStateSize`): where it is in the standard input it has read, an iovec, a count, and a
  buffer of 4032 bytes that standard input is read into, so it allocates nothing to write or read and frees nothing
  when `run` ends, and what one `run` leaves unread the next one reads (a script's statements beside an `@entry` run
  through `run` twice). A write puts the string's address in the iovec, so the host calls `keepAlive` after `fd_write`:
  counting doesn't follow an address through memory. `HostRuntime` has no members, so a function that requires it is
  passed nothing, and the type checker records no requirement of it: `Hosts.runtimeErrors` finds a use of it by the
  contexts of what each call calls (`ImportedContextTypes`, the module's own, and those in the callee's type, where a
  foreign function has them), anywhere in a module that isn't a host. The runtime imports `fd_write` and `proc_exit`
  itself only for `dbg`, a deferred error and the memory checks, and a foreign function of a WASI function shares the
  runtime's name for it (`CoreIrEmit.importName`), so the module imports it once. Hello world's WebAssembly text went
  from 49,990 bytes to 41,824, and a program that reads its arguments and an environment variable from 74,568 to
  68,181.- **Not yet.** An extern named as a value, rather than called, isn't compiled.

# Open questions

- **JavaScript imports.** How a browser host package carries the JavaScript that implements its externs, and how the
  build writes it out beside the module ([Hosts](./hosts#open-questions) asks the same).
- **Pure foreign functions.** Should an extern that does no input or output, such as a math routine, be callable
  without `HostRuntime`? The safe answer is no, until there's a case for it.
- **WASI preview 2.** The `wasi:http` world the [HTTP host](./hosts#serving-http) needs is a component, not a core
  module. Externs over the component model's types may need more than the value types above.
