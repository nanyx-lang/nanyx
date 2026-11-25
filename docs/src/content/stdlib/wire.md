---
title: wire
description: How the runtime's HTTP imports write requests and responses
order: 15.9
---

`nanyx.http/wire` is for hosts, not programs: it writes and reads requests and responses as the `nanyx`
runtime's own imports, `nanyx:http` and `nanyx:serve`, pass them. The console host and the HTTP host use it.
# Reference

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

How the `nanyx` runtime's own HTTP imports, `nanyx:http` and `nanyx:serve`, write requests and
responses, for the hosts written on them. A program doesn't need it.

A request is written as lines, each ended by `\n`: its method, its URL, its timeout in
milliseconds, how many headers it has, and each header's name and value on lines of their own; its
body follows, as it is. A response is written the same way: its status, how many headers it has,
each header's name and value, then its body.

## `methodName`

```nanyx fragment
def methodName: Method -> string
```

`method`'s name, as HTTP writes it: `GET` for `#get`.

## `methodOf`

```nanyx fragment
def methodOf: string -> #some(Method) | #unknown
```

The method HTTP writes as `name`, or `#unknown`.

## `writeRequest`

```nanyx fragment
def writeRequest: Request -> string
```

`request`, written.

## `readRequest`

```nanyx fragment
def readRequest: string -> #some(Request) | #malformed
```

The request written in `text`.

## `writeResponse`

```nanyx fragment
def writeResponse: Response -> string
```

`response`, written.

## `readResponse`

```nanyx fragment
def readResponse: string -> #some(Response) | #malformed
```

The response written in `text`.

<!-- End of the generated reference. -->
