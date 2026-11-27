---
title: "Error handling"
description: "Failures as tags, and option-like types"
order: 16
---

In Nanyx, errors are values: a function that can fail says so in its return type, using tag unions, so callers must handle the failure cases. There is no catchable exception mechanism. A program can still _crash_ on an unrecoverable failure (such as running out of memory), but a crash is not something code is expected to catch or recover from.

To crash on purpose, call the prelude's `crash` with a message. It writes `crash: ` and the message to standard error and ends the program with exit code 1. It never returns, so its type is [`undefined`](./types#top-and-bottom-types) and a call to it fits wherever a value is expected:

```nanyx
def half: int -> int = { n ->
  if n % 2 == 0 then n // 2 else crash("{n} is odd")
}
```

Where a missing value can only be a bug, `orCrash` unwraps it: it gives the value in a `#some`, and crashes with its message on anything else, so it works on any option-like value, whatever its failures. It reads well at the end of a pipeline:

```nanyx
def n = "42" \int.fromString \orCrash("not a number")
println(n + 1)
```

# Failures are tags

A function that can fail gives `#some` with its value when it succeeds, and a tag of its own for each way it can fail:

```nanyx fragment
-- A function that might fail
def getCustomer: CustomerId -> #some(Customer) | #notFound | #databaseError
  = { id -> ... }
```

Handle each case with pattern matching:

```nanyx fragment
match getCustomer(someId)
  | #some(customer) -> println("Found: {customer.name}")
  | #notFound -> println("Customer not found")
  | #databaseError -> println("Database error")
```

A failure that has something to say carries it as its payload, under a tag named for what went wrong: `#invalidPattern(RegexError)`, `#unreadable(string)`. A type alias can name a group of failures that several functions share, as the [`nanyx.fs`](../stdlib/filesystem) package's `FsError` does, and a signature writes it beside `#some`: `#some(string) | FsError`. A function that succeeds with nothing to give back has `#ok` in place of `#some`: `#ok | FsError`. The alias stands for its tags, so the union holds them all, but hover, an error message and the reference pages write it as it was written: a union that holds every tag of an alias in scope, with the same payloads, is written with the alias's name, while one with only some of them is written out.

# The `try` keyword

`proc` is the standard library's result [workflow](../advanced/workflows). Inside a `proc` block, `try` continues with the value of a `#some` and short-circuits on any other tag, and `return` wraps the final value in `#some`:

```nanyx fragment
def result = proc {
  def customer = try getCustomer("someid")
  def latestOrder = try getLatestOrder(customer)
  return latestOrder.total
}
-- `result` has type `#some(int) | #notFound | #databaseError | #noOrders`
```

If any step fails, the block immediately gives back that failure.

The steps' failures are joined: one that fails with `#notFound | #databaseError` and one that fails with `#noOrders` make the block fail with all three, and a match on the result must handle each. [`bindSome`](../stdlib/prelude-results#bindsome) chains steps the same way. Two failures with the same tag must carry the same type, unless both payloads are tags, which are joined too.

A `proc` block that is only run for what it does doesn't need a `return`. A block that ends without one is `#some()`, the `proc` workflow's `zero`:

```nanyx fragment
def greet: [Console] string -> #some() | #invalidName = { name -> proc {
  def person = try Person.new(name)
  println("Hello, {person.name}!")
} }
```

Every way out of a block must agree, so a block that returns a value on one path can't also end without `return` on another: `if n > 2 then return n` on its own is a type error, since the block would be `#some(int)` one way and `#some()` the other.

# Option types

For values that might not exist, use the `#some` / `#none` pattern:

```nanyx fragment
def findUser: int -> #some(User) | #none = { id ->
  match id
    | 1 -> #some(User(name = "Alice"))
    | _ -> #none
}
```

# Falling back with `??`

`a ?? b` unwraps an option-like value: it is the value inside `a`'s `#some`, or `b` for any other tag. `b` is only evaluated when it is needed, so it can be a call that does work:

