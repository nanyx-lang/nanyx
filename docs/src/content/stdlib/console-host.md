---
title: console.host
description: The host that runs a command-line program
order: 13
---

A program never imports `nanyx/console.host` itself: `nanyx run` and `nanyx build` pass the program's entry point to it. How hosts work is described in [Hosts](../work-in-progress/hosts).

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ by `nanyx stdlib-docs`: edit those, not this. -->

The console host: it runs a command-line program's entry point.

A program's `@entry` is passed to [`run`](#run), which runs it in the contexts this host provides:
`Console`, built on the `HostRuntime` the toolchain provides to the host, and `Env`.

## `run`

```nanyx fragment
def run: [HostRuntime] ([Console & Env] () -> a) -> ()
```

Runs `entry`, the program's entry point, with a `Console` that reads and writes the standard
streams and an `Env` that reads what the program was started with. What `entry` returns is
ignored.

<!-- End of the generated reference. -->
