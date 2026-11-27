---
title: Build a CLI Tool
description: Create a command-line application
order: 1
---

Let's build a simple CLI tool that counts words in a file.

# Setup

```bash
nanyx new word_counter
cd word_counter
```

# The code

The program reads its input with the file system's package, `nanyx.fs`, which the compiler ships. Add it to the
manifest's `dependencies`, at the compiler's version:

```nanyx fragment
dependencies = [(package = "nanyx.fs", version = "0.52.0")]
```

Then `src/main.nyx` reads the file and counts its words:

```nanyx fragment
module main

import (
  nanyx/text
  nanyx.fs/file as file
  nanyx.fs/path as path
)

@entry
def main = {
  match file.read(path.of("input.txt"))
    | #some(content) ->
      def words = content
        \split(on = " ")
        \filter { != "" }

      println("Word count: {words.length}")
    | #notFound -> println("There is no input.txt here")
    | err -> println("Error reading file: {err}")
}
```

# Run it

```bash
echo "hello world from nanyx" > input.txt
nanyx run
# Word count: 4
```

# Key Concepts

- **File I/O** — `nanyx.fs`'s `file` module returns `#some(text)` or `FsError`, and `nanyx run` lets the program read only the current directory (see [file](../stdlib/file))
- **Pipelines** — chain transformations with `\` for readable data processing
- **Pattern matching** — handle success and failure cases explicitly