```nanyx
def port: string -> #some(int) | #invalidInt = { text ->
  int.fromString(text)
}

def defaultPort: [Console] () -> int = {
  println("using the default port")
  8080
}

@entry
def main = {
  println(port("3000") ?? defaultPort())
  println(port("http") ?? defaultPort())
}

-- 3000
-- using the default port
-- 8080
```

`??` binds tighter than the comparisons and `and`, and looser than arithmetic: `a ?? b == c` is `(a ?? b) == c`, `a ?? 0 < n` is `(a ?? 0) < n`, `x and y ?? z` is `x and (y ?? z)`, and `a ?? b + 1` is `a ?? (b + 1)`. It groups to the right, so `a ?? b ?? c` tries `a`, then `b`, and then settles for `c`. In such a chain every operand but the last is option-like, and the last is a plain value.

`??` only unwraps: its right operand is the value itself, not another option. To fall back to another option, keeping the result option-like, use [`orElseSome`](../stdlib/prelude-results#orelsesome):

```nanyx fragment
cached(key) \orElseSome { lookup(key) }
```

# The `except` keyword

`value except pattern -> replacement` handles some of the cases of `value` and lets the rest through. A case the pattern matches becomes the replacement, and every other case passes through unchanged. It's a `match` that doesn't have to handle every case.

```nanyx
def parseNumber = { s ->
  int.fromString(s) except #invalidInt -> #invalidVersionNumber
}
```

`int.fromString(s)` is `#some(int) | #invalidInt`. The handled tag leaves the union and the replacement's type joins it, so `parseNumber` returns `#some(int) | #invalidVersionNumber`.

To handle several cases, write the arms on the lines after `except`, as a `match` has them. An arm may bind the parts of what it matches, and may list alternatives:

```nanyx fragment
def load: string -> #some(Config) | #notFound | #unreadable(string) | #denied = ...

def config = load(path) except
  | #notFound -> #some(Config.default)
  | #unreadable(reason) -> #some(Config.default with note = reason)
-- `config` is `#some(Config) | #denied`
```

A pattern can reach inside a tag. When a failure's payload is a union of tags, as in `#invalid(#tooShort | #tooLong)`, handling `#invalid(#tooShort)` takes `#tooShort` out of the payload and leaves `#invalid(#tooLong)`. When every tag of the payload is handled, the failure leaves the union altogether.

Because the narrowed type often has a single case left, `except` combines naturally with a destructuring binding, which requires its pattern to be total:

```nanyx fragment
def g = {
  def f: () -> #some(int) | #notFound = ...

  -- After `except`, the only remaining case is `#some(int)`, so the
  -- pattern `#some(result)` is total and `result` has type `int`
  def #some(result) = f() except #notFound -> #some(0)

  result
}
```

In a builder block such as `proc { ... }`, the replacement can end the block instead, with `return` (see [Workflows](../advanced/workflows#keywords-in-branches-loops-and-lambdas)):

```nanyx
def find: int -> #some(int) | #notFound = { id ->
  if id > 0 then #some(id * 10) else #notFound
}

def lookup = { id -> proc {
    def #some(result) = find(id) except #notFound -> return 0
    return result + 1
  }
}

@entry
def main = {
  println(lookup(4))
  println(lookup(-1))
}

-- #some(41)
-- #some(0)
```

`except` binds more loosely than every operator and pipe, so it applies to the whole expression before it:

```nanyx
def scores = [3, 9, 4]

def #some(shift) = scores \indexOf(4 + 5) except #notFound -> #some(0)

println(shift)

-- 1
```

A pattern that the value can never match is a type error, as it is in a `match`. An `except` whose arms handle every case is a `match`, so write it as one.

# Chaining with pipelines

The prelude's option helpers take the value first, so a chain of steps reads as a pipeline:

```nanyx
def doubled: string -> int = { input ->
  input
    \int.fromString
    \mapSome { * 2 }
    \someOr(0)
}
```

`int.fromString` gives `#some(int) | #invalidInt`, so the option helpers apply: they work on any union with a `#some` case, and keep its other tags as they are. See [results and options](../stdlib/prelude-results).

# Deferred errors

