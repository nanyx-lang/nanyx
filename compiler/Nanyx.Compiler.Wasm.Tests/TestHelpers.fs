module TranspilerWasmTestHelpers

open System
open System.IO
open System.Diagnostics
open System.Text.RegularExpressions
open NyxCompiler
open Parser.Program
open Transpiler.Wasm.CodeGen
open Xunit

let private testRootDir = __SOURCE_DIRECTORY__
let private featureRootDir = Path.Combine(testRootDir, "Features")

let normalizeOutput (value: string) =
    value.Replace("\r\n", "\n").TrimEnd()

/// `source` with `\n` line breaks, whatever the line endings of the test file's checkout.
let normalized (source: string) = source.ReplaceLineEndings "\n"

/// `normalized source` with a line break after it: for a source that ends in a line break, written with its closing
/// quotes, and the code after them, on its last line.
let withFinalNewline (source: string) = normalized source + "\n"

/// The text of the test data file `fileName` in `directory`.
let loadTestFile (directory: string) (fileName: string) =
    let filePath = Path.Combine(directory, fileName)
    if not (File.Exists(filePath)) then
        failwith $"Test data file not found: {filePath}"
    File.ReadAllText(filePath)

let loadFeatureFile featureName fileName = loadTestFile (Path.Combine(featureRootDir, featureName)) fileName

/// `source`, type-checked and compiled with `options`. Errors the type checker defers to run time are compiled to
/// traps, as `nanyx run` does. The text is assembled to the binary format too, so that every module a test compiles
/// shows the assembler reads what the backend writes (see `WasmBinary`).
let transpileWatWith (options: WasmOptions) (source: string) =
    match Compiler.compile source with
    | { Typed = Some typed } ->
        let wat = transpileTypedModuleWithOptions options typed |> fst
        Transpiler.Wasm.WasmBinary.assemble wat |> ignore
        wat
    | result ->
        let messages = result.Diagnostics |> List.map (fun d -> d.Message) |> String.concat "\n"
        failwith $"Compile error: {messages}"

let transpileWat (source: string) = transpileWatWith WasmOptions.Default source

/// `source` compiled without inlining, for a test of how a call is lowered: inlined, the call would not be there.
let transpileWatNotInlined (source: string) = transpileWatWith { WasmOptions.Default with Inline = false } source

/// The text of the function `name` in `wat`, from its `(func` to the line that closes it.
let functionText (name: string) (wat: string) =
    let start = wat.IndexOf($"(func ${name} ")
    Assert.True(start >= 0, $"no function {name}")
    wat.Substring(start, wat.IndexOf("\n  )", start) - start)

/// A function's text with each local it declares named by its place among them, so that two functions whose code
/// differs only in their locals' names compare equal.
let withLocalsNumbered (text: string) =
    Text.RegularExpressions.Regex.Matches(text, @"\(local \$(\w+)")
    |> Seq.mapi (fun index found -> index, found.Groups.[1].Value)
    |> Seq.fold
        (fun (text: string) (index, name) ->
            Text.RegularExpressions.Regex.Replace(text, @"\$" + Text.RegularExpressions.Regex.Escape name + @"\b", $"$$__local{index}"))
        text

/// How many times `text` occurs in `wat`.
let countOccurrences (text: string) (wat: string) = Text.RegularExpressions.Regex.Matches(wat, Text.RegularExpressions.Regex.Escape text).Count

/// The program in `filePath`, compiled with `options`; any diagnostic fails.
let transpileWatFileWith (options: WasmOptions) (filePath: string) =
    match Compiler.compileFile filePath with
    | result when not result.Diagnostics.IsEmpty ->
        let messages = result.Diagnostics |> List.map (fun d -> d.Message) |> String.concat "\n"
        failwith $"Compile error: {messages}"
    | result ->
        match result.Typed with
        | Some typed -> transpileTypedModuleWithOptions options typed |> fst
        | None -> failwith "Compile error: no typed module produced"

let transpileWatFile (filePath: string) = transpileWatFileWith WasmOptions.Default filePath

let assertFixture featureName baseName =
    let source = loadFeatureFile featureName (baseName + ".nyx")
    let expected = loadFeatureFile featureName (baseName + ".wat") |> normalizeOutput
    let actual = transpileWat source |> normalizeOutput
    Assert.Equal(expected, actual)

/// What `nanyx run <file>` produced.
type RunOutcome =
    { ExitCode: int
      StandardOutput: string
      StandardError: string }

