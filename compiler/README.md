# Nanyx Parser

A parser for the Nanyx programming language built with FParsec.

## Features

Currently supports parsing:

- **Module declarations**: `module ModuleName`
- **Value definitions**: `def identifier = expression`
- **Literals**:
  - Strings: `"Hello world"`
  - Integers: `42`
  - Floats: `3.14159`
  - Booleans: `true`, `false`
- **Identifiers**: Variable references like `someValue`
- **Function calls**: `println("Hello")`, `add(1, 2)`, nested calls
- **Lambda expressions**: 
  - Standard: `{ x -> x }`, `{ x, y -> add(x, y) }`, `{ 42 }`
  - **Shorthand lambdas**:
    - Binary operators: `{ * }` → `{ x, y -> x * y }`
    - Unary operations: `{ * 2 }` → `{ x -> x * 2 }`, `{ > 10 }` → `{ x -> x > 10 }`
    - Property access: `{ .name }` → `{ x -> x.name }`
- **Pipe operator**: `\`
  - Simple: `x \f` → `f(x)`
  - With arguments: `x \f(y, z)` → `f(x, y, z)`
  - Chaining: `x \f \g \h` → `h(g(f(x)))`
- **Binary operators**: `++` (concatenation), `+`, `-`, `*`, `/`, `<`, `>`, `<=`, `>=`, `==`, `!=`
  - With correct precedence (multiplication/division before addition/subtraction)
- **Parenthesized expressions**: `(1 + 2) * 3`
- **Whitespace handling**: Flexible whitespace and newlines

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

All 56 tests should pass:
- **Unit tests** (49): Literal parsing, identifiers, function calls, lambdas (standard & shorthand), pipe operator, binary operators, whitespace, errors
- **Integration tests** (7): Full .nyx file parsing from `testdata/` directory
  - `literals.nyx`: String, int, float, boolean literals
  - `function-calls.nyx`: Various function call patterns
  - `lambdas.nyx`: Lambda expressions with different parameter counts
  - `shorthand-lambdas.nyx`: Shorthand lambda syntax (binary ops, unary ops, property access)
  - `piping.nyx`: Pipe operator usage (simple, with args, chaining)
  - `binary-operators.nyx`: Arithmetic, comparison, and equality operators
  - `comprehensive.nyx`: Mix of all features
  - `binary-operators.nyx`: Arithmetic, comparison, and equality operators
  - `comprehensive.nyx`: Mix of all features

Run specific test files:
```powershell
dotnet test --filter "Parse literals.nyx"
```

## Continuous integration and releases

The `Compiler` workflow (`.github/workflows/compiler.yml`) builds and tests the solution on
Linux and Windows for every pull request and push to `main` that touches `compiler/`, and
publishes a self-contained `nanyx` executable for each supported platform as a workflow artifact.

To cut a release, bump `<Version>` in `Nanyx.Compiler.Cli/NanyxCompiler.Cli.csproj` and merge.
When the workflow on `main` finds no `compiler/<version>` tag for that version, it creates the tag
and attaches the binaries it just built and tested to a GitHub release; nothing is rebuilt. Pushes
that leave the version unchanged release nothing. (The VS Code extension is released the same way:
bump `version` in `extension/package.json` and merge.)

## Running programs

The parser, type checker, WASM backend and runtime are libraries; the `nanyx` CLI is the only
executable. It takes a target and a `.nyx` file:

| Target  | What it does                                                                 |
| ------- | ---------------------------------------------------------------------------- |
| `run`   | Compiles the file and runs it on the embedded Wasmtime engine                |
| `wasm`  | Emits WebAssembly text (`.wat`) next to the file, or to `--out <path>`       |
| `check` | Type-checks the file and prints the inferred types                           |
| `parse` | Prints the AST                                                               |

The hello world from the docs works as written:

```nanyx
import nanyx/console

