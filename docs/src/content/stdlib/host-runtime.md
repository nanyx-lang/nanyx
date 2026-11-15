---
title: host.runtime
description: The privileged layer a host is written on
order: 14
---

Only a host imports `nanyx/host.runtime`, as [`nanyx/console.host`](console-host) does. How hosts work is described in [Hosts](../work-in-progress/hosts).

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ by `nanyx stdlib-docs`: edit those, not this. -->

The capability a host is written on: what lets it call the foreign functions its embedder provides
(those declared with `@extern`) and reach the module's linear memory (with `nanyx/host.memory`).

[`HostRuntime`](#hostruntime) is provided only to the host, the module that runs the program's entry point (as
`nanyx/console.host` does). Anywhere else, requiring it is an error, so a library can only reach
the outside world through the contexts the host chose to provide.

## `HostRuntime`

```nanyx fragment
context HostRuntime = protected ()
```

The capability that marks the host. It has no members: every foreign function, and every function
of `nanyx/host.memory`, requires it, so only the host's code can call them.

<!-- End of the generated reference. -->