/// Runs `command` in this process, in a shell (see `Shell`) whose directory is `workingDirectory` and whose program
/// reads `standardInput` and sees the environment variables `variables`. What the command and the program write to
/// stdout is captured, and so is what they write to stderr, in the order written.
let private inProcessWith (store: NyxCompiler.PackageStore option) (github: NyxCompiler.GitHubClient option) (stop: Threading.CancellationToken) (workingDirectory: string) (standardInput: string) (variables: (string * string) list) (command: Shell -> int) : RunOutcome =
    let inputFile = Path.Combine(Path.GetTempPath(), $"nanyx-stdin-{Guid.NewGuid():N}.txt")
    File.WriteAllText(inputFile, standardInput)
    try
        use output = new MemoryStream()
        use error = new MemoryStream()
        // Flushed at every write, so that the CLI's messages and the program's own output stay in the order written.
        use outputs = new StreamWriter(output, Text.UTF8Encoding(false), AutoFlush = true)
        use errors = new StreamWriter(error, Text.UTF8Encoding(false), AutoFlush = true)
        let shell = Shell(workingDirectory, outputs, errors, Nanyx.Runtime.ProgramIo.Redirected(inputFile, output, error, variables))
        let shell =
            Shell(
                shell.Directory,
                shell.Out,
                shell.Error,
                shell.Io,
                Packages = (store |> Option.defaultValue shell.Packages),
                Stop = stop,
                GitHub = (match github with Some github -> github | None -> Unchecked.defaultof<_>)
            )
        let exitCode = command shell
        { ExitCode = exitCode
          StandardOutput = Text.Encoding.UTF8.GetString(output.ToArray())
          StandardError = Text.Encoding.UTF8.GetString(error.ToArray()) }
    finally
        File.Delete inputFile

/// The packages from the index that the tests' programs and the docs' examples use, though the compiler doesn't ship
/// them, so that no test needs the network: `nanyx.json` 0.1.0, the `.nyxpkg` its release on nanyx-lang/json carries
/// (`Packages/nanyx.json-0.1.0.nyxpkg`, downloaded from the release, whose SHA-256 the index records). It is
/// listed in an index on disk, in a directory of its own, and downloaded into the store's cache once, before any test
/// reads it, so that tests run in parallel never unpack it at the same time.
let private packagesForTests =
    lazy
        (let directory = Path.Combine(Path.GetTempPath(), $"nanyx-test-index-{Guid.NewGuid():N}")
         let store =
             { Cache = Path.Combine(directory, "cache")
               Index = Path.Combine(directory, "index")
               GitHub = GitHub.cached (Path.Combine(directory, "cache")) }
         let bytes = File.ReadAllBytes(Path.Combine(__SOURCE_DIRECTORY__, "Packages", "nanyx.json-0.1.0.nyxpkg"))
         let sha256 = PackageArchive.sha256 bytes
         Directory.CreateDirectory(Path.Combine(store.Index, "artifacts")) |> ignore
         Directory.CreateDirectory(Path.Combine(store.Index, "index", "nanyx")) |> ignore
         File.WriteAllBytes(Path.Combine(store.Index, "artifacts", "nanyx.json-0.1.0.nyxpkg"), bytes)
         File.WriteAllText(Path.Combine(store.Index, "config.json"), """{ "format": 1, "index": "index/" }""")
         File.WriteAllText(
             Path.Combine(store.Index, "index", "nanyx", "nanyx.json.json"),
             $$"""{ "name": "nanyx.json", "versions": [{ "version": "0.1.0", "url": "../../artifacts/nanyx.json-0.1.0.nyxpkg", "sha256": "{{sha256}}", "dependencies": {}, "yanked": false, "available": true }] }"""
         )
         let locked = { Name = "nanyx.json"; Version = (SemanticVersion.tryParse "0.1.0").Value; Sha256 = sha256; Dependencies = [] }
         match PackageIndex.download store locked with
         | Result.Ok _ -> store
         | Result.Error message -> failwith message)

/// The package store the tests build and run with: an index on disk and a cache of their own, with the packages from
/// the index the tests use (see `packagesForTests`), in place of the user's.
let testStore () : PackageStore = packagesForTests.Value

/// `inProcessWith` the tests' package store (see `testStore`).
let private inProcess (workingDirectory: string) (standardInput: string) (variables: (string * string) list) (command: Shell -> int) : RunOutcome =
    inProcessWith (Some(testStore ())) None Threading.CancellationToken.None workingDirectory standardInput variables command

