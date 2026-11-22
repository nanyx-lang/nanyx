---
title: "Playground"
description: "How the playground page compiles and runs Nanyx in the browser"
order: 10
---

The [playground](/playground) is a page of the docs site where you write Nanyx, run it and share it, with the compiler
running in the page. How to use it is on [Playground](../standard-tools/playground) in Standard tools; the host its
programs run under, its runner and how a program reads input are on [Hosts](./hosts). This page says how the page is
built. What is still planned is on [Playground](../work-in-progress/playground) in Work in progress.

# The page

`docs/src/pages/playground.astro` is the page, at `/playground`, and its scripts are in `docs/src/lib/playground`:

- `main.ts` wires the page: the toolbar, the editor, the output and the divider;
- `compiler.ts` loads the compiler and the runner, once per page, and runs a program into an output (`compileAndRun`);
- `editor.ts` is the editor, CodeMirror 6;
- `terminal.ts` is the output pane;
- `share.ts` reads and writes share links.

The tour's examples run with the same modules, from `tour.ts` (see [The tour's examples](#the-tours-examples)).

The page has the site's header (`Header.astro`, without the docs' sidebar), and under it a toolbar and the editor and
the output, side by side on a wide window, the editor with 60% of the width, and stacked, the editor above, below 900
pixels (`docs/src/styles/playground.css`). The divider between them can be dragged, or moved with the arrow keys; where
it is is kept in the browser's storage. The editor and the output follow the site's light or dark theme, which follows
the system's.

