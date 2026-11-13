---
title: "Playground"
description: "Proposal: a page on nanyx.dev where you write Nanyx, run it and share it, with nothing to install"
order: 3
---

This page proposes a playground at `nanyx.dev/playground`: an editor, a Run button, the program's output, and a link
that shares what you wrote. It is the first thing to build once Nanyx programs can run in a page. Nothing of it exists
yet.

# What it does

- **Write and run.** An editor with Nanyx highlighting, and Run, which compiles the code and runs it, showing what it
  prints. Stop ends a program that doesn't finish.
- **See errors as you type.** Diagnostics are underlined in the editor with the same messages and codes as the CLI,
  each code linked to its section of [Compiler errors](../standard-tools/errors). With
  [deferred errors](./deferred-errors), a program with errors still runs up to the first one, as `nanyx run` does.
- **Start from an example.** A menu of examples, starting with the tour's, which are already whole programs that
  [are compiled and run by the tests](./compilation#tour-examples). A tour example can link to "open in the
  playground".
- **Share.** The code is kept in the page's URL, so a link reproduces it with no server storage.
- **Look underneath.** A pane shows the WebAssembly text the program compiled to, for people curious about the
  backend, and for bug reports.
- **Say which compiler.** The page names the compiler release it runs, and links to its release notes.

Later, hover types and inlay hints as in the [editor](../standard-tools/vs-code-extension), a choice of compiler
version, and programs that read input. The [Nim demo](./nim-demo) is planned as one of its first examples.

# Running a program in the page

A playground program is a console program, so the page stands in for the terminal. This is the "`ConsoleHost` in a
page" case on the [Hosts](./hosts#consolehost-in-a-page) page, not the [browser host](./hosts#a-browser-host). The
program keeps its `Console` and its `@entry`, and only the embedder changes. The module the backend emits already
imports just `fd_write`, `fd_read` and `proc_exit`, which a page can provide in a few lines of JavaScript. The Hosts
page lists what stands in the way, and each of those is a step of the plan below:

- **The toolchain writes WebAssembly text.** A browser loads only the binary format, so the compiler needs to write
  `.wasm`. That is useful outside the playground too: `nanyx build` could write `.wasm` for every embedder.
- **The program runs in the module's start function**, before the page can reach the module's memory. For the page,
  the module exports its entry for the page to call, or imports its memory.
- **`proc_exit` has to unwind.** The page's `proc_exit` throws, and the code that called the entry catches it and
  reads the exit code.
- **Reading input blocks.** See "Input" below.

The program runs in a **web worker**, not on the page's thread. A program that loops forever then can't freeze the
page: Stop, or a time limit, terminates the worker. The worker posts what the program prints back to the page as it
prints it, so output appears while the program runs. A trap shows as the CLI shows it, with the deferred error's
message when that is what trapped.

**Browsers.** The backend emits `return_call` for [tail calls](./tail-calls), which Safari has had since 18.2;
Chrome and Firefox have had it for longer. An older browser gets a message saying so instead of a broken page.

# Where the compiler runs

The compiler is .NET (F# and C#). Either it runs in the browser too, or the page sends the code to a server that
compiles it.

1. **In the browser.** .NET compiles to WebAssembly (the `browser-wasm` runtime), so the compiler can run in the same
   worker as the program, or a second one. The site stays static, so Vercel serves it as it serves the docs today.
   It works offline, costs nothing per compile, and has no server to abuse. Diagnostics as you type need no round
   trip. The cost is the download: the .NET runtime, FSharp.Core, FParsec and the compiler, trimmed. Starting it the
   first time is slow, and it is cached after that. Neither cost has been measured.
2. **On a server.** A small compile service returns the `.wasm` and the diagnostics. The page stays light, but the
   service needs hosting that runs .NET (Vercel's functions don't), limits on time, memory and request rate, and
   someone to look after it. Every keystroke's diagnostics is a request.
3. **Both.** Start with the server and move to the browser once the bundle is small enough.

**Proposed: in the browser**, if step 1 of the plan measures it as acceptable: say, a bundle under 10 MB compressed,
and a first compile of a tour example in under a few seconds on a laptop. Otherwise, the server. Wasmtime, which runs
programs for the CLI, isn't needed in either case: the browser runs the program's WebAssembly itself.

**Building it.** The docs site is built on Vercel, which has no .NET SDK, so it can't build the compiler. The
Compiler workflow's `Release` job also publishes the browser build as a release asset, `nanyx-browser.zip`, and the
docs build downloads the release it names. The playground is then pinned to a compiler release and names it, and
moving it on to a newer release is a one-line change.

# The editor

- **CodeMirror 6** is small and loads fast. Highlighting needs a grammar of its own, or the existing TextMate grammar
  (`extension/syntaxes/nanyx.tmLanguage.json`, which the docs site already uses through Shiki) run through an adapter.
- **Monaco** is the editor VS Code is built on, so it reads the TextMate grammar, and later the language server's
  results, the way the extension does. It is several times larger.

**Proposed: CodeMirror**, with diagnostics from the compiler's own result rather than the language server. Hover and
inlay hints come later, from the same compile, once the compiler runs in the page.

# Input

`Console.readLine` blocks, and a page can't wait for input on its main thread. In a worker it can: the worker waits
with `Atomics.wait` on a buffer it shares with the page, and the page fills the buffer when you type a line. Shared
buffers need the site to be cross-origin isolated, which means two response headers (`Cross-Origin-Opener-Policy`
and `Cross-Origin-Embedder-Policy`) on the playground's page. Those headers restrict what else the page can embed.
The first version has no input: a program that reads it gets an end of input, and the page says so.

# Plan

1. **Measure the compiler in the browser.** Publish the CLI's compile path for `browser-wasm`, trimmed, and measure
   the download, the start-up and the compile time of a few tour examples. This decides "Where the compiler runs"
   ([#1234](https://github.com/nanyx-lang/nanyx/issues/1234)).
2. **Write `.wasm`.** The compiler writes the binary format, for `nanyx build` too, with tests that the binary runs as
   the text does ([#1235](https://github.com/nanyx-lang/nanyx/issues/1235)).
3. **An entry the page can call.** The module exports its entry, or imports its memory, and `proc_exit` unwinds. A
   runner script, in a worker, provides `fd_write`, `fd_read` and `proc_exit` and runs a module end to end in a
   headless browser test.
4. **The page.** `/playground` on the docs site: the editor, Run and Stop, the output, the examples menu and share
   links, with the compiler from step 1 or a service. Tour examples link to it.
5. **Diagnostics in the editor**, then the WebAssembly text pane.
6. **Later:** input, hover and inlay hints, and a choice of compiler version.

# Open questions

- **The size budget.** What download is acceptable before the first compile, and whether the page loads the compiler
  only when you first press Run or start typing.
- **Which standard library.** The compiler embeds the standard library, so every module is available. Are modules
  whose contexts a page can't provide, such as file access once it exists, hidden, or reported when imported?
- **Packages.** A program that imports a package needs its source. The first version supports only the standard
  library.
- **Versions.** Pinned to the latest release, as proposed, or also a build of `main` for trying what's just merged?
- **Sharing long programs.** A URL holds a few kilobytes of compressed code comfortably. Longer programs could be
  shared as a GitHub gist instead.