/// The `nanyx` command `arguments` names, with its options, as a call of what the CLI runs for it (see `CliCommands`).
/// Only the commands and options the tests use are read. As in the CLI, `--` ends the options: what follows it
/// is a path or a program argument, whatever it starts with.
let private command (arguments: string list) : Shell -> int =
    // Whether `flag` is among `arguments` before any `--`, and the arguments without it
    let takeFlag (flag: string) (arguments: string list) =
        let endOfOptions = arguments |> List.tryFindIndex ((=) "--") |> Option.defaultValue arguments.Length
        let before, after = List.splitAt endOfOptions arguments
        List.contains flag before, List.filter ((<>) flag) before @ after
    let rec options (outputPath: string, release: bool, timings: bool, host: string, paths: string list) (rest: string list) =
        match rest with
        | "--" :: rest -> outputPath, release, timings, host, Array.ofList (paths @ rest)
        | ("--out" | "-o") :: path :: rest -> options (path, release, timings, host, paths) rest
        | "--release" :: rest -> options (outputPath, true, timings, host, paths) rest
        | "--timings" :: rest -> options (outputPath, release, true, host, paths) rest
        | "--host" :: name :: rest -> options (outputPath, release, timings, name, paths) rest
        | path :: rest -> options (outputPath, release, timings, host, paths @ [ path ]) rest
        | [] -> outputPath, release, timings, host, Array.ofList paths
    match arguments with
    | [ "--version" ] -> CliCommands.VersionIn
    | "new" :: name :: directory ->fun shell -> CliCommands.NewIn(shell, name, Array.ofList directory)
    | "manifest" :: rest ->
        let json, rest = takeFlag "--json" rest
        fun shell -> CliCommands.ManifestIn(shell, json, Array.ofList rest)
    | "check" :: rest ->
        let _, _, timings, host, path = options (null, false, false, null, []) rest
        fun shell -> CliCommands.CheckIn(shell, path, timings, host)
    | "run" :: rest ->
        let noInline, rest = takeFlag "--no-inline" rest
        let checkMemory, rest = takeFlag "--check-memory" rest
        let memory, rest = takeFlag "--memory" rest
        let memoryCheck = if memory then MemoryCheck.Report elif checkMemory then MemoryCheck.Check else MemoryCheck.Off
        // Each `--allow-dir <directory>` before any `--`, and the arguments without them.
        let rec allowing (directories: string list) (kept: string list) (rest: string list) =
            match rest with
            | "--" :: _ -> List.rev directories, List.rev kept @ rest
            | "--allow-dir" :: directory :: rest -> allowing (directory :: directories) kept rest
            | argument :: rest -> allowing directories (argument :: kept) rest
            | [] -> List.rev directories, List.rev kept
        let allowed, rest = allowing [] [] rest
        let profile, rest =
            match rest with
            | "--profile" :: file :: rest -> file, rest
            | _ -> null, rest
        let _, _, timings, host, path = options (null, false, false, null, []) rest
        fun shell -> CliCommands.RunIn(shell, memoryCheck, path, timings, host, not noInline, Array.ofList allowed, profile)
    | "build" :: rest ->
        let noInline, rest = takeFlag "--no-inline" rest
        let wat, rest = takeFlag "--wat" rest
        let locked, rest = takeFlag "--locked" rest
        let outputPath, release, timings, host, path = options (null, false, false, null, []) rest
        fun shell -> CliCommands.BuildIn(shell, outputPath, release, path, timings, host, not noInline, (if wat then ProgramFormat.Text else ProgramFormat.Binary), locked)
    | "wasm" :: inputFile :: rest ->
        let wat, rest = takeFlag "--wat" rest
        let outputPath, release, _, _, _ = options (null, false, false, null, []) rest
        fun shell -> CliCommands.WasmIn(shell, inputFile, outputPath, release, (if wat then ProgramFormat.Text else ProgramFormat.Binary))
    | "migrate" :: rest ->
        let name, path =
            match rest with
            | "--name" :: name :: path -> name, path
            | path -> null, path
        fun shell -> CliCommands.MigrateIn(shell, name, Array.ofList path)
    | "pull" :: path -> fun shell -> CliCommands.PullIn(shell, Array.ofList path)
    | "bump" :: path -> fun shell -> CliCommands.BumpIn(shell, Array.ofList path)
    | [ "api-check"; older; newer ] -> fun shell -> CliCommands.ApiCheckIn(shell, older, newer)
    | "publish" :: path -> fun shell -> CliCommands.PublishIn(shell, Array.ofList path)
    | [ "yank"; package; version ] -> fun shell -> CliCommands.YankIn(shell, package, version)
    | "pack" :: rest ->
        let outputPath, _, _, _, path = options (null, false, false, null, []) rest
        fun shell -> CliCommands.PackIn(shell, outputPath, path)
    | "install" :: package :: rest ->
        let rec installOptions (version: string, directory: string) (rest: string list) =
            match rest with
            | "--version" :: version :: rest -> installOptions (version, directory) rest
            | "--path" :: directory :: rest -> installOptions (version, directory) rest
            | _ -> version, directory
        let version, directory = installOptions (null, null) rest
        fun shell -> CliCommands.InstallIn(shell, package, version, directory)
    | _ -> failwith $"""The tests do not run `nanyx {String.concat " " arguments}`"""

