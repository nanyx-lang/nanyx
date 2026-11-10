# The Nanyx compiler

The compiler, its runtime, the language server and the `nanyx` CLI. This file says how to build and test them.
How they work, and the design of what comes next, is in the
[Work in progress](https://nanyx.dev/docs/work-in-progress/workstreams) section of the docs site, starting at
*Compiler workstreams* (source in [docs/src/content/work-in-progress](../docs/src/content/work-in-progress)).

## Project Structure

```
compiler/
├── Nanyx.Parser/              # Parser library (F#, FParsec)
│   ├── Ast.fs                 # The syntax tree
│   ├── ParseState.fs, …       # Parse state, lexical rules, patterns, types (see the .fsproj)
│   └── Program.fs             # Expressions, statements and the public parse functions
├── Nanyx.Parser.Tests/        # Parser unit and fixture tests
├── Nanyx.Compiler/            # Type checker (F#)
├── Nanyx.Compiler.Tests/      # Compiler-level unit/integration tests
├── Nanyx.Compiler.Wasm/       # WebAssembly text (.wat) backend (F#)
├── Nanyx.Compiler.Wasm.Tests/ # Backend fixture, end-to-end and execution tests
├── Nanyx.Compiler.Runtime/    # Runs emitted modules on an embedded Wasmtime engine (F#)
├── Nanyx.LanguageServer/      # The language server (C#, OmniSharp LSP), run by `nanyx lsp`
├── Nanyx.LanguageServer.Tests/# Drives the server through a real LSP client in-process
├── Nanyx.Compiler.Cli/        # The `nanyx` executable (C#, Consolo)
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
install is needed.

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

## Running programs

```powershell
dotnet run --project compiler/Nanyx.Compiler.Cli/NanyxCompiler.Cli.csproj -- run hello.nyx
```

What each command does is in [The nanyx command](https://nanyx.dev/docs/work-in-progress/command-line).

## Continuous integration and releases

The `Compiler` workflow (`.github/workflows/compiler.yml`) builds and tests the solution on
Linux and Windows for every push to `main` that touches `compiler/` or `stdlib/`, and
publishes a self-contained `nanyx` executable for each supported platform as a workflow artifact.
On a pull request it does only the work the change can affect: everything when the pull request
touches the compiler, the standard library, the examples or any other `.nyx` file; the tests
without the Wasm tests when it touches only the docs' content (a formatter test reads the docs'
code blocks); and nothing otherwise.

To cut a release, bump `<Version>` in `Nanyx.Compiler.Cli/NanyxCompiler.Cli.csproj` and merge.
When the workflow on `main` finds no `compiler/<version>` tag for that version, it creates the tag
and attaches the binaries it just built and tested to a GitHub release; nothing is rebuilt. Pushes
that leave the version unchanged release nothing. (The VS Code extension is released the same way:
bump `version` in `extension/package.json` and merge.)