A type error or an unknown name doesn't stop you running a program. `nanyx run` reports every error, and then runs the program all the same: the code that checked runs as normal, and the program stops only if it reaches code that didn't, with that code's error. So you can try the part of a program you're working on while another part is still broken.

```nanyx
def area: int -> int = { side -> side * side }

def perimeter: int -> int = { side -> side * "4" } -- error NYX0201: a string is not a number

@entry
def main = {
  println(area(3))
  println(perimeter(3))
  println("done")
}
```

`nanyx run shapes.nyx` reports the error, says that it is running in spite of it, and runs `main`:

```text
error NYX0201: Type mismatch
  --> shapes.nyx:3:39
   |
 3 | def perimeter: int -> int = { side -> side * "4" }
   |                                       ^^^^^^^^^^
   = 'string' is not a number, so it cannot be used with *
Running with 1 deferred error
9
shapes.nyx:3:39: error NYX0201: Type mismatch: 'string' is not a number, so it cannot be used with * (deferred from compile time)
  perimeter (shapes.nyx:3)
  main (shapes.nyx:8)
```

`area(3)` runs and prints `9`. `perimeter(3)` reaches the broken expression, which writes its error to standard error on one line, its headline and body joined by a colon, marked `(deferred from compile time)`, with the stack under it, each function the program was in and the line it had reached, innermost first, and ends the program with exit code 1, as `crash` does. `done` is never printed. Everything that runs before the error still happens: in `f(a(), bad, c())`, `a()` is called and `c()` is not.

The error stays where it was found, so the program fails no sooner than it has to:

- **An expression that doesn't check**, such as an unknown name, a type mismatch, a call with the wrong number of arguments, a missing field, or a call that needs a context nothing provides, stops the program when it is evaluated. The rest of its function runs as usual.
- **A call that fails** but whose function's type is known, because it's missing a context, given an argument of the wrong type or the wrong number of them, passed by position what it takes by name, or calling a definition a module block doesn't export, still has the type that function gives back, so its uses are checked as usual. Only what a failed argument would have decided is unknown: `list.map(xs, 5)` is a `list(???)`.
- **A match that doesn't handle every case** runs the cases it handles, and stops the program on a case it doesn't.
- **A definition whose type is wrong**, such as one whose body doesn't fit its annotation, is reported once. Calling it stops the program with its error, and the functions that call it are not errors themselves.
- **`???`**, the [placeholder for unwritten code](../getting-started/basics#unimplemented-code), stops the program when it is reached.

An error in a module the program imports is deferred too. The module's other definitions work as usual, and the program stops when it reaches the error, which names the file and line in that module. It is reported once, however many modules import the one that has it.

Some errors can't be deferred, and with any of them nothing runs: a syntax error, an error in a type definition, an import that can't be found, an imported module with one of these errors, an error in a `::` directive, and a [linter](../advanced/linters) rule configured as an error.

## Debug and release builds

`nanyx run` is a debug build, and so are `nanyx build` and `nanyx wasm` unless you ask for a release build:

| Command | With deferrable errors |
|---------|------------------------|
| `nanyx run` | Reports them, says `Running with 2 deferred errors`, and runs the program |
| `nanyx build`, `nanyx wasm` | Reports them, and writes a program that stops where they are, saying so: `WebAssembly written with 2 deferred errors` |
| `nanyx build --release`, `nanyx wasm --release` | Reports them and writes nothing |
| `nanyx check` | Reports them and runs nothing |

A release build also leaves out every [`dbg`](../stdlib/console#dbg), which prints nothing there. It keeps every
[`assert`](../stdlib/console#assert), which crashes when its condition is false in any build.

Deferred errors don't change the exit status. A debug run exits with the program's own status: 0 if it ends normally without reaching an error, and 1 if it reaches one. A debug build that writes a program exits with 0. A script or CI job that must not pass a program with errors uses `nanyx check` or `--release`, which exit with 1 on any error.

> **Philosophy:** By making errors explicit in the type system, Nanyx ensures you always handle failure cases. No more unexpected crashes!