/// Does what `nanyx` does with `arguments`, through the same code (`CliCommands`) but in this process, which saves
/// starting the CLI and checking the standard library again for every command. `standardInput` is a program's stdin.
let nanyxWithInput (standardInput: string) (arguments: string list) : RunOutcome =
    inProcess (Directory.GetCurrentDirectory()) standardInput [] (command arguments)

/// Does what `nanyx` does with `arguments`, with the environment variables `variables` (see `nanyxWithInput`).
let nanyxWithVariables (variables: (string * string) list) (arguments: string list) : RunOutcome =
    inProcess (Directory.GetCurrentDirectory()) "" variables (command arguments)

/// Does what `nanyx` does with `arguments` in `directory`, as a user does who has changed to it (see `nanyxWithInput`).
let nanyxIn (directory: string) (arguments: string list) : RunOutcome =
    inProcess directory "" [] (command arguments)

/// `nanyxIn` for a command that serves a program until `stop` is cancelled, as `nanyx run` does one whose host
/// answers HTTP requests.
let nanyxInUntil (stop: Threading.CancellationToken) (directory: string) (arguments: string list) : RunOutcome =
    inProcessWith None None stop directory "" [] (command arguments)

/// `nanyxIn`, with the package cache and index of `store` in place of the user's.
let nanyxInStore (store: NyxCompiler.PackageStore) (directory: string) (arguments: string list) : RunOutcome =
    inProcessWith (Some store) None Threading.CancellationToken.None directory "" [] (command arguments)

/// `nanyxInStore`, with `github` in place of GitHub: what `nanyx publish` and `nanyx yank` send their requests to.
let nanyxWithGitHub (store: NyxCompiler.PackageStore) (github: NyxCompiler.GitHubClient) (directory: string) (arguments: string list) : RunOutcome =
    inProcessWith (Some store) (Some github) Threading.CancellationToken.None directory "" [] (command arguments)

/// Pulls the dependencies of the package in `packageDirectory` and packs it with `nanyx pack` into the `artifacts` directory of the index of `store`, a
/// directory on disk, and lists it in the index after the versions listed already, as a release would, with
/// `dependencies` as the index writes them and `fields`, more of its entry's JSON, such as `, "yanked": true`. Gives
/// its SHA-256.
let publishToIndex (store: NyxCompiler.PackageStore) (packageDirectory: string) (dependencies: (string * string) list) (fields: string) : string =
    let artifacts = Path.Combine(store.Index, "artifacts")
    let pulled = nanyxInStore store packageDirectory [ "pull" ]
    if pulled.ExitCode <> 0 then failwith pulled.StandardError
    let packed = nanyxInStore store packageDirectory [ "pack"; "--out"; artifacts ]
    if packed.ExitCode <> 0 then failwith packed.StandardError
    let hash = packed.StandardOutput.ReplaceLineEndings("\n").Split('\n') |> Array.find (fun line -> line.StartsWith "sha256 ") |> fun line -> line.Substring 7
    let manifest =
        match NyxCompiler.Manifest.tryFind packageDirectory with
        | Result.Ok(Some path) ->
            match NyxCompiler.Manifest.read path with
            | Result.Ok manifest -> manifest
            | Result.Error _ -> failwith $"'{path}' doesn't read"
        | _ -> failwith $"'{packageDirectory}' has no manifest"
    File.WriteAllText(Path.Combine(store.Index, "config.json"), """{ "format": 1, "index": "index/" }""")
    let listing = Path.Combine(store.Index, "index", manifest.Name.Split('.')[0], manifest.Name + ".json")
    Directory.CreateDirectory(Path.GetDirectoryName listing) |> ignore
    let earlier =
        if File.Exists listing then
            let text = File.ReadAllText listing
            text.Substring(text.IndexOf '[' + 1, text.LastIndexOf ']' - text.IndexOf '[' - 1).Trim() + ","
        else
            ""
    let written = dependencies |> List.map (fun (name, range) -> $"\"{name}\": \"{range}\"") |> String.concat ", "
    let entry =
        $$"""{ "version": "{{manifest.Version}}", "url": "../../artifacts/{{manifest.Name}}-{{manifest.Version}}.nyxpkg", "sha256": "{{hash}}", "dependencies": { {{written}} }{{fields}} }"""
    File.WriteAllText(listing, $$"""{ "name": "{{manifest.Name}}", "versions": [{{earlier}} {{entry}}] }""")
    hash

