namespace NyxCompiler

open System
open System.Collections.Concurrent
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading

/// What running a linter package needs of the WebAssembly backend and the runtime, which the compiler's core doesn't
/// reference: the CLI, which references both, gives it (see the Linters page in Work in progress).
type LintRunner =
    { /// The lint build whose root is the module `TypedModule`, which type-checked, in WebAssembly's binary format.
      Assemble: TypedModule -> byte array
      /// The lint build in WebAssembly's binary format, compiled once, as a function that lints a module: given the
      /// answers to its questions, it runs the build and gives the bytes of its reports, or why it gave none. Or why
      /// the build can't be run.
      Load: byte array -> Result<(byte array -> Result<byte array, string>) -> Result<byte array, string>, string> }

/// Lint builds of linter packages: a linter's `lint`, passed to the linter host's `run`, as a test build passes its
/// tests to the test host's. A lint build is built once, and kept in the package cache by the SHA-256 of the files it
/// is built from and the compiler's version; and compiled once per process, so that linting a module costs an instance
/// and a call, not a compile, however often the editor asks.
module LintBuilds =
    /// The name of the file a lint build's root is compiled as, in the linter package's directory: no file of its own.
    let rootFile = "__lint.nyx"

    /// The source of the root of a lint build of the linter `linter` declares: a module whose `@entry` is the `lint` its
    /// root module exports.
    let rootSource (linter: LinterDeclaration) =
        $"""import (
  {linter.Root} as linter
  {LinterPackages.analysisPackage}/analysis as (Report)
  {LinterPackages.analysisPackage}/syntax as (Module)
)

@entry
def lint: [Analysis] Module -> list(Report) = {{ m -> linter.lint(m) }}
"""

    /// The linter package of `linter` under the linter host, found in `store`, as its lint build is built; or why the host
    /// can't be read.
    let private underHost (store: PackageStore) (linter: LinterPackage) : Result<Package, string> =
        Package.withHost store linter.Package.Manifest.Root Hosts.lintPackage linter.Package

    /// The compile of the root of the lint build of `linter`, found in `store`, compiled as `rootFile` in its package's
    /// directory, so that its imports name the package's modules; or why the linter host can't be read.
    let rootResult (store: PackageStore) (linter: LinterPackage) : Result<CompileResult, string> =
        underHost store linter
        |> Result.map (fun package ->
            let path = Path.Combine(package.Manifest.Root, rootFile)
            let modules = Package.modules package
            Compiler.compileSourceIn { modules with Files = modules.Files @ [ path ] } Linters.none path (rootSource linter.Declaration))

    /// The SHA-256, in lower-case hexadecimal, of what the lint build of the linter `linter` declares, whose package under
    /// the linter host is `package`, is built from: its root's source, and the path of each source file of the package,
    /// of the packages it depends on and of its host's, relative to its package, with the file's text.
    let private fingerprint (linter: LinterDeclaration) (package: Package) =
        let rec files (package: Package) =
            let own = package.Sources |> List.map (fun file -> Path.GetRelativePath(package.Manifest.Root, file).Replace('\\', '/'), file)
            [ package.Manifest.Name, own ] @ (package.Dependencies @ Option.toList package.HostPackage |> List.collect files)
        let text = StringBuilder(rootSource linter)
        for name, own in files package |> List.distinctBy fst |> List.sortBy fst do
            for relative, file in own |> List.sortBy fst do
                text.Append(name).Append('/').Append(relative).Append('\n').Append(File.ReadAllText file).Append('\u0000') |> ignore
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant()

    /// Where the lint build whose files have the SHA-256 `sha` is kept in the cache of `store`, for this compiler.
    let cacheFile (store: PackageStore) (sha: string) = Path.Combine(store.Cache, ".lint", Toolchain.version, sha + ".wasm")

    /// How many lint builds this process has built, rather than read from the cache: for tests, which count them.
    let mutable private built = 0

    /// How many lint builds this process has built (see `built`).
    let count () = Volatile.Read &built

    /// Each lint build this process has compiled, by the SHA-256 of its files: the linter it runs, or why it can't be.
    let private loaded = ConcurrentDictionary<string, Lazy<Result<(byte array -> Result<byte array, string>) -> Result<byte array, string>, string>>>()

    /// The lint build of `package`, a linter's package under the linter host, whose files have the SHA-256 `sha`: from
    /// the cache of `store`, or built with `runner` and kept there. Or why it can't be built.
    let private wasmOf (store: PackageStore) (runner: LintRunner) (linter: LinterPackage) (sha: string) : Result<byte array, string> =
        let file = cacheFile store sha
        if File.Exists file then
            Result.Ok(File.ReadAllBytes file)
        else
            match rootResult store linter with
            | Result.Error reason -> Result.Error reason
            | Result.Ok root ->
                match root.Typed, root.Diagnostics |> List.filter Diagnostics.isError with
                | Some typed, [] ->
                    let wasm = runner.Assemble typed
                    Interlocked.Increment &built |> ignore
                    Directory.CreateDirectory(Path.GetDirectoryName file) |> ignore
                    // Written beside it first, so that another process never reads half of it.
                    let partial = $"{file}.{Guid.NewGuid():N}"
                    File.WriteAllBytes(partial, wasm)
                    File.Move(partial, file, true)
                    Result.Ok wasm
                | _, errors ->
                    let shown = errors |> List.truncate 3 |> List.map Manifest.describe |> String.concat "; "
                    Result.Error $"its lint build has errors: {shown}"

    /// The linters this process has made from packages that never change, by the store's cache, the package's directory
    /// and its name (see `make`).
    let private made = ConcurrentDictionary<string * string * string, Linter>()

    /// `make`, with the package read under the linter host and its files read and hashed again: what a linter found by
    /// its `path` is made with each time, since its files may have changed.
    let private makeAfresh (store: PackageStore) (runner: LintRunner) (linter: LinterPackage) : Result<Linter, string> =
        underHost store linter
        |> Result.bind (fun package ->
            let sha = fingerprint linter.Declaration package
            loaded.GetOrAdd(sha, fun sha -> lazy (wasmOf store runner linter sha |> Result.bind runner.Load)).Value)
        |> Result.map (fun run ->
            { Name = linter.Declaration.Name
              Package = linter.Entry.Package
              Rules = linter.Declaration.Rules
              Lint = LintAnswers.lintWith run })

    /// The linter `linter` declares, which runs its lint build, built with `runner` and kept in the cache of `store` (see
    /// `wasmOf`); or why it can't be made, which its `linterFailed` reports. The language server asks for it on every
    /// compile. A linter from the package index, from GitHub or shipped with the compiler is in a directory that never
    /// changes, so it is made once a process; one found by its `path` is made each time, which reads its package under
    /// the linter host and hashes its files, so that an edit to it is linted with, and builds only what changed.
    let make (store: PackageStore) (runner: LintRunner) (linter: LinterPackage) : Result<Linter, string> =
        match linter.Entry.Path with
        | Some _ -> makeAfresh store runner linter
        | None ->
            let key = store.Cache, linter.Package.Manifest.Root, linter.Entry.Package
            match made.TryGetValue key with
            | true, found -> Result.Ok found
            | false, _ ->
                let result = makeAfresh store runner linter
                result |> Result.iter (fun found -> made.TryAdd(key, found) |> ignore)
                result
