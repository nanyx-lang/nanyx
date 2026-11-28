module TranspilerWasmTestHelpers

open System
open System.IO
open System.Diagnostics
open System.Text.RegularExpressions
open NyxCompiler
open Parser.Program
open Transpiler.Wasm.CodeGen
open Xunit

/// The tests run with a package cache of their own (see `TestPackageCache`).
[<assembly: AssemblyFixture(typeof<Nanyx.TestPackageCache.Cache>)>]
do ()

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

/// The packages from the index that the tests' programs and the docs' examples use, so that no test needs the network:
/// the packages the compiler ships, seeded into the store (see `SeededPackages`), and, though the compiler doesn't ship
/// them, `nanyx.json` 0.1.0, 0.2.0, 0.4.2, 0.5.0 and 0.5.1, and `nanyx.regex` 0.1.1 and 0.1.2, the `.nyxpkg`s their releases on nanyx-lang/json
/// and nanyx-lang/regex carry (`Packages/<package>-<version>.nyxpkg`, downloaded from the
/// releases, whose SHA-256s the index records). They are listed in an index on disk, in a directory of its own, and
/// downloaded into the store's cache once, before any test reads them, so that tests run in parallel never unpack one at
/// the same time.
let private packagesForTests =
    lazy
        (let directory = Path.Combine(Path.GetTempPath(), $"nanyx-test-index-{Guid.NewGuid():N}")
         let store =
             { Cache = Path.Combine(directory, "cache")
               Index = Path.Combine(directory, "index")
               GitHub = GitHub.cached (Path.Combine(directory, "cache")); Seed = SeededPackages.seed }
         Directory.CreateDirectory(Path.Combine(store.Index, "artifacts")) |> ignore
         Directory.CreateDirectory(Path.Combine(store.Index, "index", "nanyx")) |> ignore
         File.WriteAllText(Path.Combine(store.Index, "config.json"), """{ "format": 1, "index": "index/" }""")
         for name, versions in [ "nanyx.json", [ "0.1.0"; "0.2.0"; "0.4.2"; "0.5.0"; "0.5.1" ]; "nanyx.regex", [ "0.1.1"; "0.1.2" ] ] do
             let versions =
                 versions
                 |> List.map (fun version ->
                     let file = $"{name}-{version}.nyxpkg"
                     let bytes = File.ReadAllBytes(Path.Combine(__SOURCE_DIRECTORY__, "Packages", file))
                     File.WriteAllBytes(Path.Combine(store.Index, "artifacts", file), bytes)
                     version, PackageArchive.sha256 bytes)
             let entries =
                 versions
                 |> List.map (fun (version, sha256) ->
                     $$"""{ "version": "{{version}}", "url": "../../artifacts/{{name}}-{{version}}.nyxpkg", "sha256": "{{sha256}}", "dependencies": {}, "unlisted": false, "available": true }""")
             File.WriteAllText(
                 Path.Combine(store.Index, "index", "nanyx", $"{name}.json"),
                 $$"""{ "name": "{{name}}", "versions": [{{String.concat ", " entries}}] }"""
             )
             for version, sha256 in versions do
                 let locked = { Name = name; Version = (SemanticVersion.tryParse version).Value; Sha256 = sha256; Dependencies = [] }
                 match PackageIndex.download store locked with
                 | Result.Ok _ -> ()
                 | Result.Error message -> failwith message
         match SeededPackages.seed store with
         | Result.Ok _ -> ()
         | Result.Error errors -> failwith (String.concat "\n" errors)
         store)

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
    | [ "parse"; inputFile ] -> fun shell -> CliCommands.ParseIn(shell, inputFile)
    | "new" :: name :: directory ->fun shell -> CliCommands.NewIn(shell, name, Array.ofList directory)
    | "manifest" :: rest ->
        let json, rest = takeFlag "--json" rest
        fun shell -> CliCommands.ManifestIn(shell, json, Array.ofList rest)
    | "check" :: rest ->
        let _, _, timings, host, path = options (null, false, false, null, []) rest
        fun shell -> CliCommands.CheckIn(shell, path, timings, host)
    | "run" :: rest ->
        let noInline, rest = takeFlag "--no-inline" rest
        let yieldAtEveryWait, rest = takeFlag "--yield-at-every-wait" rest
        let filesInMemory, rest = takeFlag "--files-in-memory" rest
        let showValues, rest = takeFlag "--show-values" rest
        let debug, rest = takeFlag "--debug" rest
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
        let dbgTo, rest =
            match rest with
            | "--dbg-to" :: file :: rest -> file, rest
            | _ -> null, rest
        let _, _, timings, host, path = options (null, false, false, null, []) rest
        fun shell -> CliCommands.RunIn(shell, memoryCheck, path, timings, host, not noInline, Array.ofList allowed, profile, yieldAtEveryWait, dbgTo, filesInMemory, showValues, debug)
    | "build" :: rest ->
        let noInline, rest = takeFlag "--no-inline" rest
        let wat, rest = takeFlag "--wat" rest
        let locked, rest = takeFlag "--locked" rest
        let core, rest = takeFlag "--core" rest
        let outputPath, release, timings, host, path = options (null, false, false, null, []) rest
        fun shell -> CliCommands.BuildIn(shell, outputPath, release, path, timings, host, not noInline, (if wat then ProgramFormat.Text else ProgramFormat.Binary), locked, core)
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
    | "test" :: rest ->
        // Each `--<name> <value>` among `named` before any `--`, and the arguments without them.
        let rec valued (named: string list) (found: Map<string, string>) (kept: string list) (rest: string list) =
            match rest with
            | "--" :: _ -> found, List.rev kept @ rest
            | name :: value :: rest when List.contains name named -> valued named (Map.add name value found) kept rest
            | argument :: rest -> valued named found (argument :: kept) rest
            | [] -> found, List.rev kept
        let given, rest = valued [ "--filter"; "--seed"; "--timeout"; "--format"; "--test"; "--module" ] Map.empty [] rest
        let value name = given |> Map.tryFind name |> Option.toObj
        let list, rest = takeFlag "--list" rest
        let filesInMemory, rest = takeFlag "--files-in-memory" rest
        let _, release, timings, _, path = options (null, false, false, null, []) rest
        fun shell ->
            CliCommands.TestIn(shell, path, value "--filter", value "--seed", value "--timeout", release, timings, value "--format", list, value "--test", value "--module", filesInMemory)
    | "pull" :: path -> fun shell -> CliCommands.PullIn(shell, Array.ofList path)
    | "bump" :: path -> fun shell -> CliCommands.BumpIn(shell, Array.ofList path)
    | [ "api-check"; older; newer ] -> fun shell -> CliCommands.ApiCheckIn(shell, older, newer)
    | "publish" :: path -> fun shell -> CliCommands.PublishIn(shell, Array.ofList path)
    | [ "unlist"; package; version ] -> fun shell -> CliCommands.UnlistIn(shell, package, version)
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