/// Does what `nanyx run <inputPath>` does, with `--check-memory` when `checkMemory` is set (see `nanyxWithInput`).
/// `standardInput` is the program's stdin. Its stdout is captured, and so is its stderr, with the compiler's diagnostics
/// and the run's own errors in it as the CLI prints them.
/// Without `inlineCalls`, as `--no-inline` does, every call is compiled as a call.
let runScriptWithOptions (checkMemory: bool) (inlineCalls: bool) (standardInput: string) (inputPath: string) : RunOutcome =
    inProcess (Directory.GetCurrentDirectory()) standardInput [] (fun shell -> CliCommands.RunScript(shell, inputPath, (if checkMemory then MemoryCheck.Check else MemoryCheck.Off), false, null, inlineCalls))

let runScriptWith (checkMemory: bool) (standardInput: string) (inputPath: string) : RunOutcome =
    runScriptWithOptions checkMemory true standardInput inputPath
/// Runs the program in `inputPath` with its memory checked, so that every program a test runs also fails it when it
/// frees a block twice, uses an object after freeing it, or leaves anything on the heap.
let runFileWithInput (standardInput: string) (inputPath: string) : RunOutcome = runScriptWith true standardInput inputPath

let runFile (inputPath: string) : RunOutcome = runFileWithInput "" inputPath

/// A new, empty directory of its own, removed with everything in it when disposed.
type TempDirectory() =
    let path = Path.Combine(Path.GetTempPath(), $"nanyx-{Guid.NewGuid():N}")
    do Directory.CreateDirectory path |> ignore
    member _.Path = path

    /// Writes `text`, with its line endings made `\n`, to the file at `relative` under the directory, making the
    /// directories it is in.
    member _.Write (relative: string) (text: string) =
        let full = Path.Combine(path, relative)
        Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
        File.WriteAllText(full, text.ReplaceLineEndings "\n")

    interface IDisposable with
        member _.Dispose() = try Directory.Delete(path, true) with _ -> ()

/// Runs `nanyx` with `arguments` (with `{package}` standing for the package's directory) on a package of `files`
/// (paths under its `src` directory, with their text), made in a directory of its own and removed afterwards. Gives
/// what it printed and whether it wrote the program to out/app.wasm.
/// A manifest's `host` entry naming the console host the compiler ships, at its version: what an application with an
/// `@entry` writes.
let consoleHostEntry = $"host = (package = \"{Hosts.consolePackage}\", version = \"{Toolchain.version}\")"

/// What `nanyx` printed for `arguments` (`{package}` standing for the package's directory) on an application under the
/// console host whose source files, in `src`, are `files`, with `dependencies` as its manifest's `dependencies`; and
/// whether it wrote `out/app.wasm`.
let private nanyxOnPackageWith (dependencies: string) (files: (string * string) list) (arguments: string list) : RunOutcome * bool =
    use directory = new TempDirectory()
    let root = directory.Path
    Directory.CreateDirectory(Path.Combine(root, "src")) |> ignore
    File.WriteAllText(
        Path.Combine(root, "app.nyxp"),
        $"(package = (name = \"app\", version = \"0.1.0\", sources = [\"src/**/*.nyx\"]), dependencies = {dependencies}, {consoleHostEntry})")
    for path, text in files do
        File.WriteAllText(Path.Combine(root, "src", path), text)
    let outcome = nanyxWithInput "" (arguments |> List.map (fun argument -> argument.Replace("{package}", root)))
    outcome, File.Exists(Path.Combine(root, "out", "app.wasm"))

/// `nanyxOnPackageWith` an application with no dependencies.
let nanyxOnPackage (files: (string * string) list) (arguments: string list) : RunOutcome * bool =
    nanyxOnPackageWith "[]" files arguments

/// `nanyxOnPackageWith` an application that depends on the console host's interface package, `nanyx.console.core`, so
/// that its files can import `nanyx.console.core/console`.
let nanyxOnPackageWithConsoleCore (files: (string * string) list) (arguments: string list) : RunOutcome * bool =
    nanyxOnPackageWith $"[(package = \"nanyx.console.core\", version = \"{Toolchain.version}\")]" files arguments

/// What `nanyx check` printed for a script holding `source`, saved as `main.nyx` in a directory of its own.
let checkScript (source: string) =
    use directory = new TempDirectory()
    let path = Path.Combine(directory.Path, "main.nyx")
    File.WriteAllText(path, source)
    nanyxWithInput "" [ "check"; path ]

/// Asserts that `output`, what `nanyx` printed, shows the diagnostic `code` as the CLI lays one out: a line
/// `error <code>: <headline>`, then `--> <file>:<line>:<column>`, whose place ends with `place` (`main.nyx:3:13`, or
/// `:3:13` for any file), and among the lines after it, before the next blank line, `= <body>`, whose body starts with
/// `body`. In the run's box, each line starts with its gutter.
let assertDiagnostic (code: string) (headline: string) (place: string) (body: string) (output: string) =
    let lines = output.ReplaceLineEndings("\n").Split '\n'
    let shown =
        lines
        |> Array.indexed
        |> Array.exists (fun (index, line) ->
            line.EndsWith $"error {code}: {headline}"
            && index + 1 < lines.Length
            && lines[index + 1].Contains "--> "
            && lines[index + 1].EndsWith place
            && (lines
                |> Array.skip (index + 2)
                |> Array.takeWhile (fun line -> line.Trim().TrimStart('│').Trim() <> "")
                |> Array.exists (fun line -> line.Contains $" = {body}")))
    Assert.True(shown, $"Expected error {code}: {headline} at {place} with the body '{body}' in:\n{output}")

