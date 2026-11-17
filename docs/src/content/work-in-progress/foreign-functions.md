---
title: "Foreign functions"
description: "Why foreign functions are gated by HostRuntime, how the compiler builds @extern and host memory, and what is still open"
order: 3
---

Foreign functions (`@extern`) and host memory (`nanyx/host.memory`) are built, and described in
[Hosts](../advanced/hosts#foreign-functions). This page keeps the reasoning behind them, how the compiler builds them,
and the open questions. It is part of the [hosts](./hosts) work.

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
([#1342](https://github.com/nanyx-lang/nanyx/issues/1342), [#1344](https://github.com/nanyx-lang/nanyx/issues/1344)).

1. **Later:** the browser host's imports, which a page's JavaScript provides ([Hosts](./hosts#a-browser-host)).
2. **Later:** a foreign function named as a value, rather than called, which can't be compiled yet (`NYX0301`).

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
- **Running it.** `nanyx run` links Wasmtime's WASI, so an extern of a WASI preview 1 function runs, and the runtime's
  own imports (below). Any other import fails when the module is instantiated, with Wasmtime's message naming it.
- **The runtime's own imports.** What WASI preview 1 lacks, the `nanyx` runtime supplies under a module name of its
  own, `nanyx:<area>`, until the backend emits components and can use the standard interface instead. A program that
  imports one runs under `nanyx run`, and fails to load under another WASI runtime, which is accurate: it needs what
  only this runtime gives it. There are two so far. One is `nanyx:http` (`Nanyx.Runtime.HttpImport`), which the console host
  implements `Http` on ([#1785](https://github.com/nanyx-lang/nanyx/issues/1785)), and which `wasi:http` replaces later
  with no change to programs, since they see only `Http`:
  - `send(request: i32, length: i32, kind: i32) -> i32` makes the request written in the `length` bytes at `request`
    with .NET's `HttpClient`, blocking until it is answered or its timeout passes, keeps the answer, writes what kind
    of answer it is at `kind` (0 a response, 1 a timeout, 2 a connection that failed, 3 a URL that isn't an absolute
    `http` or `https` one, 4 anything else) and gives the answer's length in bytes;
  - `receive(destination: i32)` copies the kept answer to `destination`, where the program has allocated that many
    bytes, and forgets it.

  A request is written as lines ended by `\n`: the method, the URL, the timeout in milliseconds, the number of headers
  and each header's name and value, then the body as it is. A response comes back the same way: the status, the number
  of headers, each header's name, in lower case, and value (several values of one header joined with `, `), then the
  body; a failed connection's and another failure's answer is the message saying why. The answer is kept per store,
  so between the two calls nothing else may send. Two calls rather than one let the program allocate the answer's
  room itself, with the allocator every object uses, rather than the runtime writing into memory it doesn't own.

  The other is `nanyx:serve` (`Nanyx.Runtime.WasmServer`), which the HTTP host answers requests on
  ([#1786](https://github.com/nanyx-lang/nanyx/issues/1786)): `requestLength() -> i32` and `request(destination: i32)`
  give the instance the request it was started for, written as above with a timeout of 0, and
  `respond(response: i32, length: i32)` gives the runtime the response. `nanyx run` serves a program whose module
  imports it, rather than running it once (see [Hosts](./hosts#how-its-built)).
- **Host memory.** `nanyx/host.memory` is written over intrinsics of its own, private to the standard library as the
  others are (`__nanyx_memory_allocate`, `__nanyx_memory_read_i32`, `__nanyx_string_address`, ...): a read or a
  write is the one instruction that does it (`CoreIrEmit`'s `memoryAccesses`), a block comes from the allocator every
  object does, and `stringAt` is a helper that copies the bytes into a new string. `Hosts.mayRequireRuntime` lets it
  require `HostRuntime`, as `nanyx/host.runtime` does. Reference counting keeps a string alive while a value made
  from its address is used (`CoreIrCounting.addressFlow`, see
  [Memory management](./memory-management#things-that-need-care)), with inlining and without.
- **The console host.** `nanyx.console/console.host` declares the WASI functions it calls with `@extern`: `Env` is
  built on `args_sizes_get` and `args_get`, `environ_sizes_get` and `environ_get`, and `proc_exit`, and `Console` on
  `fd_write` and `fd_read`. It keeps its state in the block the module sets aside for its host (`hostState`, 4096
  bytes after the data segment, in a module whose host uses it, `WasmRuntime.hostStateSize`): where it is in the
  standard input it has read, an iovec, a count, and a buffer of 4032 bytes that standard input is read into, so it
  allocates nothing to write or read and frees nothing when `run` ends, and what one `run` leaves unread the next one
  reads (a script's statements beside an `@entry` run through `run` twice). A write puts the string's address in the
  iovec, so the host calls `keepAlive` after `fd_write`: counting doesn't follow an address through memory.
  `HostRuntime` has no members, so a function that requires it is passed nothing, and the type checker records no
  requirement of it: `Hosts.runtimeErrors` finds a use of it by the contexts of what each call calls
  (`ImportedContextTypes`, the module's own, and those in the callee's type, where a foreign function has them),
  anywhere in a module that isn't a host. The runtime imports `fd_write` and `proc_exit` itself only for `dbg`, a
  deferred error and the memory checks, and a foreign function of a WASI function shares the runtime's name for it
  (`CoreIrEmit.importName`), so the module imports it once. Hello world's WebAssembly text went from 49,990 bytes to
  41,824, and a program that reads its arguments and an environment variable from 74,568 to 68,181.

# Open questions

- **JavaScript imports.** How a browser host package carries the JavaScript that implements its externs, and how the
  build writes it out beside the module ([Hosts](./hosts#open-questions) asks the same).
- **Pure foreign functions.** Should an extern that does no input or output, such as a math routine, be callable
  without `HostRuntime`? The safe answer is no, until there's a case for it.
- **WASI preview 2.** The `wasi:http` world the [HTTP host](./hosts#serving-http) needs is a component, not a core
  module. Externs over the component model's types may need more than the value types above.