**Loading.** As the page opens, `main.ts` calls `loadPlayground` (`compiler.ts`), which checks that the browser validates a module that uses `return_call`, which the
backend emits for [tail calls](./tail-calls), and says the browser can't run Nanyx programs when it doesn't. It reads
`/playground/compiler/release.json`, which the docs build writes beside the compiler (below), for the release the page
names at the right of the toolbar, linked to the release's notes on GitHub; when there is no such file, the site has no
compiler, and the page says so. Then it imports `compiler.js` and the playground host's runner,
`packages/nanyx.playground/runner/playground.js`, from beside it, loads the compiler, and compiles the default example
at once: the first compile is most of the wait (see [the measurement](#the-measurement)), so the first Run is quick.
"Loading the compiler…" shows in the toolbar, with Run disabled, until then. The compiler runs on the page's main
thread; a program runs in a worker.

**The page's route is cross-origin isolated** (`Cross-Origin-Opener-Policy` and `Cross-Origin-Embedder-Policy` in
`docs/vercel.json`), so that a program can read input ([Hosts](./hosts)). The headers come with the page's own
response, so a page the client router swaps in isn't isolated: the playground's `Base` leaves the router out
(`clientRouter={false}`), and every link to it loads it afresh with `data-astro-reload`: the header's, and in the docs'
markdown, each link to `/playground` (`rehypePlaygroundReload`) and the tour's "Open in the playground" links. Under
those headers the page loads only what is from the site itself, and its scripts, the editor among them, are bundled
into the site's. A page that isn't isolated says that its programs can't read input.

**The editor.** CodeMirror 6, with a highlighter of its own (`StreamLanguage`) for comments, strings, numbers, tags,
attributes, directives and type names, whose keywords are the TextMate grammar's
(`extension/syntaxes/nanyx.tmLanguage.json`): `editor.ts` imports the grammar and reads its `keywords` patterns. Its colours are the
docs' code blocks' (`site/shiki-themes.mjs`). Ctrl+Enter, or ⌘+Enter, runs the program.

**Running.** Run compiles the editor's code (`compileAndRun`) with `BrowserCompiler.Compile` (through `compiler.js`); when it gives a
module, the runner's `run` runs it with a `Random` seeded from `crypto.getRandomValues`, and Stop, which takes Run's
place while it runs, calls the runner's `stop`. `terminal.ts` shows, in one stream, what the program writes to
standard output and, in the error colour, to standard error, each `dbg` dimmed after its place, and the end as
`nanyx run` shows it: a trap's message, `exited with` a code other than 0, or `stopped`. The input line is the pane's
last line while the program waits, from the runner's `onRead`: Enter gives the line, which is written in the output,
and Ctrl+D ends the input. A program that doesn't compile shows its diagnostics instead, each `NYX` code linked to
its section of [Compiler errors](../standard-tools/errors); a program that compiles with warnings shows them before
its output. Each run starts under a divider with its number and time; the pane isn't cleared until **Clear**.

**What the editor starts with.** A share link's code; else the code last written, which the page keeps in the
browser's storage (`nanyx.playground.code`) as it changes; else the default example, the tour's hello world (the
`@entry` section of [Attributes](../tour/attributes)).

**Share links.** Share compresses the editor's code with raw deflate (`CompressionStream("deflate-raw")`), writes it
in base64url, and copies `/playground#code=` and that to the clipboard. A fragment isn't sent to the server, so the code
isn't in its logs and has no length limit there. Opening a link shows its code and doesn't run it, writes it to the
browser's storage, and takes the fragment out of the address, so that a reload keeps what is written after it; a link
opened while the page is open asks before it replaces changed code. A link that doesn't decode opens the default
example, and says so.

**Examples.** The Examples menu is every runnable example in the [language tour](../tour/variables), grouped by its
page in the tour's order, and named by the heading it is under. `docs/site/playground-examples.mjs` reads them: a block
is runnable when it is fenced ` ```nanyx ` with nothing after `nanyx` and has no `-- error` comment, which are the
blocks [the tests run](./compilation#tour-examples). As those tests do, it adds `import nanyx/text` to each that doesn't
import it or turn a linter on, after its module declaration or else after its directives. Choosing an example asks
before it replaces code that was changed since it was opened. What a runnable block is (`isRunnable`) and the code it
runs as (`playgroundCode`) are in `docs/site/playground-code.mjs`, which the tour's pages read in the browser too.

**The test.** `docs/site/playground-test.mjs` serves the built site with the playground's headers and drives the page
in headless Chromium with Playwright: it waits for the compiler, runs hello world, types two lines of input and
Ctrl+D, checks that runs keep the output before them, that a compile error links its code, that Share's link opens the
code without running it, that a bad link opens hello world, that a reload keeps the code, that a tour link opens its
example in an isolated page, and runs every example in the menu, checking that none fails; and the tour's examples
(below). The Compiler workflow's
`Browser test` job builds the site with the compiler it published and runs it.

# The tour's examples

Each runnable example in the [language tour](../tour/variables) is a small playground where it is
([#2248](https://github.com/nanyx-lang/nanyx/issues/2248)). `remarkPlaygroundLinks`, in
`docs/site/playground-examples.mjs`, puts a bar under each block, `<div data-tour-run>`, with an "Open in the playground"
link, a share link the build writes (`shareFragment`). `src/lib/playground/tour.ts`, which every docs page loads
(`DocsLayout.astro`), wires each bar on `astro:page-load`, since the tour's pages are swapped in by the client router:

- It adds **Run**, which loads the compiler and the runner through `loadPlayground` on the first Run on the page, not
  with the page, saying "Loading the compiler…" until they are ready, and then runs the example with `compileAndRun`
  into an output under the bar, a `Terminal` like the playground's, which keeps each run under a divider. Run becomes
  Stop while the program runs; a run that prints nothing says `(no output)`, since most of the tour's examples only
  define things. The example runs as `playgroundCode` makes it, with `import nanyx/text` added, as the
  playground opens it. A swap to another page stops what is running.
- **The code is editable where it is.** Clicking the block, or Enter on it, puts the playground's editor in its place
  (`editor.ts`, imported then, so a page that isn't edited doesn't download CodeMirror), without line numbers, with
  the caret where it was clicked; a click that ends a selection leaves the text to copy. Ctrl+Enter runs it. **Reset**
  shows once the code has changed, and puts the example back.
- **"Open in the playground"** opens the code as it is: edited, it builds the share link of the edited code.
- **An example that reads input** (`readsInput`: one that calls `readLine` or `readToEnd`) gets only the link, with
  `data-reads-input`: a tour page isn't cross-origin isolated, since those headers would put every resource the site
  loads under `require-corp`, so a program on it can't read input.

The output's styles, and the editor's colours, are shared with the playground (`playground-shared.css`); the bar's are
in `tour-run.css`. The page test checks that a tour page fetches nothing of the compiler until Run, that an example
runs where it is, that an edited example runs as edited and Reset gives its code back, that "Open in the playground"
opens the edited code, and that an example that reads input gets no Run.

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
[Hosts](./hosts)).

**Building it.** The docs site is built on Vercel, which has no .NET SDK, so it can't build the compiler. Instead the
Compiler workflow's `Browser test` job zips the page it published and tested, with the shipped packages and the
playground host's runner but without the `.br` and `.gz` copies, as `nanyx-browser.zip`, and the `Release` job attaches
it to the release with the binaries ([#2152](https://github.com/nanyx-lang/nanyx/issues/2152)). The docs build
downloads it from the release named by `playgroundRelease` in `docs/site/playground-compiler.mjs`, the one place it is
named, and unpacks it into `public/playground/compiler`, so the site serves the compiler at `/playground/compiler/`;
`compiler.js` fetches its files from beside itself, so it works from there. Moving the playground on to a newer
release is changing that line, after the release is published: a build that names a release without the asset fails.
It names 0.33.0, the first release with the asset. When it is `null`, the build unpacks nothing and the page says its
compiler isn't on the site. To build the site with a compiler you built
yourself, set `NANYX_BROWSER_ZIP` to its zip.