/// The docs site's pages, under docs/src/content.
let docsContentDirectory =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "docs", "src", "content"))

/// What a docs example's fence says it is: the words after `nanyx` on its opening line. The conventions are in
/// docs/src/content/deep-dives/compilation.md#tour-examples.
type DocExampleKind =
    /// A whole program, which compiles.
    | Program
    /// Part of a program, such as code that uses names defined elsewhere, which is not compiled.
    | Fragment
    /// A program the compiler gets wrong, which is skipped until the issue is fixed.
    | Skip of issue: int

type DocExample =
    { Kind: DocExampleKind
      /// The block's code, with nothing added.
      Code: string }

let private docExampleKind (page: string) (line: int) (info: string) =
    match info.Split(' ', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray with
    | [ "nanyx" ] -> Program
    | [ "nanyx"; "fragment" ] -> Fragment
    | [ "nanyx"; tag ] when tag.StartsWith "skip=#" -> Skip(int (tag.Substring "skip=#".Length))
    | _ -> failwith $"{page}:{line}: a docs example's fence is ```nanyx, ```nanyx fragment or ```nanyx skip=#<issue>, not ```{info}"

/// The ```nanyx blocks of the page at `path`, by the line of their opening fence.
let docExamplesOf (path: string) : Map<int, DocExample> =
    let page = Path.GetFileName path
    let lines = File.ReadAllText(path).ReplaceLineEndings("\n").Split('\n')
    let rec collect (index: int) (found: (int * DocExample) list) =
        if index >= lines.Length then
            List.rev found
        else
            let fence = lines.[index].Trim()
            if fence.StartsWith "```nanyx" then
                let close = Array.FindIndex(lines, index + 1, fun line -> line.Trim().StartsWith "```")
                let line = index + 1
                let example = { Kind = docExampleKind page line (fence.Substring 3); Code = String.Join("\n", lines.[index + 1 .. close - 1]) + "\n" }
                collect (close + 1) ((line, example) :: found)
            else
                collect (index + 1) found
    collect 0 [] |> Map.ofList

let runE2E testName =
    runFile (Path.Combine(__SOURCE_DIRECTORY__, "E2E", testName, "input.nyx"))

/// `source`, saved as a script and run (see `runScriptWithOptions`).
let runSourceWithOptions (checkMemory: bool) (inlineCalls: bool) (standardInput: string) (source: string) : RunOutcome =
    // Named `main.nyx`, in a directory of its own, so that what `dbg` prints names it so.
    let directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"nanyx-run-{Guid.NewGuid():N}")).FullName
    let inputPath = Path.Combine(directory, "main.nyx")
    File.WriteAllText(inputPath, source.ReplaceLineEndings "\n")
    try runScriptWithOptions checkMemory inlineCalls standardInput inputPath
    finally Directory.Delete(directory, true)

let runSourceWith (checkMemory: bool) (standardInput: string) (source: string) : RunOutcome =
    runSourceWithOptions checkMemory true standardInput source

let runSourceWithInput (standardInput: string) (source: string) : RunOutcome = runSourceWith true standardInput source

let runSource (source: string) : RunOutcome = runSourceWithInput "" source

/// A program that prints `T` or `F` for each of `comparisons`, one per line, after the definitions in `definitions`.
/// The comparisons can use `s`, the string "aa" made at run time.
let comparing (definitions: string list) (comparisons: string list) =
    [ "def show = { b -> if b then \"T\" else \"F\" }"
      yield! definitions
      ""
      "@entry"
      "def main = {"
      "  def s = \"a\" ++ \"a\""
      yield! comparisons |> List.map (fun comparison -> $"  println(show({comparison}))")
      "}" ]
    |> String.concat "\n"
let assertRan (expectedOutput: string) (outcome: RunOutcome) =
    Assert.True((outcome.ExitCode = 0), $"Expected exit code 0, got {outcome.ExitCode}. stderr:\n{outcome.StandardError}")
    Assert.Equal(expectedOutput, outcome.StandardOutput)
    Assert.Equal("", outcome.StandardError)

