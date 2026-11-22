---
title: client
description: Outbound HTTP, as a context a host provides
order: 15.5
---

`Http` is in the HTTP interface package, `nanyx.http.core`, which the compiler ships. The console host provides it,
and its prelude brings `Http`, `HttpError`, `Request`, `Method` and `Response` into every module of a program; the
functions that use it are in [`http`](./http). A script names the package with `::dependency nanyx.http.core`, and a
package lists it in its manifest's `dependencies` at the compiler's version. Any function that makes requests declares
`[Async, Http]` in its type, as a library's do, since a request waits for its answer: a library gets `Http`, and
`HttpError`, by importing `nanyx.http.core/client`, and `Async` by importing `nanyx/async`.

```nanyx fragment
::dependency nanyx.http.core

import nanyx.http.core/http as http

def status: [Async, Http] string -> string = { url ->
  match http.get(url)
    | #some(response) -> "{response.status}"
    | #timeout -> "no answer"
    | _ -> "unreachable"
}

println(status("https://example.com"))
```

A call waits until its answer comes, so it requires `Async`, which the console and HTTP hosts provide. While it
waits, only its instance does: the HTTP host answers other requests meanwhile.
# Reference

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

Outbound HTTP, as a context a host provides.

Code that makes requests declares `[Async, Http]` and calls the functions of [`http`](http), which come
from whichever `Http` instance its caller supplies. The console host (`nanyx.console`) supplies
one to the program's entry point; a host that doesn't provide it guarantees that nothing in the
program reaches the network. A library depends on `nanyx.http.core` and imports
`nanyx.http.core/client`.

## `HttpError`

```nanyx fragment
type HttpError = #timeout | #connectionFailed(string) | #invalidUrl | #other(string)
```

Why a request had no response: none came in the request's `timeout`, the server couldn't be
reached (with what the system said), the URL isn't an absolute `http` or `https` one, or another
reason, described.

## `Http`

```nanyx fragment
context Http = (
  sendRequest: [Async] Request -> #some(Response) | #timeout | #connectionFailed(string) | #invalidUrl | #other(string)
)
```

Outbound HTTP, which a host supplies. Its member is what a host implements; code calls the
functions of [`http`](http), which are written on it.

- `sendRequest`: Sends `request`, waiting for its response. Only the instance that sends it waits.

<!-- End of the generated reference. -->
