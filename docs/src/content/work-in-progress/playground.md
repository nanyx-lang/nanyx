---
title: "Playground"
description: "Proposal: a page on nanyx.dev where you write Nanyx, run it and share it, with nothing to install"
order: 3
---

This page proposes a playground at `nanyx.dev/playground`: an editor, a Run button, the program's output, and a link
that shares what you wrote. The compiler runs in the browser, and so do programs, under a host of their own that
reads input typed into the page; the page itself is still to build.

# What it does

- **Write and run.** An editor with Nanyx highlighting, and Run, which compiles the code and runs it, showing what it
  prints. Stop ends a program that doesn't finish.
- **See errors as you type.** Diagnostics are underlined in the editor with the same messages and codes as the CLI,
  each code linked to its section of [Compiler errors](../standard-tools/errors). With
  [deferred errors](./deferred-errors), a program with errors still runs up to the first one, as `nanyx run` does.
- **Start from an example.** A menu of examples, starting with the tour's, which are already whole programs that
  [are compiled and run by the tests](../deep-dives/compilation#tour-examples). A tour example can link to "open in the
  playground".
- **Share.** The code is kept in the page's URL, so a link reproduces it with no server storage.
- **Look underneath.** A pane shows the WebAssembly text the program compiled to, for people curious about the
  backend, and for bug reports.
- **Say which compiler.** The page names the compiler release it runs, and links to its release notes.

Later, hover types and inlay hints as in the [editor](../standard-tools/vs-code-extension), and a choice of compiler
version. The [Nim demo](./nim-demo) is planned as one of its first examples.

# Running a program in the page

Done: programs run under a host of their own, `nanyx.playground`, a browser stand-in for the console host
([#2150](https://github.com/nanyx-lang/nanyx/issues/2150)). It provides `Console`, `Random`, seeded by the page, and
`Clock`, on imports of its own that the JavaScript the package ships implements, and nothing else, so a program that
needs `FileSystem`, HTTP, `Env` or `Async` is a compile error naming the host. Programs are source-identical: the
compiler built for the browser runs a script under it unless the script names another host. The program runs in a web
worker, so Stop, or a time limit, terminates the worker, and what it prints, and what its `dbg`s print, reach the page
as it prints them. How it works is on [Hosts](../deep-dives/hosts) in Deep dives.

Still open: a trap shows the message the program wrote, but not the stack the CLI shows, which needs the module's table
of places read in the page.

**Browsers.** The backend emits `return_call` for [tail calls](../deep-dives/tail-calls), which Safari has had since 18.2;
Chrome and Firefox have had it for longer. An older browser gets a message saying so instead of a broken page.

# Where the compiler runs

The compiler is .NET (F# and C#). Either it runs in the browser too, or the page sends the code to a server that
compiles it.

1. **In the browser.** .NET compiles to WebAssembly (the `browser-wasm` runtime), so the compiler can run in the same
   worker as the program, or a second one. The site stays static, so Vercel serves it as it serves the docs today.
   It works offline, costs nothing per compile, and has no server to abuse. Diagnostics as you type need no round
   trip. The cost is the download: the .NET runtime, FSharp.Core and the compiler, trimmed, and a slow first
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
`BinaryData` reads back under the runtime's interpreter, with the readers it emits as IL for each type. Without the checked library
(`?library=none`) the download is 0.5 MB smaller and the first compile takes 4.1 s, checking the library from its
source, so the library is worth its download. The page can hide most of the wait by compiling a hello world as soon as
the compiler is ready, while you read the example or start typing.

**What had to change.**

- **Threads.** `LargeStack.run` runs the checker on a thread with a 1 GB stack. The browser runs .NET on one thread
  and can't start another, so in the browser it runs the work on the calling thread instead. That thread's stack is
  the runtime's, much smaller, so code nested thousands deep, which the large stack is there for, may run out of
  stack in the browser. None of the examples came near it.
- **Files.** The compiler reads the checked library and the packages it ships (`ShippedPackages`, the host a
  program's `println` comes from) from beside itself, `AppContext.BaseDirectory`, which in the browser is the
  runtime's in-memory file system. The SDK has no way to put files there, so the page fetches them and passes each to
  `BrowserCompiler.AddFile`, which writes it there before the first compile. The build lists the packages' files in
  `package-files.txt` beside the page, which `wwwroot/compiler.js` reads, so a file added to a package is handed over
  without anyone listing it ([#2142](https://github.com/nanyx-lang/nanyx/issues/2142)). The standard library's sources are
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
`http://localhost:8123/run.html` compiles a program and runs it under the playground host, and
`node Nanyx.Browser/playground-test.mjs` is the headless test of that (see
[Hosts](../deep-dives/hosts) in Deep dives).

**Building it.** The docs site is built on Vercel, which has no .NET SDK, so it can't build the compiler. Proposed:
the Compiler workflow's `Release` job publishes the browser build as a release asset, `nanyx-browser.zip`, and the
docs build downloads the release it names; neither does so yet. The playground is then pinned to a compiler release
and names it, and moving it on to a newer release is a one-line change.

# The editor

- **CodeMirror 6** is small and loads fast. Highlighting needs a grammar of its own, or the existing TextMate grammar
  (`extension/syntaxes/nanyx.tmLanguage.json`, which the docs site already uses through Shiki) run through an adapter.
- **Monaco** is the editor VS Code is built on, so it reads the TextMate grammar, and later the language server's
  results, the way the extension does. It is several times larger.

**Proposed: CodeMirror**, with diagnostics from the compiler's own result rather than the language server. Hover and
inlay hints come later, from the same compile, once the compiler runs in the page.

# Input

Done: a program reads lines typed into the page ([#2151](https://github.com/nanyx-lang/nanyx/issues/2151)).
`Console.readLine` blocks, and a page can't wait on its main thread, but the worker the program runs in can: it waits
with `Atomics.wait` on a buffer it shares with the page, and the page fills the buffer when you type a line. The runner
calls the page back when the program waits for a line, which is when the output pane shows an input line; Enter gives
the line, and Ctrl+D ends the input, which gives the program `#error(#endOfInput)`. How it works is on
[Hosts](../deep-dives/hosts) in Deep dives.

Shared buffers need the page to be cross-origin isolated: the playground's route, `/playground` and everything under it,
is served with `Cross-Origin-Opener-Policy: same-origin` and `Cross-Origin-Embedder-Policy: require-corp`
(`docs/vercel.json`). Under those headers the page can load only what is from the site itself or is served with a
`Cross-Origin-Resource-Policy` header, so the page ([#2153](https://github.com/nanyx-lang/nanyx/issues/2153)) loads
its scripts, fonts and editor from the site. In a page that isn't isolated, a program's input has ended before it
starts.

# The page's layout

What the page has is settled ([#2153](https://github.com/nanyx-lang/nanyx/issues/2153)); how it is laid out isn't. These
are the questions to answer before building it, each with a proposal.

1. **The site around it.** Either the docs site's header (logo, search, GitHub) over the playground, without the
   docs' sidebar and table of contents, or a page of its own that fills the window. **Proposed: the site's header**,
   and the playground fills the rest of the window, so it is plainly part of nanyx.dev and search still works.
2. **Editor and output.** **Proposed: side by side** on a wide window, the editor on the left with about 60% of the
   width, the output on the right; **stacked** below about 900 pixels, the editor above. No draggable divider in the
   first version.
3. **The controls.** **Proposed: one toolbar above the editor**: Run (with Ctrl+Enter and ⌘+Enter), Stop, which
   replaces Run while a program runs, the examples menu, and Share, which copies the link and says so; at its right end,
   the compiler release, linking to its notes. A "loading the compiler" note sits in the toolbar, with Run disabled,
   until the compiler is ready.
4. **The output pane.** **Proposed: one stream, in the order things happened**, as a terminal shows them: standard
   error in the error colour, each `dbg` on a line of its own, dimmed and prefixed with its place (`[main.nyx:3]`), and
   the end as the CLI shows it (a crash's message, or "exited with 2"; nothing for 0). The input line is the pane's
   last line while the program waits, as a terminal's prompt is, and a line typed stays in the output. A program that
   doesn't compile shows its errors there instead, each code linking to [Compiler errors](../standard-tools/errors).
   Each Run clears the pane.
5. **Opening a share link.** **Proposed: the page shows the code and doesn't run it** until you press Run, so a link
   can't start a program you haven't seen.
6. **What the editor starts with.** A share link's code; else what you last wrote, kept in the browser's storage, so
   reloading loses nothing; else the default example. **Proposed: the tour's hello world as the default**, since
   the headless test runs it; an example that reads input (such as [Nim](./nim-demo)) is in the menu.
7. **The examples menu.** **Proposed: a short list chosen by hand**, each with a title: hello world, a few of the
   tour's examples that show the language best, `fizzbuzzbaz.nyx`, and the Nim game, which reads input. Choosing one
   replaces the editor's code, after asking when it has changes.
8. **The share link's form.** **Proposed: the code in the URL's fragment**, compressed with deflate and written in
   base64url (`/playground#code=...`): a fragment isn't sent to the server, so it isn't in its logs and has no length
   limit there. A link that doesn't decode opens the default example with a message saying so.
9. **Getting there.** **Proposed: "Playground" in the site's header**, beside search, and "open in the playground"
   under the tour's examples. These links must load the page afresh, not through the site's client router
   (`data-astro-reload`): a page swapped in by the router isn't cross-origin isolated, since the headers come with the
   page's own response, and its programs couldn't read input.
10. **Light and dark.** **Proposed: the editor and output follow the site's theme**, which follows the system's.
11. **Phones.** **Proposed: the stacked layout works on a phone** (you can read, run and share), but the first version
    does nothing more for them.

# Plan

1. Done: **Measure the compiler in the browser.** The compile path built for `browser-wasm` (`compiler/Nanyx.Browser`)
   is a 5.5 MB download with brotli, and compiles a first program in about 2.2 s and the next ones in under a second, so the
   compiler runs in the browser (see "Where the compiler runs";
   [#1234](https://github.com/nanyx-lang/nanyx/issues/1234)).
2. Done: **Write `.wasm`.** The compiler writes the binary format, for `nanyx build` too, with tests that the binary runs as
   the text does ([#1235](https://github.com/nanyx-lang/nanyx/issues/1235)).
3. Done: **The `nanyx.playground` host** and its runner in a worker, with the entry callable by the page
   ([#2150](https://github.com/nanyx-lang/nanyx/issues/2150)).
4. Done: **Input**, through an input line in the page ([#2151](https://github.com/nanyx-lang/nanyx/issues/2151)).
5. **The browser build as a release asset**, fetched by the docs build. Decided: the playground runs the latest
   release, not also a build of `main` ([#2152](https://github.com/nanyx-lang/nanyx/issues/2152)).
6. **The page.** `/playground` on the docs site: CodeMirror, Run and Stop, the output, examples and share links. Decided:
   it downloads the compiler at page load and compiles a hello world at once, so the first Run is quick
   ([#2153](https://github.com/nanyx-lang/nanyx/issues/2153)).
7. **Diagnostics in the editor**, then the WebAssembly text pane
   ([#2154](https://github.com/nanyx-lang/nanyx/issues/2154)).
8. **Later:** files and HTTP emulated in the page, hover and inlay hints, and a choice of compiler version.

# Open questions

- **Packages.** A program that imports a package needs its source. The first version supports only the standard
  library.
- **Sharing long programs.** A URL holds a few kilobytes of compressed code comfortably. Longer programs could be
  shared as a GitHub gist instead.
