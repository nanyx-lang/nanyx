---
title: Overview
description: "The modules of the standard library"
order: 1
---

The standard library is the modules under `nanyx/`. The [prelude](./prelude) is imported into every module, so what it defines needs no import; any other module is imported by name, as `import nanyx/math`, except that what a module attaches to a built-in type, as `nanyx/text` does to `string`, is in scope without an import. Each page in this section describes one file of the library, and its reference is generated from the doc comments in the library's source, `stdlib/nanyx/`.

# Modules in this section

- [prelude](./prelude) — what every module sees, written across files by topic:
  [contexts](./prelude-contexts), [numbers](./prelude-numbers), [sized numbers](./prelude-sized-numbers),
  [chars](./prelude-chars), [time](./prelude-time), [instant](./prelude-instant), [format](./prelude-format), [conversions](./prelude-conversions), [lists](./prelude-lists), [sequences](./prelude-sequences),
  [maps and sets](./prelude-maps), [arrays](./prelude-arrays) and [results and options](./prelude-results)
- [array](./array) — immutable arrays with constant-time indexing (`nanyx/array`)
- [slice](./slice) — views of part of an array, which share its storage (`nanyx/slice`)
- [marray](./marray) — mutable arrays, fixed-length and changed in place (`nanyx/marray`)
- [mslice](./mslice) — views of part of a mutable array, changed in place (`nanyx/mslice`)
- [mref](./mref) — mutable references, cells that are values (`nanyx/mref`)
- [mstack](./mstack) — mutable stacks (`nanyx/mstack`)
- [mlist](./mlist) — mutable lists that grow at the end (`nanyx/mlist`)
- [stack](./stack) — immutable stacks (`nanyx/stack`)
- [queue](./queue) — immutable queues (`nanyx/queue`)
- [trie](./trie) — maps whose keys are sequences, with prefix queries (`nanyx/trie`)
- [text](./text) — functions on strings (`nanyx/text`)
- [math](./math) — numeric functions and constants (`nanyx/math`)
- [async](./async) — `Async`, the context of the functions that can wait (`nanyx/async`)
- [random](./random) — `Random`, random numbers from the host or from a seed (`nanyx/random`)
- [clock](./clock) — `Clock`, the time of day from `now`, a timer from `ticks`, and `sleep` (`nanyx/clock`)
- [host.runtime](./host-runtime) — the privileged layer a host is written on (`nanyx/host.runtime`)
- [host.memory](./host-memory) — a host's access to linear memory, for the foreign functions it calls (`nanyx/host.memory`)
- [host.async](./host-async) — a host's calls that wait on the host while the program's other tasks run (`nanyx/host.async`)

# Shipped with the compiler

The console host's interface package, `nanyx.cli`, isn't part of the standard library, but the compiler
ships it, and the console host's prelude brings its names into every module of a program:

- [console](./console) — reading from and writing to the terminal (`nanyx.cli/console`)
- [env](./env) — a program's arguments, environment variables and exit code (`nanyx.cli/env`)

The file system's interface package, `nanyx.fs`, is shipped too. A program names it with `::install nanyx.fs`, or
in its manifest's `dependencies`, and imports its modules; the console host provides `FileSystem`:

- [filesystem](./filesystem) — the `FileSystem` context and `FsError` (`nanyx.fs/filesystem`)
- [file](./file) — reading and writing files (`nanyx.fs/file`)
- [dir](./dir) — listing and making directories (`nanyx.fs/dir`)
- [path](./path) — paths to files and directories (`nanyx.fs/path`)

The HTTP client's interface package, `nanyx.http`, is shipped too, and named in the same way; the console host
provides `Http`:

- [client](./client) — the `Http` context and `HttpError` (`nanyx.http/client`)
- [http](./http) — making requests (`nanyx.http/http`)
- [request](./request) — `Request` and `Method` (`nanyx.http/request`)
- [response](./response) — `Response` (`nanyx.http/response`)
- [wire](./wire) — how the runtime's HTTP imports write requests and responses, for hosts (`nanyx.http/wire`)

