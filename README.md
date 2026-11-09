# Nanyx

Nanyx is a statically-typed, functional-first programming language with Hindley-Milner type inference, structural records and tag unions, context-based effects, and a pipeline operator that keeps data flow reading left to right. It compiles to WebAssembly.

The full documentation lives on the [docs site][docs]. This README gives a quick taste and then shows how to get going, whether you want to [write Nanyx programs](#writing-nanyx-programs) or [work on the language itself](#working-on-the-language).

```nanyx
module main

type Shape =
  | #circle(radius: float)
  | #rectangle(width: float, height: float)

def area: Shape -> float = {
  | #circle(radius) -> 3.14159 * radius * radius
  | #rectangle(width, height) -> width * height
}

@entry
def main = {
  [#circle(radius = 1.0), #rectangle(width = 2.0, height = 3.0)]
    \map(area)
    \sum
    \println
}
```

## A taste of the language

Everything is an expression, every binding is immutable, and braces always mean a function. The `\` pipe sends the value on the left into the function on the right, so a chain reads in the order it runs:

```nanyx
def adultNames = users
  \filter { .age >= 18 }
  \map { .name }
  \sortBy { .length }
```

Tags such as `#notFound` are values and types at once. They combine into unions, carry payloads, and are matched exhaustively, so there is no null and no exceptions for ordinary failures:

```nanyx
match findUser(id)
  | #some(user) -> println("Found {user.name}")
  | #notFound -> println("No such user")
  | #error(e) -> println("Database error: {e}")
```

Effects and typeclass-style constraints share one mechanism, contexts, which are implicit parameters declared in a function's type:

```nanyx
context Console = (println: string -> ())

def greet: [Console] string -> () = { name ->
  println("Hello, {name}!")
}
```

That is enough to recognise Nanyx code. The docs cover the rest, including [records][records], [pattern matching][patterns], [attached definitions][attached] in place of classes, [scoped mutation][memory] and [workflows][workflows]. If you are coming from another language, start with the [principles][principles] and the "coming from other languages" pages on [classes][classes], [overloading][overloading] and [currying][currying].

## Writing Nanyx programs

Nanyx is young: the compiler runs a small subset of the language the docs describe, and there is no standard library yet beyond a built-in `println`. The docs' [Hello, World!][hello] works today; `compiler/README.md` lists what else does.

### 1. Get the `nanyx` tool

`nanyx` compiles a `.nyx` file to WebAssembly and runs it on an embedded [Wasmtime](https://wasmtime.dev) engine, so it is the only thing you need to install.

On Windows, install it with [Scoop](https://scoop.sh):

```powershell
scoop bucket add nanyx https://github.com/nanyx-lang/scoop-nanyx
scoop install nanyx
```

`scoop update nanyx` picks up new compiler releases.

Otherwise, download the release for your platform from the [releases page](https://github.com/nanyx-lang/nanyx/releases): Windows (x64), Linux (x64, arm64) or macOS (x64, Apple Silicon). The executable is self-contained and needs no .NET runtime. Unzip it, on Linux and macOS run `chmod +x nanyx`, and put it on your `PATH`.

If you would rather build it yourself, follow [Working on the language](#working-on-the-language) below; the result is the same binary.

### 2. Run your first program

Create `hello.nyx`:

```nanyx
import nanyx/console

println("Hello, Nanyx!")
```

and run it:

```bash
nanyx run hello.nyx
# Hello, Nanyx!
```

`nanyx wasm hello.nyx` writes the WebAssembly text to `hello.wat` instead of running it, and `nanyx check hello.nyx` type-checks and prints the inferred types.

A type or name error does not stop `nanyx run`: the program runs, and reports the error if it reaches the code where it was found. `nanyx wasm` and `nanyx build` take `--release` to refuse any error.

### 3. Set up your editor

The Nanyx extension for VS Code (search for "Nanyx" in the Marketplace; its source is in `extension/`) gives you syntax highlighting and type errors as you type.

### 4. Learn the language

- [Hello, World!][hello], then the [language tour][tour]
- [Files, packages and projects][packages]
- [The standard library][stdlib], as designed; most of it is not implemented yet
- `examples/` in this repository, for longer programs

## Working on the language

Nanyx is under active development and the language is still changing, so contributions, bug reports and documentation fixes are all welcome.

### What is in this repository

| Path | Contents |
|---|---|
| `compiler/` | The compiler: an F# parser (FParsec), type checker and WebAssembly backend, a C# CLI (`nanyx`), and the runtime that embeds Wasmtime |
| `docs/` | The documentation site, built with Astro |
| `examples/` | Example Nanyx programs, also rendered into the docs |
| `extension/` | The VS Code extension and its language server |

### Prerequisites

- The [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) for the compiler. `dotnet --version` should print `10.x`. Everything else, including Wasmtime, is restored from NuGet.
- [Node.js](https://nodejs.org) (with npm) for the docs site and the VS Code extension.

### The compiler

```bash
cd compiler
dotnet build Nyx.Compiler.slnx
dotnet test Nyx.Compiler.slnx
dotnet run --project Nanyx.Compiler.Cli/NanyxCompiler.Cli.csproj -- run hello.nyx
```

The CLI has `run`, `wasm`, `check` and `parse` targets; `parse` prints the AST and is the quickest way to see what the parser makes of a file. The pipeline is parser (`Nanyx.Parser`) → type checker (`Nanyx.Compiler`) → WAT backend (`Nanyx.Compiler.Wasm`) → Wasmtime (`Nanyx.Compiler.Runtime`), and each stage has its own test project. The backend tests are snapshot fixtures (`.nyx` next to the expected `.wat`) plus execution tests that run the built CLI and check its output. See [`compiler/README.md`](compiler/README.md) for the layout and the current state of what compiles.

### The docs site

```bash
cd docs
npm install
npm run dev      # http://localhost:4321
```

Pages are Markdown files under `docs/src/content/<section>/`. The site's own README explains how sections, code highlighting and the examples page are wired up. The language is specified by its documentation, so a language change usually starts with a docs change.

### The VS Code extension

```bash
cd extension
npm install
npm run compile
```

Then press F5 in VS Code to launch an Extension Development Host. The extension's README covers its settings and how the language server calls the compiler.

### Continuous integration and releases

GitHub Actions build and test the compiler on Linux and Windows for every pull request. Merging a bump of `<Version>` in `compiler/Nanyx.Compiler.Cli/NanyxCompiler.Cli.csproj` releases the compiler: `main` tags the commit `compiler/<version>` and publishes the self-contained binaries it built to a GitHub release. The extension works the same way: merging a bump of `version` in `extension/package.json` tags the commit `extension/<version>` and publishes the package to the Marketplace, Open VSX and a GitHub release.

## Design

Nanyx aims to feel small: few rules, few exceptions, and features that compose rather than special cases. The compiler emits only errors, never warnings, and a debug build still runs in the face of type errors so iteration stays fast; style and quality checks come from pluggable [linters][linters]. There is no function overloading, no currying by default, and no arbitrary unions, each for reasons written up in the docs.

[Principles][principles] · [Why no overloading?][overloading] · [Why no currying?][currying] · [Why no `string | int`?][unions]

[docs]: https://nanyx.dev/docs
[hello]: https://nanyx.dev/docs/getting-started/hello-world
[tour]: https://nanyx.dev/docs/tour/variables
[packages]: https://nanyx.dev/docs/tour/packages
[records]: https://nanyx.dev/docs/tour/records
[patterns]: https://nanyx.dev/docs/tour/pattern-matching
[attached]: https://nanyx.dev/docs/advanced/attached-definitions
[memory]: https://nanyx.dev/docs/advanced/memory
[workflows]: https://nanyx.dev/docs/advanced/workflows
[linters]: https://nanyx.dev/docs/advanced/linters
[stdlib]: https://nanyx.dev/docs/stdlib/standard-library
[principles]: https://nanyx.dev/docs/foundations/principles
[classes]: https://nanyx.dev/docs/coming-from-other-languages/classes
[overloading]: https://nanyx.dev/docs/coming-from-other-languages/overloading
[currying]: https://nanyx.dev/docs/coming-from-other-languages/currying
[unions]: https://nanyx.dev/docs/coming-from-other-languages/arbitrary-unions
