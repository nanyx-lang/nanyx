---
title: env
description: A program's arguments, environment variables and exit code
order: 15
---

Import it as `nanyx/env`. The console host provides `Env` to the program's `@entry` and to a script's statements, so
`args`, `variable` and `exit` can be called there without any setup; any other function that calls them must declare
`[Env]` in its type. `nanyx run tool.nyx a b` passes `a` and `b` as the arguments.

```nanyx
import nanyx/console
import nanyx/env

def name =
  match args()
    | [] -> "world"
    | [first, ..._] -> first

match variable("GREETING")
  | #some(greeting) -> println("{greeting}, {name}!")
  | #notSet -> println("Hello, {name}!")

if name == "nobody" then exit(1) else ()
```

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ by `nanyx stdlib-docs`: edit those, not this. -->

What a program is started with, and how it ends: its command-line arguments, its environment
variables and its exit code.

[`Env`](#env) is a context the host provides: under `nanyx/console.host`, a program that imports this
module calls `args`, `variable` and `exit` from its entry point, or from any function that
declares `[Env]`.

## `Env`

```nanyx fragment
context Env = (
  args: () -> list(string)
  variable: string -> #some(string) | #notSet
  exit: int -> undefined
)
```

The process the program runs as. The host supplies the instance: `nanyx/console.host`'s reads
what the program was started with.

- `args`: The program's command-line arguments, after its name: `nanyx run tool.nyx a b` gives
  `["a", "b"]`.
- `variable`: The value of the environment variable `name`, or `#notSet` when it has none.
- `exit`: Ends the program with the exit code `code`, without running the rest of it. It never returns,
  so its result is `undefined` and a call fits wherever a value of any type is expected.

<!-- End of the generated reference. -->
