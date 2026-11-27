# The Nanyx compiler

The compiler, its runtime, the language server and the `nanyx` CLI. This file says how to build and test them.
How they work is in the [Deep dives](https://nanyx.dev/docs/deep-dives/compilation) section of the docs
site (source in [docs/src/content/deep-dives](../docs/src/content/deep-dives)), and the design of what
comes next in the [Work in progress](https://nanyx.dev/docs/work-in-progress/workstreams) section, starting at
*Compiler workstreams* (source in [docs/src/content/work-in-progress](../docs/src/content/work-in-progress)).

## Project Structure

```
compiler/
├── Nanyx.Parser/              # Parser library (F#)
│   ├── Ast.fs                 # The syntax tree
│   ├── ParseState.fs, …       # Parse state, lexical rules, patterns, types (see the .fsproj)
│   ├── Expressions.fs         # Expressions: blocks, match, operators, pipes, lambdas, calls
│   ├── TopLevel.fs            # Statements and a module's top-level items
│   └── Api.fs                 # The public parse functions
├── Nanyx.Parser.Tests/        # Parser unit and fixture tests
├── Nanyx.Parser.Corpus/       # Every piece of Nanyx in the repository, for the corpus and differential tests
├── Nanyx.Compiler/            # Type checker (F#)
├── Nanyx.Compiler.Tests/      # Compiler-level unit/integration tests
├── Nanyx.Compiler.CheckedLibrary/ # Checks the standard library at build time (CheckedLibrary.targets copies it)
├── Nanyx.Compiler.Wasm/       # WebAssembly text (.wat) backend (F#)
├── Nanyx.Compiler.Wasm.Tests/ # Backend fixture, end-to-end and execution tests
├── Nanyx.Compiler.Runtime/    # Runs emitted modules on an embedded Wasmtime engine (F#)
├── Nanyx.Componentizer/       # wit-component as a WASI command (Rust), which the runtime embeds
├── Nanyx.LanguageServer/      # The language server (C#, OmniSharp LSP), run by `nanyx lsp`
├── Nanyx.LanguageServer.Tests/# Drives the server through a real LSP client in-process
├── Nanyx.Compiler.Cli/        # The `nanyx` executable (C#, Consolo)
├── Nanyx.Browser/             # The compiler built for the browser, for the playground, and its headless test
├── Nyx.Compiler.slnx          # Solution covering all projects
└── README.md

stdlib/nanyx/                  # The standard library (Nanyx source, embedded into the compiler)
```

## Prerequisites

To **run** a published `nanyx` binary (from a GitHub release or a `Compiler` workflow artifact)
nothing needs to be installed: it is self-contained, and the Wasmtime engine that executes
programs is embedded in it. Supported platforms are `win-x64`, `linux-x64`, `linux-arm64`,
`osx-x64` and `osx-arm64`. On Linux and macOS, `chmod +x nanyx` after unpacking.

To **build** the compiler from source you need the
[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0); `dotnet --version` should print
`10.x`. The build restores everything else from NuGet, including the `Wasmtime` package that ships
the native engine for every platform above, so no separate Wasmtime, wat2wasm or wasm runtime
install is needed. Rust is not needed either: the runtime embeds the componentizer, wit-component compiled to
WebAssembly from the crate in `Nanyx.Componentizer/`, from the `.wasm` committed beside it. Only a change to that crate
needs [Rust](https://rustup.rs) with the `wasm32-wasip1` target, to rebuild it with
`dotnet build -p:BuildComponentizer=true` and then run `.github/scripts/componentizer-sources.sh --write`.

## Building

```powershell
cd compiler
dotnet build Nyx.Compiler.slnx
```

## Running Tests

```powershell
cd compiler
dotnet test Nyx.Compiler.slnx
```

A few tests compare how long things take, which a busy machine can make fail on noise. They are in the `Timing`
category, which the Compiler workflow leaves out: the **Timing** workflow (`.github/workflows/timing.yml`) runs them
every night on main, keeps their times in its summary and as an artifact, and opens an issue labelled `ci` when a night
fails. To run them on a branch, use `gh workflow run Timing --ref <branch>`. A local run can leave them out too:

```powershell
dotnet test Nyx.Compiler.slnx --filter "Category!=Timing"
```

and run them on their own, on a quiet machine, with `--filter "Category=Timing"`. A test times what it runs with
`TestTiming.millisecondsOf` (`TestTiming.fs`, which each test project that times anything compiles), which fails unless
the test is marked `[<Trait("Category", "Timing")>]`. Where a test can compare an amount of work instead of time, it
does: a WebAssembly program's fuel (`WasmProgram.Work`), which counts the instructions a call runs, the type
checker's full solves (`Solving.countingFullSolves`), the tokens the parser reads (`ParseState.TokensRead`), or, for a
function with no count of its own, the bytes it allocates on its thread (`TestSupport.allocatesLinearly`). Those are in
no category.

The playground host's runner is tested in headless Chromium, with Playwright (`npm install playwright` and
`npx playwright install chromium` where they aren't installed):

```powershell
cd compiler
dotnet publish Nanyx.Browser/Nanyx.Browser.csproj -c Release
node Nanyx.Browser/playground-test.mjs
```

## Running programs

```powershell
dotnet run --project compiler/Nanyx.Compiler.Cli/NanyxCompiler.Cli.csproj -- run hello.nyx
```

What each command does is in [The nanyx command](https://nanyx.dev/docs/standard-tools/command-line).

## Continuous integration and releases

The `Compiler` workflow (`.github/workflows/compiler.yml`) builds and tests the solution on
Linux and Windows for every push to `main` that touches `compiler/` or `stdlib/`, and
publishes a self-contained `nanyx` executable for each supported platform as a workflow artifact. A run of a
branch publishes them only when the branch's version has no `compiler/<version>` tag yet, as a release PR's has not.
On a pull request it does only the work the change can affect: everything when the pull request
touches the compiler, the standard library, the examples or any other `.nyx` file; the tests
without the Wasm tests when it touches only the docs' content (a formatter test reads the docs'
code blocks); and nothing otherwise. The Wasm tests run in 3 shards per platform, which
`.github/scripts/wasm-shards.py` lists by the test project's areas. A test in no shard, or in two, fails each shard's job:
a new directory under `Features/` goes into one of the shards there.

To cut a release, bump `<Version>` in `Directory.Build.props` and merge. The packages the compiler ships
are named at that version, and a manifest names them at it exactly, so update the `version` of the
`linters` and `host` entries in `examples/greeter/greeter.nyxp`, and the `version` of each shipped package in
`packages/` (`packages/nanyx.cli.host/nanyx.cli.host.nyxp`), too: the tests fail until they match.
Add the version's section to `docs/src/content/overview/changelog.md` in the same PR (AGENTS.md says how): it
is the release's notes, and the workflow fails without it.
When the workflow on `main` finds no `compiler/<version>` tag for that version, it creates the tag
and attaches the binaries it just built and tested to a GitHub release; nothing is rebuilt. Pushes
that leave the version unchanged release nothing. (The VS Code extension is released the same way:
bump `version` in `extension/package.json` and merge.)
