---
title: host.runtime
description: The privileged layer a host is written on
order: 14
---

Only a host imports `nanyx/host.runtime`, as [`nanyx/console.host`](console-host) does. How hosts work is described in [Hosts](../work-in-progress/hosts).

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ by `nanyx stdlib-docs`: edit those, not this. -->

The privileged layer a host is written on: the raw reads and writes the runtime does for it.

[`HostRuntime`](#hostruntime) is provided only to the host, the module that runs the program's entry point (as
`nanyx/console.host` does). Anywhere else, requiring it is an error, so a library can only reach
the outside world through the contexts the host chose to provide.

## `HostRuntime`

```nanyx fragment
context HostRuntime = protected (
  writeStdout: string -> ()
  writeStderr: string -> ()
  readStdinBlock: () -> string
  readStdinLine: () -> string
)
```

What the runtime does for the host. On the WebAssembly backend each member is one of the
module's imports. The definitions of the members below are the instance the toolchain supplies.

- `writeStdout`: Writes `text` to standard output, as it is.
- `writeStderr`: Writes `text` to standard error, as it is.
- `readStdinBlock`: The next block of standard input, up to 64 KB, or the empty string when the input has ended.
- `readStdinLine`: Standard input up to and including the next line break, or to the end of the block the runtime
  holds when that has none; the empty string when the input has ended.

## `writeStdout`

```nanyx fragment
def writeStdout: string -> ()
```

Writes `text` to standard output, as it is.

## `writeStderr`

```nanyx fragment
def writeStderr: string -> ()
```

Writes `text` to standard error, as it is.

## `readStdinBlock`

```nanyx fragment
def readStdinBlock: () -> string
```

The next block of standard input, up to 64 KB, or the empty string when the input has ended.

## `readStdinLine`

```nanyx fragment
def readStdinLine: () -> string
```

Standard input up to and including the next line break, or to the end of the block the runtime
holds when that has none; the empty string when the input has ended.

<!-- End of the generated reference. -->
