---
title: "Hello, World!"
description: "Write your first Nanyx program"
order: 3
---

Make sure you've [installed Nanyx](./installation) and have the CLI available. Then, let's create a simple "Hello, World!" program.

# Hello, World!

```bash
touch hello.nyx
```

Open `hello.nyx` in your code editor and add the following code:

```nanyx
import nanyx/console

println("Hello, Nanyx!")
```

Run your program with:

```bash
nanyx run hello.nyx
# Hello, Nanyx!
```

Wow, that was pretty simple! Next, let's create your first Nanyx project and run it.

# Create a project

```bash
nanyx new hello_world
cd hello_world
```

This creates a new project with the following structure:

```
hello_world/
├── hello_world.nyxp
└── src/
    └── main.nyx
```

# Your first program

Open `src/main.nyx` and you'll see:

```nanyx
module main

@entry
def main = {
  println("Hello, World!")
}
```

Notice how, once we move up to a project structure, we have a `module` declaration at the top of our files. This is how we organize code in Nanyx. Named modules don't run statements at the top level; instead the program starts at the function marked with the `@entry` attribute.

# Run it

As long as you're in the project directory, you can run the program without specifying the entrypoint file:

```bash
nanyx run
# Hello, World!
```

# Understanding the code

- `module main` — declares the module name
- `@entry` — marks the function below as the program's entry point
- `def main = { ... }` — the entry point function, which runs in the `Console` context
- `println(...)` — prints a line to standard output

# What's next?

You're ready to explore the language! Head to the [Language Tour](/docs/tour/variables) to learn Nanyx step by step.
