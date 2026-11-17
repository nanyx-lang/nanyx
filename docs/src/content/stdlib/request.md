---
title: request
description: HTTP requests
order: 15.7
---

A `Request` is a record, in the HTTP interface package `nanyx.http.core` (see [client](./client)); the console host's
prelude brings it, and `Method`, into every module of a program. Make one with `Request.of`, and change the rest
with `with`:

```nanyx fragment
def request = Request.of(#get, "https://example.com/report") with timeout = 5000
```
# Reference

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

HTTP requests.

A [`Request`](#request) is a record: its [`Method`](#method), its URL, its headers, its body and how long to wait
for an answer. [`Request.of`](#requestof) makes one with no headers and no body, and `with` changes the rest:
`Request.of(#post, url) with body = text`. Send one with `http.send`, from `nanyx.http.core/http`.

## `Method`

```nanyx fragment
type Method = #get | #post | #put | #delete | #patch | #head | #options
```

What a request asks the server to do.

## `Request`

```nanyx fragment
type Request = (method: Method, url: string, headers: list((string, string)), body: string, timeout: int)
```

An HTTP request: what it asks (`method`) of which resource (`url`), with its headers, each a name
and a value, in the order they are sent, and its body, as text. `timeout` is how many milliseconds to wait for the answer before giving up.

## `Request.of`

```nanyx fragment
def Request.of: Method, string -> Request
```

A request asking `method` of `url`, with no headers and an empty body, that waits 30 seconds for
its answer.

## `Request.path`

```nanyx fragment
def Request.path: Request -> string
```

The path `request`'s URL names, without its query or fragment: `/hello/ada` for
`http://localhost:3000/hello/ada?greeting=hi`, and `/` for a URL with no path.

<!-- End of the generated reference. -->
