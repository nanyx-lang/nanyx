---
title: "Playground"
description: "What is still planned for the playground, the page on nanyx.dev where you write Nanyx, run it and share it"
order: 3
---

The [playground](/playground) and the runnable examples in the tour are built. How to use them is on
[Playground](../standard-tools/playground) in Standard tools, and how they work on
[Playground](../deep-dives/playground) in Deep dives. This page is what is still planned.

# Plan

1. **Diagnostics in the editor**, underlined as you type with the same messages and codes as the CLI, each code linked
   to its section of [Compiler errors](../standard-tools/errors); with [deferred errors](./deferred-errors), a program
   with errors still runs up to the first one, as `nanyx run` does. Then a pane that shows the WebAssembly text the
   program compiled to, for people curious about the backend, and for bug reports
   ([#2154](https://github.com/nanyx-lang/nanyx/issues/2154)).
2. **A trap's stack.** A trap shows the message the program wrote, but not the stack the CLI shows, which needs the
   module's table of places read in the page.
3. **Later:** hover types and inlay hints as in the [editor](../standard-tools/vs-code-extension), from the same
   compile; files and HTTP emulated in the page; a choice of compiler version; examples beyond the tour's, such as the
   [Nim demo](./nim-demo); and more done for phones than the stacked layout.

# Making it faster

The first compile takes about 2.2 s on a laptop, most of it the checker's first read of the checked standard library
([the measurement](../deep-dives/playground#the-measurement)), and the compiler runs on the page's main thread, so the
page doesn't respond while it compiles. Ways to do better, none of them measured yet:

- **Ahead-of-time compilation.** With the `wasm-tools` workload, `RunAOTCompilation` would make compiles several times
  faster and the download larger.
- **Trimming the compiler's assemblies**, with what `BinaryData` reads kept, would shrink `NanyxCompiler`, the backend
  and FSharp.Core.
- **Compiling in a worker**, so that the page responds while it compiles.

# Open questions

- **Packages.** A program that imports a package needs its source. The playground supports only the standard library
  and the packages the compiler ships.
- **Sharing long programs.** A URL holds a few kilobytes of compressed code comfortably. Longer programs could be
  shared as a GitHub gist instead.