println("Hello, Nanyx!")
```

```powershell
dotnet run --project compiler/Nanyx.Compiler.Cli/NanyxCompiler.Cli.csproj -- run hello.nyx
# Hello, Nanyx!
```

`run` keeps its own output off stdout: only the program writes there, diagnostics go to stderr, and
the exit code is 0 on success, 1 when the program fails to compile or traps, and 2 for a usage error.
The project form (`module main` with `@entry def main = { ... }`) runs the same way: `@entry` marks the
definition the program starts at.

`println` comes from the standard library's `nanyx/console` module (see below). A script imports it
explicitly; the module that declares `@entry` gets it implicitly, because the runtime calls the entry
point inside its `Console` context.

## The standard library

The standard library is an ordinary package named `nanyx`, written in Nanyx, with one module per file
under [`stdlib/nanyx/`](../stdlib/nanyx) at the repository root. A program imports its modules like any
other package's: `import nanyx/console`, `import nanyx/text`, or `import nanyx/text as t` for
qualified access.

It ships inside the compiler. `NanyxCompiler.fsproj` embeds every `stdlib/nanyx/*.nyx` file as a
resource named `nanyx/<module>.nyx`, so the published single-file `nanyx` binary carries the library
and reads nothing from disk. `import nanyx/<module>` reads that resource (`StandardLibrary` in
`Nanyx.Compiler/Builtins.fs`), compiles the module together with the program, and inlines its
definitions into the program the backend sees; importing a module the library does not have is a
compile error that lists the ones it does.

One module, `nanyx/prelude`, is never imported by name: the compiler imports it, unqualified, into every
other module, so its definitions are what a file sees with no imports at all. A module's own definitions
and its explicit imports take precedence over the prelude's, so a module can define its own `proc`.

| Module          | What it holds                                                                                     |
| --------------- | ------------------------------------------------------------------------------------------------- |
| `nanyx/prelude` | Imported automatically. `proc`, the result workflow: in `proc { ... }`, `try x` continues with the payload of `#some` or gives back any other tag (the block's type collects them), and `return x` is `#some(x)`. The `Result(a, e)` type (`#some(a) \| #error(e)`) with `Result.map`, `mapError`, `bind`, `defaultValue`, `isOk`, `isError`. The helpers for option-like unions, attached to the name `Option`: `Option.map`, `bind`, `defaultValue`, `isSome`, `isNone`, `toResult`, `mapOr`. `Int.parse`, which reads a decimal integer (`#some(int) \| #invalidInt`). `Bool.parse`, which reads `true` or `false` (`#some(bool) \| #invalidBool`). `Int.toString`, `Float.toString`, `Bool.toString` and `String.toString`, which write a value as interpolation does. The `List` functions: `List.length`, `head`, `map`, `filter`, `find`, `all`, `any`, `fold`, `foldBack`, `reverse`, `append`, `flatten`, `take`, `skip`, `zip` |
| `nanyx/console` | The `Console` context (`print`, `println`, `eprint`, `eprintln`, `readLine`, `readToEnd`, `flush`), the `IoError` type, and the instance the runtime supplies to the entry point |
| `nanyx/math`    | `abs`, `min`, `max`, `clamp` and `pow` on `int` (the docs make them generic over `Num(a)`, #186); `sqrt`, `floor`, `ceil` and `round` on `float` |
| `nanyx/result`  | Nothing: kept so that `import nanyx/result` still compiles now that `proc` is in the prelude      |
| `nanyx/text`    | Attached to `String`: `String.length`, `substring`, `startsWith`, `endsWith`, `indexOf`, `lastIndexOf`, `contains`, `count`, `removePrefix`, `removeSuffix`, `uppercase`, `lowercase`, `trim`, `trimStart`, `trimEnd`, `isEmpty`, `isBlank`, `repeat`, `padStart`, `padEnd`, `split`, `join`, `concat`, `replace`, `lines`, `words`; called as `String.trim(s)` or `s \trim` |

The library is written over a handful of **compiler intrinsics**: operations that cannot be expressed in
Nanyx itself, such as writing to standard output or reading a byte of a string. They are listed with
their types in one place, `Intrinsics` in `Nanyx.Compiler/Builtins.fs`, are only in scope while a
standard-library module is being checked (user code cannot call them), and are lowered by the backend
onto helper functions it emits with the module (`runtimeHelpers` in `Nanyx.Compiler.Wasm/WasmRuntime.fs`).
Their names all start with `__nanyx_`.

Strings are UTF-8 and this first slice of `nanyx/text` works byte by byte: `length` counts bytes,
indices and widths are byte offsets, `uppercase`/`lowercase` only change ASCII letters and the trimming
functions and `words` only treat ASCII whitespace as whitespace. `indexOf` and `lastIndexOf` return
`#some(index)` or `#notFound`, as the language docs specify; `split`, `replace`, `substring` and `join`
take their arguments positionally, where the docs name them. `++` (concatenation) and `==`
on strings are language operators, lowered by the backend. (The language docs call this module
`string`; it is imported as `nanyx/text`.)

To add a module: create `stdlib/nanyx/<name>.nyx` declaring `module <name>`, mark what it offers with
`export`, and rebuild; the glob picks it up and `import nanyx/<name>` resolves. Definitions inside a
module must be written before the definitions that use them (self-recursion is fine). Add an intrinsic
only when a function genuinely cannot be written in Nanyx: give it a name and type in `Intrinsics`, and
a WebAssembly body of the same name in `runtimeHelpers`, and list it, with the helpers it calls, in
`helperDependencies` (the order of that list is the order helpers are emitted in, so add new ones at the end).

## The language server

`nanyx lsp` runs a [Language Server Protocol](https://microsoft.github.io/language-server-protocol/)
server over standard input and output, so the same binary that compiles programs also serves editors.
The VS Code extension in `extension/` launches it (see `extension/README.md`); any other LSP client
can start it with `nanyx lsp` and `TransportKind.stdio`.

The server lives in `Nanyx.LanguageServer` and calls the compiler as a library: on every open and
change it runs `Compiler.compileSource` (or `Compiler.compile` for an untitled buffer) on the editor's
text, keeps the `CompileResult` in `DocumentStore`, and publishes the resulting diagnostics; hover and
go-to-definition, lenses and inlay hints read the same result, so each change is compiled once. Standard-library imports resolve because the library is
embedded in the compiler; imports by path resolve relative to the file as they do for `nanyx check`.

What it does today:

- `initialize`/`shutdown`/`exit`, full text document sync, and `textDocument/publishDiagnostics` on
  open and change (cleared on close).
- Each diagnostic is published over the whole span of the node it is about, with its code (`NYX0201`)
  as `code` and its related locations as `relatedInformation`. One in a module the document imports
  is shown at the start of the document, naming that file and linking to the place in it; one about the
  whole compilation is shown at the start of the file.
- A file that fails to parse gets one diagnostic for the parse error, and a compiler exception is
  reported as a diagnostic rather than taking the server down.
- `textDocument/hover` shows the inferred type of the narrowest expression under the pointer, with
  made-up type variables named `α`, `β`, ... through the Greek alphabet, a union's rest variable from
  the other end (`#some(α) | ω`), a record's rest that appears nowhere else as a bare spread
  (`list(name: α, ...) -> list(α)`, with a lone record argument in the application's own brackets), and
  annotation variables keeping their names, written bare as in an
  annotation (`nanyx lsp --latin-type-vars` uses `a`, `b`, ... and `z` instead), and a type alias by its
  name (`Customer`, not `(name: string)`: inference carries aliases as `TyAlias` and looks through them); on a call's function name it shows the function's type at that call (so `id` in `id(5)` is
  `int -> int`), and on a declaration's own name (a def, a parameter, a pattern variable) the type of
  what it declares. The types come from the typed module's `ExprTypes` and `CalleeTypes`, recorded by
  node during inference, and `SymbolTypes`, by symbol (see `NyxCompiler.Hover`). A file that does not type-check shows no hover.
  On a type's name, in an annotation or a type definition, hover shows the type's definition instead
  (`type Point = (x: int, y: int)`; see `Definition.typeDefinitionAt`).
- `textDocument/definition` goes from a value, parameter, pattern variable, import, type name or context
  member to where it is declared, from the typed module's `References` and `Symbols` (see
  `NyxCompiler.Definition`). On a declaration's own name it returns that declaration. A definition in an
  imported source file opens that file. The standard library is embedded in the compiler, so a definition
  in it opens a read-only copy of its module's source, which the server writes under the user's local
  application data (see `StandardLibrarySource`).
- `textDocument/references` lists the names in the document that refer to the same declaration, and the
  declaration when asked (`Definition.referencesTo`). Only the document's own compile is searched, not the
  files that import it.
- `textDocument/documentSymbol` gives the outline: top-level defs, types, contexts and `module name =`
  blocks, with their local defs, fields and members nested (`NyxCompiler.Outline`). It needs only a parse,
  so a type error does not empty it; while the text does not parse, the last good text's outline is shown.
- `textDocument/codeAction` offers each fix of a diagnostic in the range as a quick fix: a linter's, such
  as removing an unused local or an unused import, or the import of the standard-library module an unknown name comes from
  (`import nanyx/text` for `String.trim(s)`, `import nanyx/math as math` for `math.abs(x)`; see
  `NyxCompiler.ImportFixes`). Other compiler errors have no fixes yet.
- `textDocument/codeLens` shows the inferred type above each top-level def that does not write its own
  (`α -> α` above `def id = { x -> x }`); a destructuring def's lenses say which name each is for
  (`p: int`). `textDocument/inlayHint` shows `: type` after each unannotated local def, lambda parameter
  and pattern variable. The types come from the typed module's `SymbolTypes`, leaving out its
  `AnnotatedSymbols`, and are named together a top-level def at a time, so a hint's `α` is the one in its def's
  lens (see `NyxCompiler.TypeHints`). `nanyx lsp --top-level-type-hints inlay` shows the top-level defs'
  types as inlay hints after their names instead of lenses, and `--top-level-type-hints off` shows
  neither for them (the default is `lens`). `--no-local-type-hints`, `--no-parameter-type-hints` and
  `--no-pattern-variable-type-hints` turn off the inlay hints for each of the other kinds of declaration. A file
  that does not type-check shows neither.

Each feature is one handler class in `Nanyx.LanguageServer/Handlers/`, registered in
`NanyxLanguageServer.Configure`; what is still to come is tracked in
[#113](https://github.com/nanyx-lang/nanyx/issues/113).
Hover shows only types, not a name's declared signature or docs. Two compiler improvements
the server makes visible: a parse error currently drops the whole file (so one error hides
everything after it), and inference stops at a module's first error, so only one shows at a time.

The tests in `Nanyx.LanguageServer.Tests` start the server in-process and talk to it through
OmniSharp's LSP client over a pair of pipes, so they exercise the real wire protocol.

## WebAssembly (MVP)

Current WASM backend support is intentionally minimal and focused on bootstrapping:

- top-level `def` values exported as wasm functions
- lambda-backed defs compiled as wasm functions with `i32` params/result
- integer literals
- integer binary operators (`+`, `-`, `*`, `/`, `%`, comparisons)
- bare tag literals (`#ok`, `#error`, etc.) lowered to deterministic, odd `i32` codes; a tag with a payload
  (`#some(x)`) is a pointer to a heap cell of (code, payload)
- function calls to known module defs
- simple expression blocks with local `def` assignments (`local.set`/`local.get`)
- top-level expressions (and the `@entry` definition) run from a WebAssembly start function
- strings are pointers to length-prefixed UTF-8 bytes; `++` and `==` on strings call emitted helpers
- list literals, `++` on lists, and list patterns (`[]`, `[x, ...rest]`, `[1, y]`) over heap-allocated cons cells
- every object on the heap has a reference count in its header, and the compiler inserts `dup` and `drop` (Perceus
  reference counting), so an object is freed once nothing refers to it, and one a `match` takes apart and holds
  the only reference to is rewritten in place into a new one of the same size (see `compiler/design/memory.md`)
- `dbg(...)` of integers and strings, and `nanyx/console`, write to stdout through WASI `fd_write`
- `dbg(#tag)` calls a host import `(import "env" "dbg_tag_<tag>" (func))`, which the runtime provides
- a function that requires a context (`[Console]`) takes its members as function-table slots; a
  `use` binding supplies them, otherwise a module-level function of the member's name does (that is how
  the runtime's `Console` reaches `@entry`)

Other language constructs will currently fail fast with an explicit "Unsupported ... for WASM MVP backend" message.

## AST Structure

```fsharp
type Literal =
    | StringLit of string
    | IntLit of int
    | FloatLit of float
    | BoolLit of bool

type Expression =
    | LiteralExpr of Literal
    | IdentifierExpr of Identifier

type Definition =
    | ValueDef of Identifier * Expression

type TopLevelItem =
    | ModuleDecl of ModuleName
    | Def of Definition

type Module = TopLevelItem list
```

## Next Steps

To extend the parser, consider adding:
- Function definitions with parameters
- Lambda expressions
- Function calls
- Operators and binary expressions
- Pattern matching
- Type annotations
- Comments (single-line `--` and multi-line `---`)
- Indentation-based blocks
- Collections (lists, records, tuples)

## Example

Input:
```nanyx
module Test

def message = "Hello world"
def count = 42
def pi = 3.14159
def flag = true
```

Parsed AST:
```fsharp
[ModuleDecl "Test";
 Def (ValueDef ("message", LiteralExpr (StringLit "Hello world")));
 Def (ValueDef ("count", LiteralExpr (IntLit 42)));
 Def (ValueDef ("pi", LiteralExpr (FloatLit 3.14159)));
 Def (ValueDef ("flag", LiteralExpr (BoolLit true)))]
```
