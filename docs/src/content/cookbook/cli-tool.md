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

```nanyx
import (
  nanyx/collections
  nanyx/file
  nanyx/string
)

match file.read("input.txt")
  | #some(content) ->
    def words = content
      \split(on = " ")
      \filter { != "" }

    def count = words.length
    println("Word count: {count}")
  | #error(err) ->
    println("Error reading file: {err}")
```

# Run it

```bash
echo "hello world from nanyx" > input.txt
nanyx run
# Word count: 4
```

# Key Concepts

- **File I/O** — the `file` module returns result types for safe error handling
- **Pipelines** — chain transformations with `\` for readable data processing
- **Pattern matching** — handle success and failure cases explicitly
