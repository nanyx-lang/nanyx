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

- Done: **the toolchain writes `.wasm`.** A browser loads only the binary format. The compiler assembles its
  WebAssembly text itself (`WasmBinary.fs`), without Wasmtime, so a browser build of the compiler can do it too, and
  `nanyx build` and `nanyx wasm` write `.wasm` for every embedder.
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
   trip. The cost is the download: the .NET runtime, FSharp.Core, FParsec and the compiler, trimmed, and a slow first
   compile. Both are measured below.
2. **On a server.** A small compile service returns the `.wasm` and the diagnostics. The page stays light, but the
   service needs hosting that runs .NET (Vercel's functions don't), limits on time, memory and request rate, and
   someone to look after it. Every keystroke's diagnostics is a request.
3. **Both.** Start with the server and move to the browser once the bundle is small enough.

**Decided: in the browser.** The bar was a bundle under 10 MB compressed and a first compile of a tour example in a few
seconds on a laptop. Measured on 2026-11-17 ([#1234](https://github.com/nanyx-lang/nanyx/issues/1234)), the compiler
in the browser is a 5.5 MB download with brotli, ready to compile a third of a second after the page starts loading (from
localhost), and compiles its first program in about 2.2 seconds and each one after that in 0.2 to 0.8 seconds.
Wasmtime, which runs programs for the CLI, isn't needed: the browser runs the program's WebAssembly itself.

## The measurement

`compiler/Nanyx.Browser` is the compiler's compile path, `Compiler.compile`, `CodeGen.tryTranspileTypedModuleWith`
and `WasmBinary.assemble`, as `nanyx wasm` runs them, built for .NET 10's `browser-wasm` runtime with the
`Microsoft.NET.Sdk.WebAssembly` SDK. Its `BrowserCompiler.Compile` takes a program's source and gives its `.wasm`, its
diagnostics and how long each stage took. Its page, `wwwroot/index.html`, loads the runtime, hands the compiler the
files it reads from beside itself (below), and compiles a hello world and three examples (`fizzbuzzbaz.nyx`, 28
lines; `Nim.nyx`, 181; `shop.nyx`, 377) twice each, checking that each `.wasm` validates. Every one did.

It was measured on a laptop with an Intel Core Ultra 9 285HX, in Chrome 157 and Edge 157, headless, each with a fresh
profile, served from localhost with brotli, three rounds of a cold load and then a load from the cache. The times
below are the medians, in milliseconds. Firefox wasn't measured: it isn't installed on that machine.

| | Chrome | Edge |
| --- | --- | --- |
| Ready to compile, cold | 310 | 388 |
| Ready to compile, cached | 143 | 167 |
| Hello world, first compile | 2,221 | 2,369 |
| Hello world, second compile | 175 | 178 |
| `fizzbuzzbaz.nyx`, first and second | 233, 198 | 266, 217 |
| `Nim.nyx`, first and second | 534, 417 | 579, 462 |
| `shop.nyx`, first and second | 822, 733 | 912, 830 |

"Ready" is from the start of the page's load to the runtime started and the compiler's files handed over. From
localhost it is mostly starting the runtime; over a network, the download comes first: 5.5 MB is about a second at
50 Mbit/s and four and a half at 10. Loaded from the cache, the first compile takes as long as it does cold: it is
the compiler's own warm-up, not the download. For comparison, `nanyx wasm` on the same machine takes 1.5 to 1.7 s for
each of these programs, most of which is starting the process.

**The download**, as the page's resources report it, is 5.46 MB transferred with brotli, 17.8 MB once
decompressed; the page and the examples are a few kilobytes of it. The largest pieces, with brotli: FSharp.Core 988 KB, the runtime's native code 954 KB, `NanyxCompiler` 913 KB, `System.Private.CoreLib`
514 KB, the checked standard library 486 KB, the backend 381 KB and the parser 212 KB.

**The first compile** is most of the wait. Of its 2.2 s, checking takes about 1.9 and lowering 0.25, against 0.08
and 0.09 for a second compile of the same program: the first check reads the checked standard library (`CheckedLibrary.fs`), 2.9 MB that
`BinaryData` reads back through reflection, under the runtime's interpreter. Without the checked library
(`?library=none`) the download is 0.5 MB smaller and the first compile takes 4.1 s, checking the library from its
source, so the library is worth its download. The page can hide most of the wait by compiling a hello world as soon as
the compiler is ready, while you read the example or start typing.

**What had to change.**

- **Threads.** `LargeStack.run` runs the checker on a thread with a 1 GB stack. The browser runs .NET on one thread
  and can't start another, so in the browser it runs the work on the calling thread instead. That thread's stack is
  the runtime's, much smaller, so code nested thousands deep, which the large stack is there for, may run out of
  stack in the browser. None of the examples came near it.
- **Files.** The compiler reads the checked library and the packages it ships (`ShippedPackages`, the console host a
  program's `println` comes from) from beside itself, `AppContext.BaseDirectory`, which in the browser is the
  runtime's in-memory file system. The SDK has no way to put files there, so the page fetches them and passes each to
  `BrowserCompiler.AddFile`, which writes it there before the first compile. The standard library's sources are
  embedded in the compiler, as they are for the CLI.
- **Trimming.** Only the framework is trimmed (`TrimMode` `partial`). Reading the checked library reflects over the
  compiler's F# types, which trimming the compiler's assemblies would break. Trimming them, with what `BinaryData`
  reads kept, would shrink `NanyxCompiler`, the backend and FSharp.Core; how much hasn't been measured.
- **No `wasm-tools`.** The build needs only the .NET SDK. Without the `wasm-tools` workload the compiler runs under the
  runtime's interpreter, and the runtime isn't relinked to drop what the compiler doesn't use. With it, ahead-of-time
  compilation (`RunAOTCompilation`) would make compiles several times faster and the download larger. That is the
  next thing to try if the first compile is too slow, and it wasn't measured here.
- **Globalization.** The compiler runs with invariant globalization, so no culture data is downloaded.

**Reproducing it.** From `compiler/`, with the .NET 10 SDK:

```bash
dotnet publish Nanyx.Browser/Nanyx.Browser.csproj -c Release
```

```bash
node Nanyx.Browser/serve.mjs Nanyx.Browser/bin/Release/net10.0/publish/wwwroot 8123
```

Open `http://localhost:8123/` to see the times, or `http://localhost:8123/?run=cold`, which loads the page again from
the cache when it is done. `serve.mjs` serves each file with brotli and appends each load's report to `results.jsonl`
in the directory it runs in. `Nanyx.Browser/measure.ps1` runs the rounds above: it opens the page headless in each
browser it is given, with a fresh profile, and waits for the reports (`-Library none` leaves out the checked library).
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

1. Done: **Measure the compiler in the browser.** The compile path built for `browser-wasm` (`compiler/Nanyx.Browser`)
   is a 5.5 MB download with brotli, and compiles a first program in about 2.2 s and the next ones in under a second, so the
   compiler runs in the browser (see "Where the compiler runs";
   [#1234](https://github.com/nanyx-lang/nanyx/issues/1234)).
2. Done: **Write `.wasm`.** The compiler writes the binary format, for `nanyx build` too, with tests that the binary runs as
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