The HTTP host, `nanyx.http.host`, serves a program whose entry point is a `Request -> Response` handler: see the
[HTTP server cookbook](../cookbook/http-server).

`nanyx.json`, for reading and writing JSON, is not shipped: it is published to the package index from
[nanyx-lang/json](https://github.com/nanyx-lang/json), and a program names it in its dependencies,
`(package = "nanyx.json", version = "0.2")`:

- [json](./json) — the `Json` value, parsing and printing (`nanyx.json/json`)
- [json, reflection](./json-reflection) — encoding and decoding any type by walking it (`nanyx.json/json`)
- [json, decoders](./json-typed) — reading JSON into types of your own (`nanyx.json/json`)
- [encode](./encode) — writing types of your own as JSON (`nanyx.json/encode`)

`nanyx.regex`, for regular expressions that take linear time, is published to the package index too, from
[nanyx-lang/regex](https://github.com/nanyx-lang/regex), and a program names it in its dependencies,
`(package = "nanyx.regex", version = "0.1")`:

- [regex](./regex) — finding, replacing and splitting with patterns (`nanyx.regex/regex`)

# Named parameters

A [named parameter](../tour/functions#named-parameters) must be passed by name. The standard library names a parameter only when a call can't show what it is without the name: usually when two or more parameters after the receiver have the same type, or when a bare literal would leave you guessing its role. It never names the receiver (the value you pipe in), a lambda argument, or a single extra argument that the function's name already explains.

```nanyx fragment
-- Named: a bare literal or two arguments of the same type
"a,b" \split(on = ",")
"7" \padStart(width = 3, fill = "0")
"hello world" \replace(pattern = "o", replacement = "0")
x \math.clamp(min = 0, max = 10)

-- Positional: one extra argument that the name explains
["a", "b"] \string.join("-")
"nanyx" \startsWith("nan")
```

The functions with named parameters are `string.split`, `string.splitOnce`, `string.substring`, `string.padStart`, `string.padEnd`, `string.replace`, `math.clamp` and `list.replicate`, `json.printPretty`'s `indent` in `nanyx.json`, and `Regex.replace`'s `with` in `nanyx.regex`. Everything else takes its arguments by position.

Passing one of these by position is an error that names the parameters to write:

```nanyx fragment
"7" \padStart(3, "0")
-- error NYX0218: 'string.padStart' takes 'width' and 'fill' by name, not by position: write 'width = ...' and 'fill = ...'
```

# Core abstractions

These exist today:

- `list(a)` for indexed lists ([lists](./prelude-lists))
- `seq(a)` for lazy sequences, ranges among them ([sequences](./prelude-sequences))
- `set(a)` for membership checks ([maps and sets](./prelude-maps))
- `map(k, v)` for key/value lookup ([maps and sets](./prelude-maps))
- `Array(a)` for fixed-length sequences read by index in constant time ([array](./array))
- `Stack(a)` and `Queue(a)` for an immutable stack and queue ([stack](./stack), [queue](./queue))
- `Trie(k, v)` and `StringTrie(v)` for maps whose keys are sequences, with prefix queries ([trie](./trie))
- `MRef(a)` for a cell that is a value, inside a `memory` block ([mref](./mref))
- `MStack(a)` and `MList(a)` for a stack and a list changed in place, inside a `memory` block ([mstack](./mstack), [mlist](./mlist))
- `MArray(a)` for a fixed-length array changed in place, inside a `memory` block ([marray](./marray))
- `Slice(a)`, `MSlice(a)` and `StringSlice` for views of part of an array, a mutable array or a string, which share its storage ([slice](./slice), [mslice](./mslice), [text](./text#stringslice))
- `Task(a)` for a computation started with `start!`, which runs while its starter does other things until it is awaited ([async](./async#task))

Waiting is not a type but a context: a function that can wait requires [`Async`](./async). There is no stream type either: a stream is a `seq` whose steps require `Async` ([Streams: async sequences](../work-in-progress/concurrency-and-crash-boundaries#streams-async-sequences)).

There is no `Option` type: any union containing `#some(a)` is [option-like](../advanced/option-like-types) by convention, and the [option helpers](./prelude-results#options) work on all of them.
