---
title: Overview
description: "The modules of the standard library"
order: 1
---

The standard library is the modules under `nanyx/`. The [prelude](./prelude) is imported into every module, so what it defines needs no import; any other module is imported by name, as `import nanyx/text`. Each page in this section describes one file of the library, and its reference is generated from the doc comments in the library's source, `stdlib/nanyx/`.

# Modules in this section

- [prelude](./prelude) — what every module sees, written across files by topic:
  [contexts](./prelude-contexts), [numbers](./prelude-numbers), [sized numbers](./prelude-sized-numbers),
  [conversions](./prelude-conversions), [lists](./prelude-lists), [maps and sets](./prelude-maps) and
  [results and options](./prelude-results)
- [text](./text) — functions on strings (`nanyx/text`)
- [console](./console) — reading from and writing to the terminal (`nanyx/console`)
- [math](./math) — numeric functions and constants (`nanyx/math`)
- [console.host](./console-host) — the host that runs a command-line program (`nanyx/console.host`)
- [host.runtime](./host-runtime) — the privileged layer a host is written on (`nanyx/host.runtime`)

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

The functions with named parameters are `string.split`, `string.splitOnce`, `string.substring`, `string.padStart`, `string.padEnd`, `string.replace`, `math.clamp` and `list.replicate`. Everything else takes its arguments by position.

Passing one of these by position is an error that names the parameters to write:

```nanyx fragment
"7" \padStart(3, "0")
-- error NYX0218: 'string.padStart' takes 'width' and 'fill' by name, not by position: write 'width = ...' and 'fill = ...'
```

# Core abstractions

These exist today:

- `list(a)` for indexed lists ([lists](./prelude-lists))
- `set(a)` for membership checks ([maps and sets](./prelude-maps))
- `map(k, v)` for key/value lookup ([maps and sets](./prelude-maps))

These are planned, and not yet in the library:

- `seq(a)` for finite sequences. Today `seq(a)` is only the type of a range in a `for` loop; sequences as values are tracked in [#192](https://github.com/nanyx-lang/nanyx/issues/192), and designed in [Lazy sequences](../work-in-progress/lazy-sequences).
- `gen(a)` for potentially infinite sequences
- `lookup(k, v)` for multi-value lookups
- `stream(a)` for async streams, and `async(a)` for async computations

There is no `Option` type: any union containing `#some(a)` is [option-like](../advanced/option-like-types) by convention, and the [option helpers](./prelude-results#options) work on all of them. The `Result(a, e)` type is `#some(a) | #error(e)`.
