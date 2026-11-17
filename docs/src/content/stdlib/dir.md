---
title: dir
description: Listing and making directories
order: 15.3
---

The functions of `nanyx.fs/dir` list and make directories, each requiring [`FileSystem`](./filesystem). Import the
module under its own name, `import nanyx.fs/dir as dir`, to call them as `dir.list` and `dir.create`. A listing
gives the path of each thing in the directory, sorted, so it can be read straight away:

```nanyx fragment
match dir.list(path.of("notes"))
  | #some(paths) ->
    for note in paths do
      println(note)
  | #error(#notFound) -> println("No notes yet")
  | #error(e) -> eprintln("Couldn't list the notes: {e}")
```

# Reference

<!-- The reference below is generated from the doc comments in stdlib/ and packages/ by `nanyx stdlib-docs`: edit those, not this. -->

Listing and making directories.

Each function requires [`FileSystem`](filesystem), and gives what went wrong as an
[`FsError`](filesystem). Import the module as `import nanyx.fs/dir as dir` to call `dir.list` and
`dir.create` by those names.

## `list`

```nanyx fragment
def list: [Async, FileSystem] Path -> #some(list(Path)) | #error(FsError)
```

The paths of what the directory at `at` holds, its files and directories, each `at` joined with
its name, sorted by name.

## `create`

```nanyx fragment
def create: [Async, FileSystem] Path -> #ok | #error(FsError)
```

Makes the directory `at`. Its parent must be there already, and `#error(#alreadyExists)` says
something is at `at` already.

<!-- End of the generated reference. -->