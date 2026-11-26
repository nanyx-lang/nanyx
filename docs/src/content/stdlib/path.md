---
title: path
description: Paths to files and directories
order: 15.4
---

`Path` is in the file system's interface package, `nanyx.fs`, and the console host's prelude brings the type into
every module of a program. Import the module under its own name, `import nanyx.fs/path as path`, to make one with
`path.of` and take it apart with `path.parent`, `path.fileName` and `path.extension`. A path is written with
`/` between its parts on every system, so a program's paths read the same wherever it runs:

```nanyx
::install nanyx.fs

import nanyx.fs/path as path

def log = path.join(path.of("logs\\2026"), "today.txt")

println(log)
println(path.parent(log))
println(path.fileName(log))
println(path.extension(log))
-- logs/2026/today.txt
-- #some(logs/2026)
-- #some(today.txt)
-- #some(txt)
```

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

Paths to files and directories.

A [`Path`](#path) is made from text with [`of`](#of), which normalises it: its parts are separated by `/`,
whichever separator the text used, and the empty parts, the `.` parts and each `..` that follows a
part are taken out. So `path.of("notes\\./drafts/../todo.txt")` is `notes/todo.txt`. A path that
starts with `/`, or with a drive such as `C:`, is absolute; any other is relative, and the empty
path is `.`. Import the module as `import nanyx.fs/path as path` to call `path.of`, `path.join` and
the rest by those names.

## `Path`

```nanyx fragment
type Path = protected string
```

Where a file or a directory is, written with `/` between its parts. Make one with [`of`](#of): a
string is never taken for a path, so a string passed where a path was meant is an error.

## `of`

```nanyx fragment
def of: string -> Path
```

The path `text` names, normalised: `path.of("logs\\today.txt")` is `logs/today.txt`, and
`path.of("a/./b/../c")` is `a/c`.

## `join`

```nanyx fragment
def join: Path, string -> Path
```

The path `part` names within `base`: `path.join(path.of("logs"), "today.txt")` is
`logs/today.txt`. When `part` is absolute, it is the path `part` names, as it is.

## `parent`

```nanyx fragment
def parent: Path -> #some(Path) | #none
```

The directory `at` is in: `logs` for `logs/today.txt`, and `.` for `today.txt`. A root, and the
empty path `.`, have none.

## `fileName`

```nanyx fragment
def fileName: Path -> #some(string) | #none
```

The last part of `at`: `today.txt` for `logs/today.txt`. A root, `.` and a path that ends in `..`
have none.

## `extension`

```nanyx fragment
def extension: Path -> #some(string) | #none
```

What follows the last `.` in the file name of `at`, without the dot: `txt` for
`logs/today.txt`. A name with no `.`, one that ends in `.`, and one whose only `.` starts it, as
`.gitignore` does, have none.

## `Path.toString`

```nanyx fragment
def Path.toString: Path -> string
```

`at` written with `/` between its parts, as [`of`](#of) normalised it.

<!-- End of the generated reference. -->