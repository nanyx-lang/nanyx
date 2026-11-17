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

<!-- End of the generated reference. -->