/// The outcome of `start`, a process of `nanyx`, given `arguments` and run in `workingDirectory`.
let private runNanyx (start: ProcessStartInfo) (workingDirectory: string) (arguments: string list) : RunOutcome =
    start.RedirectStandardOutput <- true
    start.RedirectStandardError <- true
    start.RedirectStandardInput <- true
    start.WorkingDirectory <- workingDirectory
    for argument in arguments do
        start.ArgumentList.Add argument
    use run = Process.Start start
    run.StandardInput.Close()
    let error = run.StandardError.ReadToEndAsync()
    let output = run.StandardOutput.ReadToEnd()
    run.WaitForExit()
    { ExitCode = run.ExitCode
      StandardOutput = output
      StandardError = error.Result }

/// The `dotnet` that runs the tests, started on the CLI built in `cliDirectory`, `nanyx.dll`.
let private dotnetNanyx (cliDirectory: string) =
    let host = Environment.GetEnvironmentVariable "DOTNET_HOST_PATH" |> Option.ofObj |> Option.defaultValue "dotnet"
    let start = ProcessStartInfo(host)
    start.ArgumentList.Add(Path.Combine(cliDirectory, "nanyx.dll"))
    start

/// Runs `nanyx` with `arguments` in a process of its own: the CLI built beside the tests (`nanyx.dll`), started by the
/// `dotnet` that runs them. For a command that measures the run, such as `run --profile`, which this process's
/// collections of garbage would disturb: they stop its managed threads, though not the WebAssembly the run is executing
/// (#2491).
let nanyxProcess (arguments: string list) : RunOutcome =
    runNanyx (dotnetNanyx AppContext.BaseDirectory) (Directory.GetCurrentDirectory()) arguments

/// Runs `nanyx` with `arguments` in `workingDirectory`, started as a user starts it: `nanyx.exe` in `cliDirectory`, a
/// spelling of the directory the CLI is built in, where there is one, and `nanyx.dll` there otherwise.
let nanyxExecutableFrom (cliDirectory: string) (workingDirectory: string) (arguments: string list) : RunOutcome =
    let executable = Path.Combine(cliDirectory, "nanyx.exe")
    let start = if OperatingSystem.IsWindows() && File.Exists executable then ProcessStartInfo(executable) else dotnetNanyx cliDirectory
    runNanyx start workingDirectory arguments

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

/// `nanyxInStore`, with `github` in place of GitHub: what `nanyx publish` and `nanyx unlist` send their requests to.
let nanyxWithGitHub (store: NyxCompiler.PackageStore) (github: NyxCompiler.GitHubClient) (directory: string) (arguments: string list) : RunOutcome =
    inProcessWith (Some store) (Some github) Threading.CancellationToken.None directory "" [] (command arguments)

/// Pulls the dependencies of the package in `packageDirectory` and packs it with `nanyx pack` into the `artifacts` directory of the index of `store`, a
/// directory on disk, and lists it in the index after the versions listed already, as a release would, with
/// `dependencies` as the index writes them and `fields`, more of its entry's JSON, such as `, "unlisted": true`. Gives
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

/// `nanyx run --memory` with `options` on a script of `source`, which writes its memory report to stderr after the
/// program's own.
let runWithMemoryReportUsing (options: string list) (source: string) : RunOutcome =
    use directory = new TempDirectory()
    directory.Write "main.nyx" source
    nanyxWithInput "" ([ "run"; "--memory" ] @ options @ [ Path.Combine(directory.Path, "main.nyx") ])

/// `nanyx run --memory` on a script of `source`, which writes its memory report to stderr after the program's own.
let runWithMemoryReport (source: string) : RunOutcome = runWithMemoryReportUsing [] source

/// The number the memory report in `outcome`'s stderr gives on the line of `measure`, such as `"peak live bytes"`.
let reportedMemory (measure: string) (outcome: RunOutcome) : int64 =
    let found = Regex.Match(outcome.StandardError, $@"{Regex.Escape measure} +([\d,]+)")
    Assert.True(found.Success, $"no '{measure}' in:\n{outcome.StandardError}")
    int64 (found.Groups.[1].Value.Replace(",", ""))

/// A store whose cache and index are directories in `directory`, which can't fetch from GitHub.
let storeIn (directory: TempDirectory) : NyxCompiler.PackageStore =
    { Cache = Path.Combine(directory.Path, "cache"); Index = Path.Combine(directory.Path, "index"); GitHub = NyxCompiler.GitHub.unsupported; Seed = SeededPackages.seed }

/// The library acme.core at `version`, whose `answer` is `answer`, in a directory of its own, `core-<version>` in
/// `directory`, published to the index of `store` with `fields` in its index entry (see `publishToIndex`).
let publishCore (directory: TempDirectory) (store: NyxCompiler.PackageStore) (version: string) (answer: int) (fields: string) =
    directory.Write $"core-{version}/core.nyxp" $"(package = (name = \"acme.core\", version = \"{version}\"))\n"
    directory.Write $"core-{version}/core.nyx" $"export module core\n\nexport def answer = {answer}\n"
    publishToIndex store (Path.Combine(directory.Path, $"core-{version}")) [] fields |> ignore

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