/// Asserts that `outcome` ran to its end, writing `expectedOutput` to stdout and `expectedDbg`, what its `dbg`s printed,
/// to stderr.
let assertRanWithDbg (expectedOutput: string) (expectedDbg: string) (outcome: RunOutcome) =
    Assert.True((outcome.ExitCode = 0), $"Expected exit code 0, got {outcome.ExitCode}. stderr:\n{outcome.StandardError}")
    Assert.Equal(expectedDbg, outcome.StandardError)
    Assert.Equal(expectedOutput, outcome.StandardOutput)

/// `outcome` with what `dbg` wrote to stderr after what the program wrote to stdout, as its stdout, with the location
/// each `dbg` line starts with, `[main.nyx:12] `, left out: for a test that prints values with `dbg` and is about
/// something else. A test that reads both in the order they were written prints them all with one or the other.
let withoutDbgLocations (outcome: RunOutcome) =
    { outcome with
        StandardOutput = outcome.StandardOutput + Regex.Replace(outcome.StandardError, @"^\[main\.nyx:\d+\] ", "", RegexOptions.Multiline)
        StandardError = "" }

/// `stderr` without the stack a crash or a runtime error writes under its message (the lines that start with two
/// spaces): for a test of the message.
let withoutStack (stderr: string) =
    stderr.ReplaceLineEndings("\n").Split('\n') |> Array.filter (fun line -> not (line.StartsWith "  ")) |> String.concat "\n"

/// The options the in-process tests compile with: memory checks on, so that the counters are there to read.
let checkedOptions = { WasmOptions.Default with CheckMemory = true }

/// A program compiled with memory checks (see `WasmOptions.CheckMemory`), instantiated on Wasmtime with a WASI that
/// reads nothing and writes nowhere, and its top-level code run. The drop functions of some types are exported as
/// well, so that a test can call its functions, give up what they return, and see how far the heap has grown, how
/// often it allocated, and how many blocks are still in use.
type WasmProgram(engine: Wasmtime.Engine, store: Wasmtime.Store, instance: Wasmtime.Instance) =
    let memory = instance.GetMemory("memory")
    let counter (name: string) = instance.GetGlobal(name).GetValue() :?> int
    /// Calls the function `name` with `argument`, as the `i64` an int is held in where it takes an int, and gives what it
    /// returns: a pointer, or an int's low 32 bits.
    member _.Call(name: string, argument: int) : int =
        let func = instance.GetFunction name
        let value =
            if func.Parameters.[0] = Wasmtime.ValueKind.Int64 then Wasmtime.ValueBox.op_Implicit (int64 argument)
            else Wasmtime.ValueBox.op_Implicit argument
        match func.Invoke value with
        | :? int64 as result -> int result
        | result -> result :?> int
    member _.Drop(ty: Transpiler.Wasm.CoreIr.IrType, value: int) =
        instance.GetAction<int>(Transpiler.Wasm.CoreIrDrop.dropName ty).Invoke value
    member _.HeapPointer = counter CheckedExports.heapPointer
    member _.Allocations = counter CheckedExports.allocations
    member _.LiveBlocks = counter CheckedExports.liveBlocks
    /// The size of the memory in bytes, which never shrinks: how far the heap has ever reached, to the page.
    member _.MemoryBytes = memory.GetSize() * 65536L
    member _.Word(address: int) = memory.ReadInt32(int64 address)
    member _.Text(pointer: int) = memory.ReadString(int64 pointer, memory.ReadInt32(int64 (pointer - 4)))
    interface IDisposable with
        member _.Dispose() =
            store.Dispose()
            engine.Dispose()

/// How many blocks `name(argument)` allocates in `program`, and what it returns.
let allocationsOf (program: WasmProgram) (name: string) (argument: int) =
    let before = program.Allocations
    let result = program.Call(name, argument)
    program.Allocations - before, result

