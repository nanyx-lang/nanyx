---
title: Simple HTTP Server
description: Build a basic web server
order: 3
---

Build a basic web server: a program whose entry point answers HTTP requests, run under the HTTP host, `nanyx.http.host`.

# Setup

```bash
nanyx new my_server
cd my_server
```

Then name the HTTP host in the manifest, `my_server.nyxp`, in place of the console host, with the port to serve on:

```nanyx fragment
(
  package = (name = "my_server", version = "0.1.0", sources = ["src/**/*.nyx"])
  host = (package = "nanyx.http.host", version = "0.49.1", settings = (port = 3000))
)
```

# The code

In `src/main.nyx`:

```nanyx fragment
module main

@entry
def main: Request -> Response = { request ->
  match string.split(Request.path(request), on = "/")
    | ["", ""] -> Response.text(200, "Welcome to Nanyx!")
    | ["", "hello", name] -> Response.text(200, "Hello, {name}!")
    | _ -> Response.text(404, "Not Found")
}
```

The entry point is the handler itself, a `Request -> Response` function: the host owns the server, listens on the
port, and calls the handler for each request. `Request.path` is the path of the request's URL, without its query, so
`/hello/Ada` splits into `""`, `"hello"` and `"Ada"`.

# Run it

```bash
nanyx run
```

`nanyx run` serves the program until you press Ctrl+C:

```text
Serving http://localhost:3000/ (Ctrl+C to stop)
```

Open `http://localhost:3000/hello/Ada` and it answers `Hello, Ada!`.

# Key concepts

- **The handler is the entry point.** The HTTP host takes a `Request -> Response`, and provides `Http` for requests
  the handler makes itself, but neither `Console` nor `FileSystem`: a library that writes files can't be used here.
- **Each request runs on its own.** Every request is answered by a fresh instance of the program, so nothing is shared
  between requests, and one whose handler crashes is answered with a 500 while the others carry on.
- **Pattern matching on routes**, with list patterns over the path's parts: readable routing without a framework.