/// `nanyxOnPackageWith` an application that depends on the console host's interface package, `nanyx.cli`, so
/// that its files can import `nanyx.cli/console`.
let nanyxOnPackageWithConsoleCore (files: (string * string) list) (arguments: string list) : RunOutcome * bool =
    nanyxOnPackageWith $"[(package = \"nanyx.cli\", version = \"{Toolchain.version}\")]" files arguments

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
/// docs/src/content/deep-dives/compilation.md#docs-examples.
type DocExampleKind =
    /// A whole program, which compiles.
    | Program
    /// Part of a program, such as code that uses names defined elsewhere, which is not compiled.
    | Fragment
    /// A program the compiler gets wrong, which is skipped until the issue is fixed.
    | Skip of issue: int
    /// Code shown as it was before formatting (the Formatter page's before-and-after examples), which is not compiled.
    | Unformatted

type DocExample =
    { Kind: DocExampleKind
      /// The block's code, with nothing added.
      Code: string }

let private docExampleKind (page: string) (line: int) (info: string) =
    match info.Split(' ', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray with
    | [ "nanyx" ] -> Program
    | [ "nanyx"; "fragment" ] -> Fragment
    | [ "nanyx"; "unformatted" ] -> Unformatted
    | [ "nanyx"; tag ] when tag.StartsWith "skip=#" -> Skip(int (tag.Substring "skip=#".Length))
    | _ -> failwith $"{page}:{line}: a docs example's fence is ```nanyx, ```nanyx fragment, ```nanyx skip=#<issue> or ```nanyx unformatted, not ```{info}"

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

/// `source` compiled once, as `nanyx run` compiles a script, and run as it would run it, with inlining and without it:
/// what each run gives, inlined first. Compiling is most of what a run takes, so this takes about half what running it
/// both ways with `runSourceWithOptions` does (#2553).
let runSourceBothWays (source: string) : RunOutcome * RunOutcome =
    let directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"nanyx-run-{Guid.NewGuid():N}")).FullName
    let inputPath = Path.Combine(directory, "main.nyx")
    File.WriteAllText(inputPath, source.ReplaceLineEndings "\n")
    try
        let mutable compiled = None
        let compiling =
            inProcess (Directory.GetCurrentDirectory()) "" [] (fun shell ->
                if CliCommands.TryPullScript(shell, inputPath) then
                    compiled <- Some(CliCommands.CompileScript(shell, inputPath, false))
                    0
                else 1)
        match compiled with
        | Some result ->
            let run (inlineCalls: bool) =
                inProcess (Directory.GetCurrentDirectory()) "" [] (fun shell ->
                    CliCommands.RunCompiledScript(shell, inputPath, result, MemoryCheck.Off, false, null, inlineCalls))
            run true, run false
        | None -> compiling, compiling
    finally Directory.Delete(directory, true)

/// The script at `inputPath` compiled as `nanyx run` compiles it, its packages pulled first, and written with `options`,
/// on a large stack as `nanyx run` writes it: the WebAssembly text.
let transpileScriptWith (options: WasmOptions) (inputPath: string) : string =
    let mutable compiled = None
    let compiling =
        inProcess (Path.GetDirectoryName inputPath) "" [] (fun shell ->
            if CliCommands.TryPullScript(shell, inputPath) then
                compiled <- Some(CliCommands.CompileScript(shell, inputPath, false))
                0
            else 1)
    match compiled |> Option.bind (fun result -> result.Typed) with
    | Some typed -> Nanyx.LargeStack.run (fun () -> transpileTypedModuleWithOptions options typed |> fst)
    | None -> failwith $"{inputPath} didn't compile:\n{compiling.StandardError}"

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

/// `assertRan` for `expected` written in a test's source, whose line breaks are a checkout's: they are made `\n`.
let assertPrinted (expected: string) (outcome: RunOutcome) = assertRan (expected.ReplaceLineEndings "\n") outcome

/// The calls `calls`, each a call in Nanyx and the `float` it should give, run in one program that imports `nanyx/math`
/// as `math`: each value it prints must be the expected one when that is NaN, zero or infinite, and otherwise as near
/// it as `close expected actual` asks.
let assertPrintsFloats (close: float -> float -> bool) (calls: (string * float) list) =
    let source =
        "import nanyx/math as math\n\n" + (calls |> List.map (fun (call, _) -> $"println({call})") |> String.concat "\n") + "\n"
    let outcome = runSource source
    Assert.True((outcome.ExitCode = 0), outcome.StandardError)
    let printed = outcome.StandardOutput.ReplaceLineEndings("\n").TrimEnd().Split('\n')
    Assert.Equal(calls.Length, printed.Length)
    for (call, expected), line in List.zip calls (List.ofArray printed) do
        let actual =
            match line with
            | "NaN" -> nan
            | "Infinity" -> infinity
            | "-Infinity" -> -infinity
            | _ -> Double.Parse(line, Globalization.CultureInfo.InvariantCulture)
        let near =
            if Double.IsNaN expected then Double.IsNaN actual
            elif expected = 0.0 || Double.IsInfinity expected then actual = expected
            else close expected actual
        Assert.True(near, $"{call} is {line}, but should be {expected:R}")

/// The calls `calls`, each a call in Nanyx and the correctly rounded value of it, run in one program: each value it prints
/// must be within one unit in the last place of that value, or exactly it when that is NaN, zero or infinite.
let assertWithinUlp (calls: (string * float) list) =
    assertPrintsFloats (fun expected actual -> abs (actual - expected) <= Math.BitIncrement(abs expected) - abs expected) calls

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

