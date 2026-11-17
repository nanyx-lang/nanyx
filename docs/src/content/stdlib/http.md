---
title: http
description: Making HTTP requests
order: 15.6
---

The functions of `nanyx.http.core/http` make HTTP requests, each requiring [`Http`](./client). Import the module under
its own name, `import nanyx.http.core/http as http`, to call them as `http.get`, `http.post` and `http.send`. Each
waits for the response, and gives it, or why there was none as an `HttpError`:

```nanyx fragment
match http.post("https://example.com/notes", "Buy milk")
  | #some(response) -> println("{response.status}: {response.body}")
  | #error(#timeout) -> println("No answer in time")
  | #error(#connectionFailed(why)) -> println("Couldn't connect: {why}")
  | #error(_) -> println("The request failed")
```

`http.send` takes a whole [`Request`](./request), for another method, headers or a timeout of its own:

```nanyx fragment
def request = Request.of(#put, "https://example.com/notes/1") with headers = [("Content-Type", "text/plain")]

match http.send(request with body = "Buy eggs")
  | #some(response) -> println(response.status)
  | #error(_) -> println("The request failed")
```
# Reference

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

Making HTTP requests.

Each function requires [`Http`](client), waits for the response, and gives it, or why there was
none as an [`HttpError`](client). Import the module as `import nanyx.http.core/http as http` to
call `http.get`, `http.post` and `http.send` by those names.

## `send`

```nanyx fragment
def send: [Http] Request -> #some(Response) | #error(HttpError)
```

Sends `request`, and gives its response.

## `get`

```nanyx fragment
def get: [Http] string -> #some(Response) | #error(HttpError)
```

Gets `url`, and gives the response.

## `post`

```nanyx fragment
def post: [Http] string, string -> #some(Response) | #error(HttpError)
```

Posts `body` to `url`, and gives the response.

<!-- End of the generated reference. -->
