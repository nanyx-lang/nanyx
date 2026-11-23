---
title: host.async
description: How a host's code waits on the host while the program's other tasks run
order: 16.5
---

Only a host imports `nanyx/host.async`, for its calls that wait on the host. How hosts use it is described in [Hosts](../advanced/hosts#waiting-on-the-host).

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

How a host's code waits on the host without holding up the program's other tasks: it starts what it
waits for, such as a request, and looks whether it has finished, waiting on the host between looks.
Every function requires `HostRuntime`, so only a host can call them.

## `waitOnHost`

```nanyx fragment
def waitOnHost: [Async, HostRuntime] ([HostRuntime] I32 -> ()), I32 -> ()
```

Waits until the scheduler has made the host's wait, then gives back: the code that called it
looks again at what it waits for. The scheduler makes it when every task is waiting, by calling
`wait`, the host's function that blocks until something the host started may have finished, with
the shortest timeout in milliseconds that the tasks waiting gave it, or -1 when none gave one.
`timeout` is this task's: how long it can wait before it must look again, or -1 for as long as
it takes.

<!-- End of the generated reference. -->
