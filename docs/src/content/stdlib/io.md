---
title: "io"
description: "Input and output operations"
order: 2
---

The `io` module provides functions for reading from and writing to standard I/O. Import it as `nanyx/console`; the module with the `@entry` function gets it without importing it.

All functions in this module require a `Console` context. Its members are `write`, `writeError`, `readLine`, `readToEnd` and `flush`; `print`, `println`, `eprint` and `eprintln` are written on top of them. The program's `@entry` function runs in `Console`, so they are available in `main` without any setup; any other function that calls them must declare `[Console]` in its type.

# println

```nanyx
println: [Console & ToString(a)] a -> ()
```

Prints a value followed by a newline to stdout. Any value with a [`ToString`](../advanced/contexts#tostringa) can be printed: a string as it is, a number, a tag, a tuple, a record, a list, or a type with an attached `toString`.

```nanyx
println("Hello, World!")
println(42)
println([1, 2, 3])
-- Prints: [1, 2, 3]
```

A value with no `ToString`, such as a function, is an error at the call.

---

# print

```nanyx
print: [Console & ToString(a)] a -> ()
```

Prints a value to stdout without a trailing newline, as `println` writes it.

```nanyx
print("Enter your name: ")
```

---

# eprintln

```nanyx
eprintln: [Console & ToString(a)] a -> ()
```

Prints a value followed by a newline to stderr, as `println` writes it.

```nanyx
eprintln("Configuration file missing")
```

---

# eprint

```nanyx
eprint: [Console & ToString(a)] a -> ()
```

Prints a value to stderr without a trailing newline.

```nanyx
eprint("warning: ")
```

---

# write

```nanyx
write: [Console] string -> ()
```

Writes a string to stdout exactly as it is. It is a member of `Console`, and `print`, `println` and the others are written on top of it and `writeError`, so an instance of `Console` supplies these two and gets all four.

```nanyx
write("no line break")
```

---

# writeError

```nanyx
writeError: [Console] string -> ()
```

Writes a string to stderr exactly as it is. It is a member of `Console`.

---

# dbg

```nanyx
dbg: [Console] a -> a
```

Prints a debug representation of any value and returns it. Useful for inspecting values in pipelines.

```nanyx
[1, 2, 3]
  \map { * 2 }
  \dbg
-- Prints: [2, 4, 6]
```

---

# readLine

```nanyx
readLine: [Console] () -> #some(string) | #error(IoError)
```

Reads a line of input from stdin, without its line break. At the end of the input it gives `#error(#endOfInput)`: `IoError` is `#endOfInput`.

```nanyx
match readLine()
  | #some(line) -> println("You said: {line}")
  | #error(_) -> println("Failed to read input")
```

---

# readToEnd

```nanyx
readToEnd: [Console] () -> #some(string) | #error(IoError)
```

Reads all remaining input from stdin.

```nanyx
match readToEnd()
  | #some(text) -> println("Read {text.length} chars")
  | #error(_) -> eprintln("Failed to read stdin")
```

---

# flush

```nanyx
flush: [Console] () -> ()
```

Flushes buffered output streams. The runtime doesn't buffer its writes, so this does nothing there; it is a member of `Console` for instances that do.

```nanyx
print("processing...")
flush()
```
