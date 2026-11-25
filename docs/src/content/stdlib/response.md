---
title: response
description: HTTP responses
order: 15.8
---

A `Response` is a record, in the HTTP interface package `nanyx.http` (see [client](./client)); the console host's
prelude brings it into every module of a program. Its headers are named in lower case, and `Response.header` finds
one whatever case it is asked for in:

```nanyx fragment
match http.get("https://example.com")
  | #some(response) -> println(Response.header(response, "Content-Type"))
  | _ -> println("The request failed")
```
# Reference

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

HTTP responses.

A [`Response`](#response) is a record: its status, its headers, each a name in lower case and a value, and
its body, as text. A client gets one from `http.send`; a handler makes one, with
[`Response.text`](#responsetext) or as a record.

## `Response`

```nanyx fragment
type Response = (status: int, headers: list((string, string)), body: string)
```

An HTTP response: its `status`, as `200`, its headers, each a name in lower case and a value, in
the order they came, and its body.

## `Response.text`

```nanyx fragment
def Response.text: int, string -> Response
```

A response with the status `status` and the text `body`, as plain text in UTF-8.

## `Response.header`

```nanyx fragment
def Response.header: Response, string -> #some(string) | #notFound
```

The value of the first header of `response` named `name`, whatever its case:
`Response.header(r, "Content-Type")`.

<!-- End of the generated reference. -->
