---
title: "Hosts"
description: "How the compiler builds hosts, the console host and the HTTP host"
order: 9
---

Choosing a host, the console, HTTP and playground hosts, writing a host package, foreign functions and host memory are
described for users in [Hosts](../advanced/hosts). This page says how the compiler builds hosts, and why they have
the shape they do. The hosts still to come and the open questions are on [Hosts](../work-in-progress/hosts) in Work
in progress.

# How it's built

- **A host declares itself.** A host package's manifest has `host = (root = "...", prelude = "...")` in its `package`
  (`Manifest.checkHostDeclaration` checks it, `HostPackages.ofManifest` reads it into `Manifest.Declares`). `Host`
  (`Hosts.fs`) is what the compiler knows of one: its package, its root module, its prelude and its directory. The
  compiler keeps no table of hosts: a host is a package like any other, found by its version from the package index,
  the version the compiler ships among them, or by its path, and read from its manifest
  ([Shipped packages](./shipped-packages#how-shipped-packages-are-built)). `HostPackages.console` is the console host, read
  from the compiler's own copy, which a loose script runs under unless it names another. The host is imported as
  `nanyx.cli.host/cli.host` (`Host.Import`), and its prelude as `nanyx.cli.host/cli.prelude` (`Host.PreludeImport`).
- **A package chooses its host.** `Manifest.check` reads the manifest's `host`, `(package = "nanyx.cli.host", version
  = "0.28.0")` or `(package = "acme.host", path = "../acme.host")`, into `Manifest.Host` (a `HostEntry`; none without a
  `host` field: a library, whose `@entry` `Attributes.applyAttributes` reports as `NYX0031`). A host the compiler
  doesn't ship, named without a path, is `NYX0023`, saying how to give its path; a host with both a path and a version
  is an error. Its `settings` are a record, checked against the settings the host declares, with their defaults, in its
  own manifest (`Host.Settings`, `HostSettings.errors`) once the host is read. `Package.ofManifest` reads the host's
  own manifest (`Package.hostIn`): its name must match, and it must declare itself a host. The host's package travels
  with the package's module table (`PackageModules.Host` and `PackageModules.HostPackage`), so every compile of a
  package's file, the language server's included, passes `@entry` to that host's `run`, and the host's imports resolve
  in its own package. `--host` on `run`, `build` and `check` replaces it with a host the compiler ships or the
  directory of one (`Package.withHost`); on a script it is a usage error.
- **A script chooses its host.** `Script.read` reads a script's `::host` and `::install` directives into the
  entries a manifest's `host` and `dependencies` would be (`Manifest.hostEntry`, `Manifest.dependencyEntries`), so they
  are checked as a manifest's are, with the same errors, at the word each is about. `Package.scriptModules` reads the packages
  they name, the console host when there is no `::host`, into a module table for the script's build
  (`PackageModules.Script`): it has no files of its own, so every file of the build in no package it depends on is the
  program's (`PackageModules.isProgramFile`), and its imports are found by path, but for an import of a dependency's
  public module, `acme.util/strings`. `Script.compileSourceReading` compiles a script in that build, for `nanyx run`
  and the language server alike. A script with neither directive compiles in no build.
- **Which modules see the host.** A module of the program itself runs under the build's host: it sees the host's
  prelude and may declare the `@entry`. A module of a package the program depends on, of the host's own package, or
  of the standard library has no host (`checkModule` in `Library.fs`).
- **The host's prelude.** A module with a host imports its prelude after the standard library's, as an implicit import
  that the module's own imports win over. The contexts the prelude re-exports are those the host provides
  (`providesContexts` in `ImportMerging.fs`): importing the module that declares one, as the prelude does, binds the names
  of its members, so they are ambient (see below). An import that selects a context, as the prelude's
  `export nanyx.cli/console (Console, ...)` does, selects its members too.
- **`@entry` calls the host's `run`.** `Attributes.applyAttributes` imports the host under an alias of its own
  (`__host`), so that it can't clash with the program's names, and appends `__host.run(main)` to the module as its
  start. The entry point passed to `run` is at the attribute's span, so a mismatch there is the entry not fitting the
  host: `NYX0237`, reported at `@entry`, with the type the host expects and the one the entry has.
- **`run` ignores what the entry returns.** Its parameter is `[Console] () -> a`, not `() -> ()`, so that an entry
  whose block ends in an expression (`dbg(x)`) fits, and its value is dropped.
- **`HostRuntime`** is declared in `nanyx/host.runtime`, a context with no members: the capability that lets code
  call [foreign functions](./foreign-functions) and use `nanyx/host.memory`. With no members, a function that requires
  it is passed nothing, and the type checker treats it as available wherever it is required, so `Hosts.runtimeErrors`
  reports `NYX0238` in any module but one of the build host's own package or of a host library it depends on (`isHost`
  in `Library.checkModule`, `PackageModules.HostLibrary`), `nanyx/host.runtime` and `nanyx/host.memory`, by what each
  call calls: at
  each top-level def that requires it, as its written contexts say or a call in it does, other than a foreign function,
  which any module may declare, and at each call at the top level that requires it, other than the call of `run` that
  `@entry` makes. A module that calls a host's `run` itself needs `HostRuntime` for it, so that is an error too.
  A host checked or packed on its own is the build's host for this (`PackageModules.DeclaresHost`), and a host library
  checked or packed on its own may require it too, so `nanyx check` and `nanyx pack` accept either as a build under
  the host does.
- **Where the entry's `Console` comes from.** Every entry is passed the members of the `Console` that `run` `use`s, so
  it reads and writes through the host. An entry that doesn't write its type, which is most programs, infers `Console`
  as an ambient context, because the host's prelude brings `Console`'s names in. A def that infers an ambient context takes its
  members as a def that writes it does: `InferModule` adds the contexts it inferred to the def's annotation for the
  backend (`[Console]` on its own, or after the parameterised contexts it was given), and the backend gives a function
  named as a value the contexts without parameters it requires (`CodeGen.carryPlainContexts`), so `run(main)` passes
  `main` its members rather than closing it over the module that declares `Console`. Nothing wires `Console` to the entry by name.
- **Scripts run under the host.** A module without an `@entry` whose top level has statements is a script, and its
  statements are its entry point: `Scripts.asEntry` makes them the body of a lambda, followed by `()`, and appends
  `__host.run({ ... })` to the module, importing the host (a script sees the host's prelude, as any program's module
  does).
  The lambda is not given `Console`'s members as a lambda passed to `run` would be
  (`Contexts.extendEnvForLambdaArgument`): its statements are the top level, and see the script's own names and
  ambient contexts as top-level code does. A script's value that needs a context, directly or through the functions and values it
  names (`def line = readLine()`), is inferred in a scope of its own (`ScriptValueScope`) that records what it
  needs. A lambda written in the value is not run by building it, so what it calls is its own type's, not the value's
  (`def handlers = (log = { m -> println(m) })` needs nothing). Once the module is inferred, those values (`InferState.ScriptEntryValues`) move into the entry point as local
  defs, in the order written, so each is computed once
  ([#1257](https://github.com/nanyx-lang/nanyx/issues/1257)). A value that needs no context stays at the top level,
  where a function can name it, and is built on its first read
  ([Memory management](./memory-management#core-ir)). A function that names a value that moved, or a statement that reads one before its def,
  is `NYX0239`. The ambient contexts the statements and the moved values need through unannotated functions are
  only known once the module is inferred, so the lambda's type is given them then (`InferModule.withEntryContexts`),
  and the host passes their members. Once typed, the entry point is a def of its own, `def __script: [Console] = { ... }`,
  which `__host.run(__script)` names (`Scripts.asDefinition`), so a lambda in a statement captures its members as a
  lambda in any def does. Statements beside an `@entry` are a script entry too, run before the `@entry`'s. A value that needs a context in a module with no statements, which nothing would run, is an error
  at the value. A script carries only the members of the host's contexts that it calls, as an entry program does
  (see `Env` below).
- **`Console` only comes from the host.** `nanyx.cli/console` declares the context and `print`, `println`, `eprint` and
  `eprintln`, but no instance: the line reading lives in the console host alone. Importing a module whose contexts
  the host provides (those its prelude re-exports) binds the names of its plain contexts' members, `readLine` and the
  rest, each a symbol of its own (`ImportMerging.mergeImport`), so `Console` is still an ambient context: a script's
  statements and an unannotated def can call `readLine()`, and the def infers `Console`. A member of a context a def
  declares, or a `use` loads, shadows an imported function of the same name, and takes none of its contexts
  (`Contexts.contextRequirementsOf`): a def's own `println` member is not the prelude's `println`. A function named as a value carries it in its
  type (#1288), and an unannotated one carries those it was inferred to require, its own and those of the functions it
  calls (`Contexts.inferredAmbientContexts`), found once for each function it reaches however many paths reach it
  ([#1458](https://github.com/nanyx-lang/nanyx/issues/1458)). A def that writes `[c.Console]` for an aliased import is passed the members as `[Console]` is: the
  backend reads the layout of the context's own name (`CodeGen.layoutOf`), and a member called under the alias,
  `c.write`, is written by its own name for the backend (`ModuleLinking.writeMemberAliases`).
- **A context the host doesn't provide.** An imported function may require a context the module can't name, as a
  library's `[Console]` function does under a host whose prelude doesn't bring `Console` in. Where the imported module
  has it as an ambient context, it is carried in (`InferState.CarriedAmbientContexts`). Under a host, one that isn't
  carried in can only come from the host, which doesn't provide it, so `ImportMerging.mergeImport` records it in
  `InferState.UnprovidedContexts`, and a call to a function that requires it is `NYX0205`, naming the contexts and the
  host (`Contexts.ensureContextAvailable`, [#1811](https://github.com/nanyx-lang/nanyx/issues/1811)). `HostRuntime`,
  which only a host may require, is left out: it has an error of its own, `NYX0238`.
- **`Env`** is declared in `nanyx.cli/env` (`args`, `variable`, `exit`), and `run` `use`s it beside `Console`, with
  `FileSystem`, `Http` (see below), `Random` and `Clock`, so its parameter is `[Async, Clock, Console, Env, FileSystem, Http, Random] () -> a`; an entry that requires fewer
  contexts fits. The host's prelude re-exports
  `Env`, so its members are ambient names in every module of the program. Its members are functions of the host's own
  over WASI's `args_sizes_get` and `args_get`, `environ_sizes_get` and `environ_get`, and `proc_exit`, declared with
  `@extern`, with their buffers allocated with `nanyx/host.memory` (see
  [Foreign functions](./foreign-functions#whats-built)). `run` passes the members of both contexts to every entry,
  but the inliner inlines `run` and the entry into the start ([#1469](https://github.com/nanyx-lang/nanyx/issues/1469)),
  the entry whatever its size, since the module calls it once ([#1883](https://github.com/nanyx-lang/nanyx/issues/1883)),
  so a member nothing calls is never built, and a program contains only the members it calls and imports only what
  they call ([#1497](https://github.com/nanyx-lang/nanyx/issues/1497)): hello world imports `fd_write` alone. A
  program that reads `args()` imports no `environ_*` function, and one that reads only `variable` no `args_*` one
  ([#1766](https://github.com/nanyx-lang/nanyx/issues/1766)): `entriesOf`, which reads either list, is passed the
  list's two WASI functions, each wrapped in a lambda the inliner removes, rather than a tag it matches on, which
  built both lists' code into every caller. `nanyx run` passes what follows the
  file or package as the arguments, after the program's name, and the process's environment variables (see
  [`nanyx run`](../standard-tools/command-line#nanyx-run)); an `exit(0)` part-way is a clean end, which `--check-memory` doesn't
  count as a leak. A script's entry point requires only the plain contexts that are ambient in its module
  (`Scripts.withAmbientContextsOnly`): a statement that calls a function whose type is an error would otherwise leave it
  requiring all that `run` provides, `Env` too in a script that doesn't import it.
- **`FileSystem`** is declared in the shipped package `nanyx.fs`, which the console host depends on; its prelude
  re-exports `FileSystem`, with `Path` and `FsError`, the types its members name: a program module sees `run`'s
  parameter type, so it must be able to name the members' types. A program reaches its functions by importing
  `nanyx.fs`'s modules, with `::install nanyx.fs` or a manifest's `dependencies`. `FileSystem`'s members are named
  for what they do on their own (`readFile`, `writeFile`, `pathExists`, `listDirectory`, ...), since the prelude makes
  them ambient names. `listDirectory` gives names in any order, and `dir.list` sorts and joins them, so another host
  has less to do.
- **The file system on WASI.** The console host's `FileSystem` is written in the host library `nanyx.wasi`, in `preview1.system.files.nyx`, a `@partial` fragment of its module `preview1.system` (a package's modules are assembled from their fragments as a program's are, `PartialModules.otherFragments` searching the host's packages too), which `cli.host`'s `provide` loads with `Console` and `Env`, on WASI's
  `fd_prestat_get` and `fd_prestat_dir_name`, `path_open`, `fd_read`, `fd_pread`, `fd_write`, `fd_close`,
  `fd_filestat_get`, `fd_readdir`, `path_filestat_get`, `path_unlink_file` and `path_create_directory`. A path is
  found in the runtime's preopened directories: a relative one in the preopen named `.`, an absolute one in the
  preopen whose name starts it (compared without case when it starts with a drive), and one in none is
  `#permissionDenied`, as is a relative path that climbs out of `.`, which WASI refuses (`ENOTCAPABLE`). WASI's error
  numbers map to `FsError`. `WasmRunner` (`Nanyx.Compiler.Runtime/Runner.fs`) takes the directories a program may
  reach, the current directory first: it preopens that one as `.` and by its absolute path, and each other by its
  absolute path, written with `/`. `nanyx run` passes the shell's directory and the one `--allow-dir` names
  (`Program.RunIn`). `file.lines` reads 64 KB at a time through a member of its own, `readFileChunk(at, offset,
  length)`, which opens the file, reads the chunk with `fd_pread` and closes it again, so a sequence abandoned before
  its end holds no file, and pulling a sequence again reads the same lines again. The host's `readFileChunk` gives an
  error, not an empty chunk, for an offset past the file's end, so a file cut short between chunks is caught.
- **`Async`** marks a function that can wait. It is declared in the standard library's `nanyx/async` as
  `protected ()`, so no `use` outside that module can make one, and it has no members. Both hosts provide it: each
  prelude re-exports it, and each `run` requires it in its own type, `[HostRuntime, Async]`, and passes it to the
  entry, whose parameter type requires it. A context with no members that a host's prelude exports is ambient by its
  name, as one whose members the prelude stands in for is by theirs (`Contexts.isAmbientMemberless`): the program's top
  level has it, and supplies it to the start's call of `run` as the runtime supplies `HostRuntime`; an unannotated def
  that calls something requiring it infers it; and an annotated def that doesn't declare it is `NYX0211`. A memberless
  context has nothing to pass at run time, so `Async` costs nothing in the generated code. `nanyx.http`'s
  `sendRequest`, `Clock`'s `sleep` and `nanyx.fs`'s members, and the functions written on them, require it. A request
  and a sleep stop the task that waits while the program's other tasks run; a file's functions are blocking host
  calls, which hold the instance's thread. Each instance has a thread of its own: the console host has one instance,
  and the HTTP host runs each request's instance on a thread of its own (`WasmServer.serve`), so a handler that waits
  on an outbound request holds only its own (see [Async](./async)).
- **`Http`** is declared in the shipped package `nanyx.http`, which the console host depends on; its prelude
  re-exports `Http` and `HttpError`, with `Request`, `Method` and `Response`. Its one member, `sendRequest`, is written
  on the runtime's own import `nanyx:http`, not on WASI, which has no networking (see
  [Foreign functions](./foreign-functions#whats-built)). `nanyx.http`'s module `wire` writes and reads requests
  and responses as the runtime's imports pass them, so the console host and the HTTP host share it.
- **`Random`** is declared in the standard library's `nanyx/random`, and both hosts' preludes re-export it. Each host
  writes its one member, `bits`, on WASI's `random_get`, which fills 8 bytes that `readI64` then reads: both call
  `systemBits` in the host library `nanyx.wasi` (`nanyx.wasi/preview1`), which their packages depend on.
  `random.seeded` is the library's own `Random`, SplitMix64 over a `mut` cell, so a test host, when there is one, can
  give its tests a fixed seed by running each under it.
- **`Clock`** is declared in the standard library's `nanyx/clock`, with `now`, an `Instant`, `ticks`, a `Time(int)`
  since a start the host chooses that never goes backwards, and `sleep`, which takes a `Time(int)` and requires
  `Async`; both hosts' preludes re-export it. Each host writes `now` and `ticks` on WASI's `clock_time_get`, `now` on the realtime clock (0)
  and `ticks` on the monotonic one (1), each giving nanoseconds in 8 bytes of the host's state that `readI64` reads,
  which `nanyx.wasi/preview1`'s `timeOn` makes a `Time(int)` and `now` adds to `Instant.epoch`. `sleep` is `nanyx.wasi/runtime`'s `sleepOnRuntime`, which waits on the runtime's
  `nanyx:async` for what is left of it, in whole milliseconds rounded up (`nanyx/clock`'s `wholeMilliseconds`), until
  the monotonic clock has passed its deadline, so the program's other tasks
  run meanwhile. A sleep is a member, not a function of `nanyx/clock`, so that a test's `use Clock(...)` decides how
  long it takes, as it decides the time.
- **The HTTP host.** `packages/nanyx.http.host` declares itself a host with `host = (root = "http.host", prelude =
  "http.prelude", settings = (port = 3000))`. Its `run` takes the handler, `use`s an `Http` written on `nanyx:http` as
  the console host's is, takes the request from the runtime, calls the handler, and gives the runtime the response; a
  request the runtime wrote wrongly is answered with a 400 without calling the handler. The host imports the
  runtime's own `nanyx:serve`, and `nanyx run` serves any program whose module imports it (`WasmServer.serves`),
  whatever its host: it compiles the module once, listens on `http://localhost:<port>/` with .NET's `HttpListener`,
  and answers each request in a store and an instance of its own, on a thread of its own. An instance that traps or
  exits without responding is answered with a 500, the reason goes to standard error, and the server carries on.
  `Request.path` takes the path from the whole URL the runtime passes. Its component root, `http.host.wasi`, which
  `nanyx build` builds a `wasi:http/service` component from, is on [Components](./components#the-http-hosts-component-root).
- **The playground host.** `packages/nanyx.playground.host` declares itself a host with `host = (root = "playground.host",
  prelude = "playground.prelude")`, and depends only on `nanyx.cli`. Its `run` takes `[Async, Clock, Console, Random] () -> a`
  and `use`s a `Console`, a `Random` and a `Clock` written on imports of its own, `nanyx:playground` `write`,
  `writeError` (each given the address and byte length of the text in the module's memory), `randomBits` (64 bits, as
  an `i64`), `now` and `ticks` (milliseconds, as `i64`s), and `readLine` and `takeLine`, which a page implements, not
  on WASI. It provides `Async`, and its prelude exports it. Its `Clock`'s `sleep` waits on the host, as the core
  roots' does (see [Async](./async#tasks-and-scopes)), for what is left of it: a page has nothing to wait on but the
  time, so the host's wait reads `ticks` until the timeout has passed, while every task is waiting. Its `Console`'s `readLine` calls the page's `readLine`, which waits for a line and gives its length in
  bytes, or -1 when the input has ended (`#endOfInput`), then allocates a buffer that long and has `takeLine`
  write the line into it; its `readToEnd` reads lines until the input ends, each followed by a line break
  ([#2151](https://github.com/nanyx-lang/nanyx/issues/2151)). Its prelude re-exports `Console`
  with `print`, `println`, `eprint` and `eprintln`, `Random` and `Clock`, so a console program that needs only those compiles
  under it unchanged, and one that needs a context it doesn't provide is `NYX0205` naming it, by the check above
  ([#2150](https://github.com/nanyx-lang/nanyx/issues/2150)). The runtime's own helpers still import WASI's `fd_write`
  and `proc_exit`, for a deferred error or crash, so whatever runs the module provides those two as well.
- **The test host.** `packages/nanyx.test.host` declares itself a host with `host = (root = "test.host", prelude =
  "test.prelude")`, and depends on `nanyx.cli`, `nanyx.fs` and `nanyx.wasi`. Its `run` takes
  `[Async, Console, Env, FileSystem, Random] int -> a`, a test build's entry point (see
  [Test builds](./test-builds#running-one)), asks the runtime's `nanyx:test` `index` which test to run, and calls the
  entry point with it under `random.seeded` with `nanyx:test`'s `seed`. For a test that isn't `#io` (`nanyx:test`'s
  `io` gives 0), it `use`s a `Console` whose `write` and `writeError` give the text to `nanyx:test`'s, each with its
  address and byte length, and whose `readLine` gives `#endOfInput`, and an `Env` and a `FileSystem` whose members
  crash, which `NYX0312` keeps any test from reaching. For a `#io` test, it calls `nanyx.wasi`'s
  `preview1.system.provide`, which loads the console host's real `Console`, `Env` and `FileSystem`. Its prelude
  re-exports `Console`, with `print`, `println`, `eprint` and `eprintln`, `Random` and `Async`. The contexts it provides
  are `Hosts.testContexts`, `Async`, `Console` and `Random`, and `Hosts.ioTestContexts` for a `#io` test, which the compiler checks a test against
  (`NYX0312`).
- **The playground host's runner.** `packages/nanyx.playground.host/runner` is the JavaScript that runs a module compiled for
  the playground host; it isn't a source of the package (its `sources` are `src/**/*.nyx`), and the compiler built for
  the browser publishes it with the page. `playground.js`'s `run(wasm, { seed, onStdout, onStderr, onDbg, onRead })`
  starts `worker.js` in a web worker and gives `{ done, stop, input, closeInput }`: `stop` terminates the worker, and
  `done` resolves to `{ code }`, with `trap` when the program trapped, or `{ code: null, stopped: true }`. The worker
  implements `nanyx:playground` (`write` and `writeError` post the text, decoded from the module's memory; `readLine`
  waits for the page's next line, below, and `takeLine` copies it into the module's memory; `randomBits` is
  SplitMix64 over the page's seed, so a seed gives what `random.seeded` gives with it; `now` is `Date.now()` and
  `ticks` the milliseconds since the run started, from `performance.now()`), `nanyx:debug` `dbg` (posts the file,
  line, column and text, and says it has dealt with it), and WASI's `fd_write` (fd 1 is stdout, any other stderr) and
  `proc_exit`, which throws, so the program unwinds to the call of its start, which gives the exit code. A trap is
  exit code 1. The module is compiled with its start exported (`WasmOptions.ExportStart`) and `dbg` given to the page
  (`WasmOptions.DbgToEmbedder`), so the worker instantiates it, then calls `__nanyx_start` once the memory can be read;
  a program with nothing to run at its top level, only definitions, has no start, and ends at once with 0. The
  playground page uses it as [Playground](./playground) says.
- **Input in the playground.** A program's `readLine` blocks, which a page's main thread can't, so the worker waits
  for the line: the page gives it a `SharedArrayBuffer`, three `Int32`s (answered, the line's length or -1 for the end
  of the input, and whether the line goes on) and then 64 KiB for the line's bytes. To read, the worker posts `read`
  and blocks in `Atomics.wait` until the page answers; a longer line comes in parts. The page answers from the lines
  given to `input(text)`, in order; when it has none it calls `onRead()`, where a page shows its input line, and
  answers when a line arrives, or with the end of the input after `closeInput()`. A page has a `SharedArrayBuffer`
  only when it is cross-origin isolated, served with `Cross-Origin-Opener-Policy: same-origin` and
  `Cross-Origin-Embedder-Policy: require-corp`, as `docs/vercel.json` serves the playground's route and `serve.mjs`
  serves the compiler's page; `canReadInput` says whether it is, and when it isn't, the worker gets no buffer and a
  program's input has ended before it starts.
- **The browser's default host.** `Script.compileUnder` compiles a script that runs under a shipped host of the
  caller's choosing unless its `::host` names another (`Script.readFromUnder`, and `Package.scriptModules`, which take
  the default host); `nanyx run` and the language server pass the console host. `BrowserCompiler.Compile`
  (`compiler/Nanyx.Browser`) passes the playground host (`Hosts.playgroundPackage`), compiling the page's program as
  the script `/playground/main.nyx` in the runtime's in-memory file system, with a store that finds only the packages
  the compiler ships. The build writes `package-files.txt`, the shipped packages' files the page hands the compiler
  (`wwwroot/compiler.js`). `wwwroot/run.html` compiles and runs a program, and `playground-test.mjs` drives it in
  headless Chromium with Playwright: the tour's hello world, `Random` from a seed, `Clock`, a crash, `dbg`, a program
  that needs `FileSystem`, two lines typed into the page's input line and then Ctrl+D, `readToEnd` and a line longer
  than the buffer, and Stop; the Compiler workflow's `Browser test` job runs it.
- **Where `dbg` prints.** `dbg` needs no context, and a host package does nothing for it: the runtime that runs the
  host's program decides. A module `nanyx run` runs gives each `dbg` to `nanyx:debug` `dbg` first, with its file, line,
  column and text, and the handler says whether it has dealt with it (`Probing.defineDbg`). The console runner
  (`WasmRunner`) leaves it to the program, which writes `[file:line] text` to its stderr through WASI `fd_write`, in
  order with what else it writes there; the HTTP server (`WasmServer`) writes it to its log, `dbg (GET /path): ...`,
  and never into the response. A module written by `nanyx build` or `nanyx wasm` has no such import and writes it to
  stderr itself, so another runtime can run it ([Debug probes](./debug-probes#dbg),
  [#2058](https://github.com/nanyx-lang/nanyx/issues/2058)).
- **Host settings.** A host declares the settings it takes, with their defaults, in its own manifest's `host`; a
  program's manifest sets them in its `host`'s `settings`, and one the host doesn't take, or of another kind than its
  default, is an error at the setting (`HostSettings`, checked when `Package` reads the host). A script's `::host` has
  no settings, so it gets the defaults.

# Why it's built this way

A command-line tool, an HTTP service, a browser app and a test run are the same program model under different hosts.
A host answers three questions for a program:

1. What shape must the entry function have?
2. Which contexts are available to it?
3. Who owns the process: the main loop, concurrency, signals, shutdown and the exit code?

Different kinds of program need different answers. An HTTP service wants its entry to be a `Request -> Response`
handler and shouldn't have to write its own accept loop. A sandboxed plugin should get *fewer* capabilities than a
CLI tool. A test run wants fake or recording implementations of the same contexts the real program uses. Hosts make
these answers explicit and swappable, and let the compiler check them.

- **A host is a function that takes the entry and runs it**, under `use`s of the contexts it provides. Checking an
  entry against a host is checking it against `run`'s parameter type, and a library that requires a context the host
  doesn't provide fails the same check. Nothing about hosts needs a check of its own.
- **One entry shape per host.** The console host's is `() -> ()`. A program's arguments, environment and exit code
  come from the `Env` context, not from a second shape. A handler may require fewer contexts than its host provides:
  a function type with fewer contexts fits where one with more is expected.
- **Capability security comes with it.** Libraries never assume a host. They declare what they need, such as
  `[FileSystem]`, and a host that doesn't provide `FileSystem` guarantees that the program, and every library it uses,
  can't touch the file system.
- **Hosts are ordinary packages** that the compiler ships but doesn't treat specially, so anyone can write one.
- **The HTTP handler is synchronous, and the host provides concurrency.** A server can block, so the host runs many
  calls of the handler at once, one instance per request, as `wasmtime serve`, Spin and Fastly run components of the
  `wasi:http` world. An outbound call waits in its request's instance only. A trap ends only that request.

## Hosts are not frameworks

A host owns the *process lifecycle*. A framework is something that needs effects or produces an entry:

- The **HTTP host** listens on a socket, runs the request lifecycle, manages concurrency and shuts down. It expects a
  `Request -> Response`.
- A **web framework** provides routing, middleware and templating. It is a library that *produces* a
  `Request -> Response`.

The test is: **if it needs to own the main loop, it's a host; if it only needs effects, it's a library that requires
contexts.** That way any framework runs on any HTTP host, and swapping hosts (local server, serverless, WASI) doesn't
mean rewriting the application. Python's WSGI/ASGI and Ruby's Rack show how this split works out, and what happens
without it.

## Interface packages

A library that only makes sense with a certain kind of host, such as middleware for the HTTP host, depends on what a
host provides, its contexts and types, never on the host itself. So each host is two packages:

- **An interface package** that declares the types and contexts: `nanyx.http` declares `Request`, `Response`
  and `Http`; `nanyx.cli` declares `Console` and `Env`. It has no code beyond those declarations and the
  functions on them.
- **The host** that implements it, its `run` built on [foreign functions](./foreign-functions). It depends on its
  interface package.

Middleware is a function from handler to handler, `(Request -> Response) -> (Request -> Response)`: it needs the
types, not the host that serves them. A library depends only on the interface package, and the capability check does
the rest. The same interface can have more than one host, so a third-party HTTP host is another implementation of the
same interface, with every library already written for it. This is the split WASI makes between an interface (WIT)
and its implementation, and that WSGI makes between a server and a framework.

## Prior art

- **Roc**: platforms and applications. This is the closest existing design. The platform owns `main` and all effects;
  the application is pure. Roc's platforms are written in a systems language, and making effects available to
  libraries in a platform-agnostic way was a long-running problem. Nanyx's contexts address that directly.
- **Elm**: `Browser.sandbox`, `Browser.element` and `Browser.application` are hosts that each require a different
  entry shape.
- **WASI**: worlds describe the imports and exports an embedder provides, which corresponds to a host's contexts and
  entry shape.
- **WSGI / ASGI / Rack / OWIN**: the split between server and framework.
- **.NET Generic Host**: host configuration, lifetime and composition of hosted services.