/// `x` as a Nanyx float literal, with all the digits it takes to be read back as `x`: `1.0e-300`, not .NET's `1E-300`.
let floatLiteral (x: float) =
    let text = x.ToString("R", Globalization.CultureInfo.InvariantCulture).Replace("E+", "e").Replace("E", "e")
    let mantissa, exponent =
        match text.IndexOf 'e' with
        | -1 -> text, ""
        | at -> text.Substring(0, at), text.Substring at
    (if mantissa.Contains '.' then mantissa else mantissa + ".0") + exponent

/// The options the in-process tests compile with: memory checks on, so that the counters are there to read.
let checkedOptions = { WasmOptions.Default with CheckMemory = true }

/// A program compiled with memory checks (see `WasmOptions.CheckMemory`), instantiated on Wasmtime with a WASI that
/// reads nothing and writes nowhere, and its top-level code run. The drop functions of some types are exported as
/// well, so that a test can call its functions, give up what they return, and see how far the heap has grown, how
/// often it allocated, how many blocks are still in use, and how much work a call did (see `Work`).
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
    /// How much work calling the function `name` with `argument` does: the fuel it uses, which is about one for each
    /// instruction it runs, the same however busy the machine is. A test that compares two calls' costs compares
    /// these, not the time they take. A `memory.copy` costs one whatever its length, and where in memory the data is
    /// costs nothing, so a cost that is in those is timed instead (see `Timing`).
    member this.Work(name: string, argument: int) : uint64 =
        let before = store.Fuel
        this.Call(name, argument) |> ignore
        before - store.Fuel
    /// Calls the function `name`, which takes an `i32` and returns nothing, with `argument`.
    member _.Run(name: string, argument: int) = instance.GetAction<int>(name).Invoke argument
    member _.Drop(ty: Transpiler.Wasm.CoreIr.IrType, value: int) =
        instance.GetAction<int>(Transpiler.Wasm.CoreIrDrop.dropName ty).Invoke value
    member _.HeapPointer = counter CheckedExports.heapPointer
    member _.Allocations = counter CheckedExports.allocations
    member _.LiveBlocks = counter CheckedExports.liveBlocks
    /// The value of the global `name`, which the module exports, as an `i32`.
    member _.Global(name: string) = counter name
    member _.SetGlobal(name: string, value: int) = instance.GetGlobal(name).SetValue(value)
    /// The most bytes there have been in blocks in use at once.
    member _.PeakLiveBytes = counter CheckedExports.peakLiveBytes
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

/// Asserts that `name(n)` in `program` takes time that grows with `n`, not with its square: less than ten times what
/// `name(n / 4)` takes. A linear function takes about 4 times as long, and a quadratic one 16. When the first runs miss,
/// each size runs again in turn, and each one's quickest run counts, so that load on the machine, which slows one run
/// and not the other, can't make the test fail (#2388). It is a timing test (see `TestTiming`): where the cost is in
/// the instructions run, rather than in copying or in where the data is, compare `WasmProgram.Work` instead.
let callsInLinearTime (program: WasmProgram) (name: string) (n: int) =
    let time (size: int) = Nanyx.TestTiming.millisecondsOf (fun () -> program.Call(name, size) |> ignore)
    program.Call(name, n / 4) |> ignore
    let isLinear (atQuarter: float) (atN: float) = atN < 10.0 * atQuarter
    let atQuarter, atN = time (n / 4), time n
    let atQuarter, atN =
        if isLinear atQuarter atN then atQuarter, atN
        else
            let again = List.init 3 (fun _ -> time (n / 4), time n)
            min atQuarter (again |> List.map fst |> List.min), min atN (again |> List.map snd |> List.min)
    Assert.True(isLinear atQuarter atN, $"{name}({n:N0}) took {atN:F1} ms, {atN / atQuarter:F1} times the {atQuarter:F1} ms of {name}({n / 4:N0})")

/// `wat`, a module compiled with `checkedOptions`, instantiated and run (see `WasmProgram`), with the drop functions of `dropped`
/// exported.
let instantiateWat (wat: string) (dropped: Transpiler.Wasm.CoreIr.IrType list) =
    let dropName = Transpiler.Wasm.CoreIrDrop.dropName
    let exports =
        dropped |> List.map (fun ty -> $"  (export \"{dropName ty}\" (func ${dropName ty}))") |> String.concat "\n"
    // With fuel, so that `WasmProgram.Work` can count what a call does: more than any test uses.
    let engine = new Wasmtime.Engine((new Wasmtime.Config()).WithFuelConsumption true)
    let store = new Wasmtime.Store(engine)
    store.Fuel <- System.UInt64.MaxValue / 2UL
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
    let consoleHostFile = Path.Combine(ShippedPackages.directoryOf Hosts.consolePackage, "src", "cli.host.nyx")
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

/// The options of a debug build with probes (see `WasmOptions.Probes`), which only code that listens to them asks for:
/// the debugger, when it attaches, and a test. `nanyx run` compiles without them (#2225).
let probedOptions = { WasmOptions.Default with Places = true; Probes = true; CrashToRuntime = true }

