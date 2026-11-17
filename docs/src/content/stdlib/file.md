---
title: file
description: Reading and writing files
order: 15.2
---

The functions of `nanyx.fs/file` read and write files, each requiring [`FileSystem`](./filesystem). Import the module
under its own name, `import nanyx.fs/file as file`, to call them as `file.read`, `file.write` and so on. Each
gives what went wrong as an `FsError`, which a `match` takes apart:

```nanyx
::dependency nanyx.fs

import (
  nanyx.fs/file as file
  nanyx.fs/path as path
)

match file.read(path.of("config.json"))
  | #some(text) -> println(text)
  | #error(#notFound) -> println("No config file")
  | #error(e) -> println("Couldn't read the config: {e}")

-- No config file
```

Writing a file makes it, or replaces what it held, and appending adds to its end:

```nanyx fragment
def log = path.of("run.log")

match file.write(log, "started\n")
  | #ok -> ignore(file.append(log, "finished\n"))
  | #error(e) -> eprintln("Couldn't write the log: {e}")
```

`file.lines` reads a file a line at a time, lazily, so a file of any size is never held whole. It gives `#error` when
the file can't be read, and otherwise a sequence that reads a chunk of the file each time it needs more:

```nanyx fragment
match file.lines(path.of("access.log"))
  | #some(lines) ->
    def errors = lines \seq.filter { line -> string.contains(line, " 500 ") }
    println("{seq.fold(errors, 0, { count, _ -> count + 1 })} server errors")
  | #error(e) -> eprintln("Couldn't read the log: {e}")
```

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

Reading and writing files.

Each function requires [`FileSystem`](filesystem), and gives what went wrong as an
[`FsError`](filesystem): reading a file that isn't there is `#error(#notFound)`, and one outside the
directories the host gave the program is `#error(#permissionDenied)`. Import the module as
`import nanyx.fs/file as file` to call `file.read`, `file.write` and the rest by those names.

## `read`

```nanyx fragment
def read: [FileSystem] Path -> #some(string) | #error(FsError)
```

The text of the file at `at`, read as UTF-8.

## `readBytes`

```nanyx fragment
def readBytes: [FileSystem] Path -> #some(array(U8)) | #error(FsError)
```

The bytes of the file at `at`.

## `write`

```nanyx fragment
def write: [FileSystem] Path, string -> #ok | #error(FsError)
```

Writes `text` to the file at `at` in UTF-8, in place of what it held, and makes the file when
there is none. Its directory must be there already.

## `append`

```nanyx fragment
def append: [FileSystem] Path, string -> #ok | #error(FsError)
```

Writes `text` to the end of the file at `at` in UTF-8, and makes the file when there is none.

## `exists`

```nanyx fragment
def exists: [FileSystem] Path -> bool
```

Whether there is a file or a directory at `at` that the program may reach.

## `delete`

```nanyx fragment
def delete: [FileSystem] Path -> #ok | #error(FsError)
```

Deletes the file at `at`. A directory is `#error(#isDirectory)`: this deletes only files.

## `lines`

```nanyx fragment
def lines: [FileSystem] Path -> #some([FileSystem] seq(string)) | #error(FsError)
```

The lines of the file at `at`, read lazily, a chunk at a time: a file larger than memory is
never held whole. Each line is without its line break, `\n` or `\r\n`, and a last line with none
is a line too. The first chunk is read when this is called, and `#error` says why the file can't
be read; after that, the file is opened, read and closed again for each chunk the sequence pulls,
so a sequence abandoned before its end leaves nothing open. A read that fails partway, because the
file was deleted or cut short, say, stops the program with a crash naming the file and why: the
sequence never ends early, so a short file and a failed read can't be mistaken for each other.

```nanyx fragment
match file.lines(path.of("big.log"))
  | #some(lines) ->
    for lines | line -> println(line)
  | #error(e) -> println("Couldn't read the log: {e}")
```

<!-- End of the generated reference. -->