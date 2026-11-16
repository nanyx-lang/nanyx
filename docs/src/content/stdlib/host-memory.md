---
title: host.memory
description: A host's access to linear memory, for the foreign functions it calls
order: 16
---

Only a host imports `nanyx/host.memory`, to pass buffers to and from the foreign functions it calls. How hosts use it is described in [Hosts](../advanced/hosts#host-memory).

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

A host's access to the WebAssembly module's linear memory, where it passes buffers to and from the foreign
functions it calls (those declared with `@extern`). An address is an `I32`, the place of a byte in memory.

These are the only unchecked operations a host performs. Nothing checks that an address is in a block the host
allocated, or that a read or a write stays inside it: one outside the module's memory traps, as WebAssembly does,
but one inside it where the host has no block reads or overwrites the program's own data. Every function requires
`HostRuntime`, so only a host can call them.

## `allocate`

```nanyx fragment
def allocate: [HostRuntime] I32 -> I32
```

A new block of `size` bytes, whose address nothing else is given until the block is given back with [`free`](#free). Its
bytes are not cleared.

## `free`

```nanyx fragment
def free: [HostRuntime] I32 -> ()
```

Gives back the block at `address`, which [`allocate`](#allocate) made. The block must not be read, written or freed again.

## `readByte`

```nanyx fragment
def readByte: [HostRuntime] I32 -> I32
```

The byte at `address`, from 0 to 255.

## `writeByte`

```nanyx fragment
def writeByte: [HostRuntime] I32, I32 -> ()
```

Writes the low 8 bits of `value` at `address`.

## `readI32`

```nanyx fragment
def readI32: [HostRuntime] I32 -> I32
```

The `I32` in the 4 bytes at `address`, little-endian.

## `writeI32`

```nanyx fragment
def writeI32: [HostRuntime] I32, I32 -> ()
```

Writes `value` in the 4 bytes at `address`, little-endian.

## `readI64`

```nanyx fragment
def readI64: [HostRuntime] I32 -> int
```

The `I64` in the 8 bytes at `address`, little-endian.

## `writeI64`

```nanyx fragment
def writeI64: [HostRuntime] I32, int -> ()
```

Writes `value` in the 8 bytes at `address`, little-endian.

## `addressOf`

```nanyx fragment
def addressOf: [HostRuntime] string -> I32
```

Where the bytes of `text` are, in UTF-8. When `text` is held in a name, a parameter or a def, it is kept alive
for as long as the code uses the address, or a value made from it, such as `addressOf(text) + 4`. A string made in
the argument itself, as in `addressOf(a ++ b)`, is not: give it a name first. Writing at the address changes
`text`, which must not be done.

## `byteLength`

```nanyx fragment
def byteLength: [HostRuntime] string -> I32
```

How many bytes of UTF-8 `text` takes, which [`addressOf`](#addressof) gives the first of.

## `hostState`

```nanyx fragment
def hostState: [HostRuntime] () -> I32
```

The address of 4096 bytes the module sets aside for its host, all zero when the program starts:
where a host keeps what it needs from one call to the next, such as how much of standard input it
has read, and the small buffers it passes to foreign functions, without allocating. Nothing else
reads or writes them.

## `keepAlive`

```nanyx fragment
def keepAlive: [HostRuntime] string -> ()
```

Keeps `text` alive until here. Reference counting follows an address of `text` only through
values made from it, not through memory: when the address is written into memory that a foreign
function reads, as an iovec is for WASI's `fd_write`, call this after the foreign function.

## `stringAt`

```nanyx fragment
def stringAt: [HostRuntime] I32, I32 -> string
```

A new string holding a copy of the `length` bytes at `address`, which are taken to be UTF-8.

<!-- End of the generated reference. -->