/// `source`, saved as `main.nyx`, compiled with `probedOptions`, on a large stack as `nanyx run` compiles, and run by
/// the runtime, with nothing listening to its probes: what it writes to stdout.
let runProbed (source: string) : Result<string, Nanyx.Runtime.RunError> =
    use directory = new TempDirectory()
    let path = Path.Combine(directory.Path, "main.nyx")
    File.WriteAllText(path, source.ReplaceLineEndings "\n")
    let wat = Nanyx.LargeStack.run (fun () -> transpileWatFileWith probedOptions path)
    runHost [ "main" ] wat

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
/// `answer` makes of its method, path, headers and body: a status, headers and a body. It answers each on a thread of
/// its own, so `answer` may wait for another request.
type TestServer(answer: string -> string -> (string * string) list -> string -> int * (string * string) list * string) =
    let port = freePort ()
    let listener = new Net.HttpListener()
    do
        listener.Prefixes.Add($"http://localhost:{port}/")
        listener.Start()
    /// Answers the request `context` holds, and closes it.
    let respond (context: Net.HttpListenerContext) =
        try
            let request = context.Request
            let headers = [ for name in request.Headers.AllKeys -> name.ToLowerInvariant(), request.Headers.[name] ]
            let body = (new StreamReader(request.InputStream)).ReadToEnd()
            let status, answerHeaders, text = answer request.HttpMethod request.Url.AbsolutePath headers body
            context.Response.StatusCode <- status
            for name, value in answerHeaders do
                context.Response.Headers.Add(name, value)
            let bytes = Text.Encoding.UTF8.GetBytes(text: string)
            context.Response.OutputStream.Write(bytes, 0, bytes.Length)
            context.Response.Close()
        with _ -> ()
    let serving =
        Threading.Thread(
            (fun () ->
                try
                    while listener.IsListening do
                        let context = listener.GetContext()
                        // Each request is answered on a thread of its own, so that several can be in flight at once,
                        // and not on the thread pool: an answer a test holds back, as a slow server's is, keeps its
                        // thread, and on a loaded machine the pool can be slow to give another request one (#2878).
                        Threading.Thread((fun () -> respond context), IsBackground = true).Start()
                with _ -> ()),
            IsBackground = true)
    do serving.Start()

    /// Where it is: `http://localhost:<port>`.
    member _.Url = $"http://localhost:{port}"

    interface IDisposable with
        member _.Dispose() =
            listener.Stop()
            listener.Close()

