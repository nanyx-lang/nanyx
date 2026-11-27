---
title: filesystem
description: The file system, as a context a host provides
order: 15.1
---

`FileSystem` is in the file system's interface package, `nanyx.fs`, which the compiler ships. The console host
provides it, and its prelude brings `FileSystem`, `Path` and `FsError` into every module of a program; the
functions that use it are in [`file`](./file) and [`dir`](./dir), and paths are made with [`path`](./path). A
script names the package with `::install nanyx.fs`, and a package lists it in its manifest's `dependencies` at the
compiler's version. Any function that reads or writes files declares `[Async, FileSystem]` in its type, as a
library's do, since a call waits for the file system: a library gets `FileSystem`, and `FsError`, by importing
`nanyx.fs/filesystem`, and `Async` by importing `nanyx/async`.

```nanyx
::install nanyx.fs

import (
  nanyx.fs/file as file
  nanyx.fs/path as path
)

def colour: [Async, FileSystem] () -> string = {
  match file.read(path.of("colour.txt"))
    | #some(text) -> text
    | _ -> "blue"
}

println(colour())
-- blue
```

Under `nanyx run`, a program reaches the current directory and what is in it, and the one directory
[`--allow-dir`](../standard-tools/command-line#nanyx-run) names. A path anywhere else, whether relative, as
`../secret.txt`, or absolute, is `#permissionDenied`.

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

The file system, as a context a host provides.

Code that reads or writes files declares `[Async, FileSystem]` and calls the functions of
[`file`](file) and [`dir`](dir), which come from whichever `FileSystem` instance its caller
supplies. The console host (`nanyx.cli.host`) supplies one to the program's entry point, on WASI's
file functions, which sees the current directory and those `nanyx run --allow-dir` adds; a host
that doesn't provide it guarantees that nothing in the program touches the file system. A library
depends on `nanyx.fs` and imports `nanyx.fs/filesystem`.

## `FsError`

```nanyx fragment
type FsError = #notFound | #permissionDenied | #isDirectory | #notDirectory | #alreadyExists | #cancelled | #other(string)
```

Why a file or directory couldn't be read, written, deleted or made: there is nothing at the path;
the program may not reach it (a path outside the directories the host gave it is this too); it
is a directory where a file was meant, or the other way round; there is something at the path
already; or the task it was asked in was cancelled, as a task `race` lost is, so nothing was done.
Anything else is `#other`, with what the host said.

## `FileSystem`

```nanyx fragment
context FileSystem = (
  readFile: [Async] Path -> #some(string) | FsError
  readFileBytes: [Async] Path -> #some(array(U8)) | FsError
  readFileChunk: [Async] Path, int, int -> #some(string) | FsError
  writeFile: [Async] Path, string -> #ok | FsError
  appendFile: [Async] Path, string -> #ok | FsError
  pathExists: [Async] Path -> bool
  deleteFile: [Async] Path -> #ok | FsError
  listDirectory: [Async] Path -> #some(list(string)) | FsError
  createDirectory: [Async] Path -> #ok | FsError
)
```

The file system, which a host supplies. Its members are the operations a host implements; code
calls the functions of [`file`](file) and [`dir`](dir), which are written on them.

- `readFile`: The text of the file at `at`, read as UTF-8.
- `readFileBytes`: The bytes of the file at `at`.
- `readFileChunk`: Up to `length` bytes of the file at `at`, from byte `offset` on: fewer at its end, and none
  past it, but for an `offset` past its end, which is an error: the file is shorter than it was.
  The file is opened for each call, and closed before it returns.
- `writeFile`: Writes `text` to the file at `at` in UTF-8, in place of what it held, making it when there is
  none.
- `appendFile`: Writes `text` to the end of the file at `at` in UTF-8, making it when there is none.
- `pathExists`: Whether there is a file or a directory at `at` that the program may reach.
- `deleteFile`: Deletes the file at `at`.
- `listDirectory`: The names of what the directory at `at` holds, in no order, without `.` and `..`.
- `createDirectory`: Makes the directory `at`, in a directory that is there already.

## `inMemory`

```nanyx fragment
def inMemory: [c] list((string, string)), ([c, FileSystem] () -> a) -> a
```

What a test's `body` gives, run with a `FileSystem` that holds its files in memory, starting with
`files`, each a path and its text: the directories they are in are there too, and so is `.`. A file
`body` writes is there for it to read back, and nothing reaches the disk, so a test of code that
reads and writes files needs neither `@test(#io)` nor a fake of each member. Each member gives the
`FsError` the console host does for the same thing, but for two differences: there is no current
directory besides `.`, so a relative path and an absolute one never name the same file, and nothing
is out of reach, so there is no `#permissionDenied`.

<!-- End of the generated reference. -->