/// `wat`, a module compiled with `checkedOptions`, instantiated and run (see `WasmProgram`), with the drop functions of `dropped`
/// exported.
let instantiateWat (wat: string) (dropped: Transpiler.Wasm.CoreIr.IrType list) =
    let dropName = Transpiler.Wasm.CoreIrDrop.dropName
    let exports =
        dropped |> List.map (fun ty -> $"  (export \"{dropName ty}\" (func ${dropName ty}))") |> String.concat "\n"
    let engine = new Wasmtime.Engine()
    let store = new Wasmtime.Store(engine)
    store.SetWasiConfiguration(Wasmtime.WasiConfiguration())
    use module' = Wasmtime.Module.FromText(engine, "program", wat.Substring(0, wat.LastIndexOf ')') + exports + "\n)")
    use linker = new Wasmtime.Linker(engine)
    linker.DefineWasi()
    let instance = linker.Instantiate(store, module')
    match instance.GetAction CheckedExports.start with
    | null -> ()
    | start -> start.Invoke()
    new WasmProgram(engine, store, instance)

/// `source` compiled with memory checks, instantiated and run (see `instantiateWat`).
let instantiateWasm (source: string) (dropped: Transpiler.Wasm.CoreIr.IrType list) =
    instantiateWat (transpileWatWith checkedOptions source) dropped

/// The definitions a test host (see `hostWatWith`) writes to stdout with: `writeStdout`, over WASI's `fd_write`, with
/// `nanyx/host.memory` for its iovec. The host imports `nanyx/host.memory` and `nanyx/host.runtime`.
let hostWriteStdout =
    """@extern("wasi_snapshot_preview1", "fd_write")
def fdWrite: [HostRuntime] I32, I32, I32, I32 -> I32

def writeStdout: [HostRuntime] string -> () = { text ->
  def iovec = allocate(12)
  writeI32(iovec, addressOf(text))
  writeI32(iovec + 4, byteLength(text))
  ignore(fdWrite(1, iovec, 1, iovec + 8))
  keepAlive(text)
  free(iovec)
}
"""

/// `source` checked as the console host's file is (see `ShippedPackages`), so that it may require `HostRuntime`, and compiled
/// with `options`; any diagnostic fails.
let hostWatWith (options: WasmOptions) (source: string) =
    let consoleHostFile = Path.Combine(ShippedPackages.directoryOf Hosts.consolePackage, "src", "console.host.nyx")
    match Compiler.compileFrom Linters.none (SourceFile consoleHostFile) (normalized source) with
    | { Typed = Some typed } as result when result.Diagnostics.IsEmpty -> transpileTypedModuleWithOptions options typed |> fst
    | result -> failwith $"""Compile error: {result.Diagnostics |> List.map _.Message |> String.concat "\n"}"""

/// What the module `wat` writes to stdout when the CLI's runtime runs it with the command-line arguments `arguments`,
/// its name first, or why it did not run.
let runHost (arguments: string list) (wat: string) : Result<string, Nanyx.Runtime.RunError> =
    let input = Path.GetTempFileName()
    try
        use output = new MemoryStream()
        use error = new MemoryStream()
        Nanyx.Runtime.WasmRunner.run (Nanyx.Runtime.Redirected(input, output, error, [])) arguments [] wat
        |> Result.map (fun () -> Text.Encoding.UTF8.GetString(output.ToArray()))
    finally
        File.Delete input

/// `runHost` for a module compiled with `checkedOptions`: what it writes to stdout, and how many blocks it left
/// allocated.
let runCheckedHost (arguments: string list) (wat: string) : Result<string * int, Nanyx.Runtime.RunError> =
    let input = Path.GetTempFileName()
    try
        use output = new MemoryStream()
        use error = new MemoryStream()
        Nanyx.Runtime.WasmRunner.runReading (Nanyx.Runtime.Redirected(input, output, error, [])) arguments [] CheckedExports.start [| CheckedExports.liveBlocks |] wat
        |> Result.map (fun counters -> Text.Encoding.UTF8.GetString(output.ToArray()), counters.[0])
    finally
        File.Delete input

/// A port nothing is listening on, as the system gives it for a moment.
let freePort () =
    let listener = new Net.Sockets.TcpListener(Net.IPAddress.Loopback, 0)
    listener.Start()
    let port = (listener.LocalEndpoint :?> Net.IPEndPoint).Port
    listener.Stop()
    port

/// A server on a port of its own on this machine, for as long as it is held, that answers each request with what
/// `answer` makes of its method, path, headers and body: a status, headers and a body.
type TestServer(answer: string -> string -> (string * string) list -> string -> int * (string * string) list * string) =
    let port = freePort ()
    let listener = new Net.HttpListener()
    do
        listener.Prefixes.Add($"http://localhost:{port}/")
        listener.Start()
    let serving =
        Threading.Thread(
            (fun () ->
                try
                    while listener.IsListening do
                        let context = listener.GetContext()
                        let request = context.Request
                        let headers = [ for name in request.Headers.AllKeys -> name.ToLowerInvariant(), request.Headers.[name] ]
                        let body = (new StreamReader(request.InputStream)).ReadToEnd()
                        let status, answerHeaders, text = answer request.HttpMethod request.Url.AbsolutePath headers body
                        try
                            context.Response.StatusCode <- status
                            for name, value in answerHeaders do
                                context.Response.Headers.Add(name, value)
                            let bytes = Text.Encoding.UTF8.GetBytes(text: string)
                            context.Response.OutputStream.Write(bytes, 0, bytes.Length)
                            context.Response.Close()
                        with _ -> ()
                with _ -> ()),
            IsBackground = true)
    do serving.Start()

    /// Where it is: `http://localhost:<port>`.
    member _.Url = $"http://localhost:{port}"

    interface IDisposable with
        member _.Dispose() =
            listener.Stop()
            listener.Close()