/// The core module `wat`, of a component, instantiated on Wasmtime with each of its imports the function `implement`
/// gives for it, or else a function that traps.
let instantiateCoreWith (implement: Wasmtime.FunctionImport -> Wasmtime.Function.UntypedCallbackDelegate option) (wat: string) =
    let engine = new Wasmtime.Engine()
    let store = new Wasmtime.Store(engine)
    use module' = Wasmtime.Module.FromText(engine, "component", wat)
    use linker = new Wasmtime.Linker(engine)
    // A module may import a function twice, as one whose exports run under the host's contexts does.
    linker.AllowShadowing <- true
    for import in module'.Imports do
        match import with
        | :? Wasmtime.FunctionImport as func ->
            let trap = Wasmtime.Function.UntypedCallbackDelegate(fun _ _ _ -> failwith $"{func.ModuleName} {func.Name} was called")
            let callback = implement func |> Option.defaultValue trap
            linker.Define(func.ModuleName, func.Name, Wasmtime.Function.FromCallback(store, callback, func.Parameters, func.Results))
        | _ -> ()
    engine, store, linker.Instantiate(store, module')

/// The core module `wat`, of a component, instantiated on Wasmtime with each of its imports a function that traps.
let instantiateCore (wat: string) = instantiateCoreWith (fun _ -> None) wat

/// The core module of the component `nanyx build` writes for a program under the host of the manifest entry `hostEntry`
/// whose `main.nyx` is `main`, which may import `nanyx.http`.
let private componentCore (hostEntry: string) (main: string) =
    use directory = new TempDirectory()
    Directory.CreateDirectory(Path.Combine(directory.Path, "src")) |> ignore
    let dependencies = $"""[(package = "nanyx.http", version = "{Toolchain.version}")]"""
    let manifest = $"""(package = (name = "app", version = "0.1.0", sources = ["src/**/*.nyx"]), dependencies = {dependencies}, {hostEntry})"""
    File.WriteAllText(Path.Combine(directory.Path, "app.nyxp"), manifest + "\n")
    File.WriteAllText(Path.Combine(directory.Path, "src", "main.nyx"), main)
    let built = nanyxIn directory.Path [ "build"; "--release"; "--wat" ]
    Assert.Equal("", built.StandardError)
    Assert.Equal(0, built.ExitCode)
    File.ReadAllText(Path.Combine(directory.Path, "out", "app.wat"))

/// The core module of the component `nanyx build` writes for a console program whose `main.nyx` is `main`.
let consoleComponentCore (main: string) = componentCore consoleHostEntry main

/// The core module of the component `nanyx build` writes for a handler under the HTTP host whose `main.nyx` is `main`.
let httpComponentCore (main: string) =
    componentCore $"host = (package = \"nanyx.http.host\", version = \"{Toolchain.version}\")" main

/// What a fake host's waitable gives when its time comes: the event's code and its value.
type private FakeWaitable = { Deadline: int64; Event: int; Value: int }

/// A host of a console component's imports, enough of them for the tests: `wasi:clocks/monotonic-clock`, the waitable
/// built-ins, and a `wasi:http` client whose requests' bodies the host takes 50 ms after each write and which answers
/// every request with an error 100 ms after it is sent. Its clock moves only when the module waits: a wait for a set
/// takes the waitable in it whose time comes first, and moves the clock to that time. It traps where the canonical
/// ABI does. It runs `run` as a host runs one lifted with the callback ABI (`Run`): while `run` or its callback returns
/// WAIT, it gives the callback the next event of the set. For a handler (`Handle`), it gives `handle` a `GET` of `/` with
/// no headers and no body, and keeps the body of the response it returns.
type FakeWasiHost() =
    // The canonical ABI's codes: a subtask's states, the events, and what an async built-in gives when it blocks.
    let started = 1
    let returned = 2
    let cancelledBeforeReturned = 4
    let subtaskEvent = 1
    let streamWriteEvent = 3
    let taskCancelledEvent = 6
    let blocked = -1
    let exit = 0
    let wait = 2
    let mutable now = 0L
    let mutable nextHandle = 1
    let waitables = Collections.Generic.Dictionary<int, FakeWaitable>()
    let joined = Collections.Generic.Dictionary<int, int>()
    let delivered = Collections.Generic.HashSet<int>()

    let handle () =
        let made = nextHandle
        nextHandle <- nextHandle + 1
        made

    /// A subtask that returns `after` nanoseconds from now, and the status an async call that started it gives.
    let subtask (after: int64) =
        let made = handle ()
        waitables.[made] <- { Deadline = now + after; Event = subtaskEvent; Value = returned }
        (made <<< 4) ||| started

    /// The two ends of a new stream or future, as one `i64`: the readable end, then the writable end.
    let ends () =
        let readable = handle ()
        let writable = handle ()
        int64 readable ||| (int64 writable <<< 32)

    let isIn (set: int) (waitable: int) =
        match joined.TryGetValue waitable with
        | true, joinedTo -> joinedTo = set
        | _ -> false

    /// The waitable whose time comes first in `set`, its event delivered.
    let nextEvent (set: int) =
        let ready =
            waitables
            |> Seq.filter (fun pair -> isIn set pair.Key && not (delivered.Contains pair.Key))
            |> Seq.sortBy (fun pair -> pair.Value.Deadline)
            |> Seq.tryHead
        match ready with
        | None -> failwith "a wait on a set with nothing to wait for"
        | Some pair ->
            now <- max now pair.Value.Deadline
            delivered.Add pair.Key |> ignore
            pair.Key, pair.Value

    /// The durations, in nanoseconds, of each `wait-for`, by the subtask it started.
    member val Waits = Collections.Generic.Dictionary<int, int64>()
    /// When each `wait-for` was started, in nanoseconds, by its subtask.
    member val WaitStarts = Collections.Generic.Dictionary<int, int64>()
    /// The subtasks cancelled, and those dropped.
    member val Cancelled = Collections.Generic.List<int>()
    member val Dropped = Collections.Generic.List<int>()
    /// The writable ends of the requests' bodies.
    member val BodyWriters = Collections.Generic.List<int>()
    /// Each waitable whose event was delivered, in order.
    member val Delivered = Collections.Generic.List<int>()
    /// How many times the module blocked in `[waitable-set-wait]`, rather than returning WAIT.
    member val BlockingWaits = 0 with get, set
    /// What `run` gave with `task.return`, if it has.
    member val Returned: int option = None with get, set
    /// How many times `run` returned WAIT, or its callback did.
    member val Waited = 0 with get, set
    /// The values each export's `task.return` was given, by the export's name, `[task-return]` and its function's.
    member val TaskReturns = Collections.Generic.Dictionary<string, Wasmtime.ValueBox list>()
    /// The text of each `task.return` given a string, as an address and a length, read when it was called.
    member val TaskReturnTexts = Collections.Generic.Dictionary<string, string>()
    /// The response `handle` returned, if it has, and its body, as written.
    member val Response: int option = None with get, set
    member val ResponseBody = Text.StringBuilder()
    member _.Now = now

    member host.Implement(import: Wasmtime.FunctionImport) : Wasmtime.Function.UntypedCallbackDelegate option =
        match import.ModuleName, import.Name with
        | "wasi:clocks/monotonic-clock@0.3.0", "now" ->
            Some(Wasmtime.Function.UntypedCallbackDelegate(fun _ _ results -> results.[0] <- Wasmtime.ValueBox.op_Implicit now))
        | "wasi:clocks/monotonic-clock@0.3.0", "[async-lower]wait-for" ->
            Some(Wasmtime.Function.UntypedCallbackDelegate(fun _ arguments results ->
                let duration = arguments.[0].AsInt64()
                let status = subtask duration
                host.Waits.[status >>> 4] <- duration
                host.WaitStarts.[status >>> 4] <- now
                results.[0] <- Wasmtime.ValueBox.op_Implicit status))
        | "$root", "[waitable-set-new]" ->
            Some(Wasmtime.Function.UntypedCallbackDelegate(fun _ _ results -> results.[0] <- Wasmtime.ValueBox.op_Implicit (handle ())))
        | "$root", "[waitable-join]" ->
            Some(Wasmtime.Function.UntypedCallbackDelegate(fun _ arguments _ ->
                let waitable = arguments.[0].AsInt32()
                match arguments.[1].AsInt32() with
                | 0 -> joined.Remove waitable |> ignore
                | set -> joined.[waitable] <- set))
        | "$root", "[waitable-set-wait]" ->
            Some(Wasmtime.Function.UntypedCallbackDelegate(fun caller arguments results ->
                host.BlockingWaits <- host.BlockingWaits + 1
                let waitable, ready = nextEvent (arguments.[0].AsInt32())
                host.Delivered.Add waitable
                let memory = caller.GetMemory("memory")
                let address = int64 (arguments.[1].AsInt32())
                memory.WriteInt32(address, waitable)
                memory.WriteInt32(address + 4L, ready.Value)
                results.[0] <- Wasmtime.ValueBox.op_Implicit ready.Event))
        | "$root", "[waitable-set-drop]" -> Some(Wasmtime.Function.UntypedCallbackDelegate(fun _ _ _ -> ()))
        | "$root", "[subtask-cancel]" ->
            Some(Wasmtime.Function.UntypedCallbackDelegate(fun _ arguments results ->
                let subtask = arguments.[0].AsInt32()
                if joined.ContainsKey subtask then failwith $"subtask {subtask} cancelled while in a waitable set"
                if delivered.Contains subtask then failwith $"subtask {subtask} cancelled after its return was delivered"
                host.Cancelled.Add subtask
                delivered.Add subtask |> ignore
                results.[0] <- Wasmtime.ValueBox.op_Implicit cancelledBeforeReturned))
        | "$root", "[subtask-drop]" -> Some(Wasmtime.Function.UntypedCallbackDelegate(fun _ arguments _ -> host.Dropped.Add(arguments.[0].AsInt32())))
        | "[export]wasi:http/handler@0.3.0", "[task-return]handle" ->
            Some(Wasmtime.Function.UntypedCallbackDelegate(fun _ arguments _ ->
                if host.Response.IsSome then failwith "task.return called twice"
                Assert.Equal(0, arguments.[0].AsInt32())
                host.Response <- Some(arguments.[1].AsInt32())))
        | "$root", ("[backpressure-inc]" | "[backpressure-dec]") -> Some(Wasmtime.Function.UntypedCallbackDelegate(fun _ _ _ -> ()))
        | interfaceName, name when interfaceName.StartsWith "[export]" && interfaceName <> "[export]wasi:cli/run@0.3.0" && interfaceName <> "[export]wasi:http/handler@0.3.0" && name.StartsWith "[task-return]" ->
            Some(Wasmtime.Function.UntypedCallbackDelegate(fun caller arguments _ ->
                if host.TaskReturns.ContainsKey name then failwith $"{name} called twice"
                if arguments.Length = 2 then
                    let memory = caller.GetMemory("memory")
                    let address = int64 (arguments.[0].AsInt32())
                    let length = arguments.[1].AsInt32()
                    host.TaskReturnTexts.[name] <- Text.Encoding.UTF8.GetString(Array.init length (fun index -> memory.ReadByte(address + int64 index)))
                let given = ResizeArray<Wasmtime.ValueBox>()
                for index in 0 .. arguments.Length - 1 do
                    given.Add arguments.[index]
                host.TaskReturns.[name] <- List.ofSeq given))
        | "[export]wasi:cli/run@0.3.0", "[task-return]run" ->
            Some(Wasmtime.Function.UntypedCallbackDelegate(fun _ arguments _ ->
                if host.Returned.IsSome then failwith "task.return called twice"
                host.Returned <- Some(arguments.[0].AsInt32())))
        | ("wasi:http/types@0.3.0" | "wasi:http/client@0.3.0"), name ->
            Some(Wasmtime.Function.UntypedCallbackDelegate(fun caller arguments results ->
                let memory = caller.GetMemory("memory")
                for index in 0 .. results.Length - 1 do
                    results.[index] <- Wasmtime.ValueBox.op_Implicit 0
                match name with
                | "[constructor]fields"
                | "[constructor]request-options" -> results.[0] <- Wasmtime.ValueBox.op_Implicit (handle ())
                // A result the host writes in memory, at the address given last, is `ok`, its case 0.
                | "[method]fields.append" -> memory.WriteInt32(int64 (arguments.[5].AsInt32()), 0)
                | "[method]request-options.set-connect-timeout"
                | "[method]request-options.set-first-byte-timeout" -> memory.WriteInt32(int64 (arguments.[3].AsInt32()), 0)
                | "[stream-new-0][static]request.new"
                | "[future-new-1][static]request.new"
                | "[stream-new-0][static]response.new"
                | "[future-new-1][static]response.new"
                | "[future-new-0][static]request.consume-body" -> results.[0] <- Wasmtime.ValueBox.op_Implicit (ends ())
                // The request `handle` is given: a `GET` (case 0), with no scheme, authority or path, which are options.
                | "[method]request.get-method"
                | "[method]request.get-scheme"
                | "[method]request.get-authority"
                | "[method]request.get-path-with-query" -> memory.WriteInt32(int64 (arguments.[1].AsInt32()), 0)
                | "[method]request.get-headers" -> results.[0] <- Wasmtime.ValueBox.op_Implicit (handle ())
                | "[method]fields.copy-all" ->
                    // An empty list, in a block of the module's, which it frees.
                    let block = caller.GetFunction("cabi_realloc").Invoke(0, 0, 4, 8) :?> int
                    let at = int64 (arguments.[1].AsInt32())
                    memory.WriteInt32(at, block)
                    memory.WriteInt32(at + 4L, 0)
                | "[static]request.consume-body"
                | "[static]response.new" ->
                    let made = int64 (arguments.[arguments.Length - 1].AsInt32())
                    memory.WriteInt32(made, handle ())
                    memory.WriteInt32(made + 4L, handle ())
                // The request's body has ended: DROPPED, 1, with nothing read.
                | "[async-lower][stream-read-1][static]request.consume-body" -> results.[0] <- Wasmtime.ValueBox.op_Implicit 1
                | "[async-lower][stream-write-0][static]response.new" ->
                    let length = arguments.[2].AsInt32()
                    let address = int64 (arguments.[1].AsInt32())
                    host.ResponseBody.Append(Text.Encoding.UTF8.GetString(Array.init length (fun index -> memory.ReadByte(address + int64 index)))) |> ignore
                    results.[0] <- Wasmtime.ValueBox.op_Implicit (length <<< 4)
                | "[static]request.new" ->
                    let made = int64 (arguments.[6].AsInt32())
                    memory.WriteInt32(made, handle ())
                    memory.WriteInt32(made + 4L, handle ())
                | "[async-lower][stream-write-0][static]request.new" ->
                    let writer = arguments.[0].AsInt32()
                    host.BodyWriters.Add writer
                    waitables.[writer] <- { Deadline = now + 50_000_000L; Event = streamWriteEvent; Value = arguments.[2].AsInt32() <<< 4 }
                    delivered.Remove writer |> ignore
                    results.[0] <- Wasmtime.ValueBox.op_Implicit blocked
                | "[async-lower]send" ->
                    // `err`, with the error-code `DNS-timeout`, case 0.
                    let answer = int64 (arguments.[1].AsInt32())
                    memory.WriteByte(answer, 1uy)
                    memory.WriteByte(answer + 8L, 0uy)
                    results.[0] <- Wasmtime.ValueBox.op_Implicit (subtask 100_000_000L)
                | _ -> ()))
        | _ -> None

    /// Runs `run` of `instance` to its end, as a host does one lifted with the callback ABI, and checks that it gave
    /// `ok`; after `cancelAfter` waits, it gives the callback TASK_CANCELLED rather than the set's next event.
    member host.Run(instance: Wasmtime.Instance, ?cancelAfter: int) =
        let callback = instance.GetFunction<int, int, int, int>("[callback][async-lift]wasi:cli/run@0.3.0#run")
        let mutable code = instance.GetFunction<int>("[async-lift]wasi:cli/run@0.3.0#run").Invoke()
        while code &&& 0xF = wait do
            host.Waited <- host.Waited + 1
            code <-
                if cancelAfter = Some host.Waited then callback.Invoke(taskCancelledEvent, 0, 0)
                else
                    let waitable, ready = nextEvent (code >>> 4)
                    host.Delivered.Add waitable
                    callback.Invoke(ready.Event, waitable, ready.Value)
        Assert.Equal(exit, code)
        Assert.Equal(Some 0, host.Returned)

    /// Runs `handle` of `instance` to its end, as a host does one lifted with the callback ABI, with a request of its
    /// own, and checks that it returned a response.
    member host.Handle(instance: Wasmtime.Instance) =
        let callback = instance.GetFunction<int, int, int, int>("[callback][async-lift]wasi:http/handler@0.3.0#handle")
        let mutable code = instance.GetFunction<int, int>("[async-lift]wasi:http/handler@0.3.0#handle").Invoke(handle ())
        while code &&& 0xF = wait do
            host.Waited <- host.Waited + 1
            let waitable, ready = nextEvent (code >>> 4)
            host.Delivered.Add waitable
            code <- callback.Invoke(ready.Event, waitable, ready.Value)
        Assert.Equal(exit, code)
        Assert.True(host.Response.IsSome, "handle returned no response")

    /// Calls the export `name` of `instance`, lifted with the callback ABI, with `arguments`, to its end, as a host
    /// does: while it or its callback returns WAIT, it gives the callback the next event of the set.
    member host.Call(instance: Wasmtime.Instance, name: string, [<ParamArray>] arguments: Wasmtime.ValueBox array) =
        let callback = instance.GetFunction<int, int, int, int>($"[callback][async-lift]{name}")
        let mutable code = instance.GetFunction($"[async-lift]{name}").Invoke(arguments) :?> int
        while code &&& 0xF = wait do
            host.Waited <- host.Waited + 1
            let waitable, ready = nextEvent (code >>> 4)
            host.Delivered.Add waitable
            code <- callback.Invoke(ready.Event, waitable, ready.Value)
        Assert.Equal(exit, code)

/// The library package `tested`, whose manifest's `tests` selects `tests` when it isn't empty, with `files`, by their
/// paths relative to its directory, written to a new directory.
let testedPackage (tests: string) (files: (string * string) list) =
    let directory = new TempDirectory()
    let testsField = if tests = "" then "" else $", tests = [\"{tests}\"]"
    directory.Write "tested.nyxp" ($"""(package = (name = "tested", version = "0.1.0", sources = ["src/**/*.nyx"]{testsField}))""" + "\n")
    for path, text in files do
        directory.Write path text
    directory

/// The test build of the package in `directory` (see `testedPackage`), under the test host, as `nanyx test` builds it: a
/// debug build, its places named from the package's directory, in the binary format; and the errors its build reported,
/// which it defers to the tests that reach them.
let testBuildOf (directory: TempDirectory) : byte array * Diagnostic list =
    let store = testStore ()
    match Package.ofManifestFrom store (Path.Combine(directory.Path, "tested.nyxp")) with
    | Ok found ->
        let build = Package.testBuild store false found |> Result.defaultWith failwith
        let root = (Package.testResult build).Value
        let typed = root.Typed |> Option.defaultWith (fun () -> failwith $"%A{build.Diagnostics @ root.Diagnostics}")
        let options = { WasmOptions.Default with Places = true; SourceRoot = Some directory.Path }
        Transpiler.Wasm.TestBuild.assemble options typed,
        build.Diagnostics @ root.Diagnostics |> List.filter NyxCompiler.Diagnostics.isError |> List.distinct
    | Error problems -> failwith $"%A{problems}"

/// Each test of the test build `wasm`, by its full name, run as `nanyx test` runs it, with `seed` and a time limit of
/// `timeout`, a `#io` test in this process's directory.
let runTestsOf (wasm: byte array) (seed: int64) (timeout: TimeSpan) : Map<string, Nanyx.Runtime.TestRun> =
    match Nanyx.Runtime.WasmTester.run wasm (Directory.GetCurrentDirectory()) [] (fun _ -> true) seed timeout ignore with
    | Ok runs -> runs |> List.map (fun run -> run.Test.FullName, run) |> Map.ofList
    | Error error -> failwith $"%A{error}"

/// The lint build of the linter script at `inputPath`, whose `@entry` is a linter's `lint` and whose host is the linter
/// host, `nanyx.analysis.host`, compiled as `nanyx run` compiles a script, with the tests' package store: the module, in
/// the binary format (see `Transpiler.Wasm.LintBuild`).
let lintBuildOf (inputPath: string) : byte array =
    let mutable compiled = None
    let compiling =
        inProcess (Path.GetDirectoryName inputPath) "" [] (fun shell ->
            if CliCommands.TryPullScript(shell, inputPath) then
                compiled <- Some(CliCommands.CompileScript(shell, inputPath, false))
                0
            else 1)
    match compiled with
    | Some result when result.Diagnostics |> List.exists NyxCompiler.Diagnostics.isError ->
        failwith $"{inputPath} has errors:\n%A{result.Diagnostics |> List.filter NyxCompiler.Diagnostics.isError}"
    | Some { Typed = Some typed } -> Nanyx.LargeStack.run (fun () -> Transpiler.Wasm.LintBuild.assemble WasmOptions.Default typed)
    | _ -> failwith $"{inputPath} didn't compile:\n{compiling.StandardError}"

/// The linter `name`, with the rules `rules`, that runs the lint build `wasm` (see `lintBuildOf`) over each module, as
/// the compiler runs a linter package.
let lintBuildLinter (name: string) (rules: NyxCompiler.Rule list) (wasm: byte array) : NyxCompiler.Linter =
    let run = Nanyx.Runtime.WasmLinter.compile wasm |> Result.defaultWith (fun error -> failwith $"%A{error}")
    { Name = name
      Package = $"{name}.lint"
      Rules = rules
      Lint = NyxCompiler.LintAnswers.lintWith (fun answer -> run answer |> Result.mapError (fun error -> $"%A{error}")) }
