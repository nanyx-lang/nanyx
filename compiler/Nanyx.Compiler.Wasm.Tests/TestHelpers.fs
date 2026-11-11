module TranspilerWasmTestHelpers

open System
open System.IO
open System.Diagnostics
open NyxCompiler
open Parser.Program
open Transpiler.Wasm.CodeGen
open Xunit

let private testRootDir = __SOURCE_DIRECTORY__
let private featureRootDir = Path.Combine(testRootDir, "Features")

let normalizeOutput (value: string) =
    value.Replace("\r\n", "\n").TrimEnd()

let loadFeatureFile featureName fileName =
    let filePath = Path.Combine(featureRootDir, featureName, fileName)
    if not (File.Exists(filePath)) then
        failwith $"Test data file not found: {filePath}"
    File.ReadAllText(filePath)

/// `source`, type-checked and compiled with `options`. Errors the type checker defers to run time are compiled to
/// traps, as `nanyx run` does.
let transpileWatWith (options: WasmOptions) (source: string) =
    match Compiler.compile source with
    | { Typed = Some typed } -> transpileTypedModuleWithOptions options typed |> fst
    | result ->
        let messages = result.Diagnostics |> List.map (fun d -> d.Message) |> String.concat "\n"
        failwith $"Compile error: {messages}"

let transpileWat (source: string) = transpileWatWith WasmOptions.Default source

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
    if actual <> expected then
        failwith $"WAT output mismatch for {featureName}/{baseName}."

/// What `nanyx run <file>` produced.
type RunOutcome =
    { ExitCode: int
      StandardOutput: string
      StandardError: string }

/// Runs the real CLI (built next to the tests through the project reference) in a child process with `arguments`, so
/// the program's stdout, the diagnostics on stderr and the exit code are all observed exactly as a user would.
/// `standardInput` is written to the program's stdin, which is then closed. It runs in `workingDirectory`, or the
/// tests' own when that is `None`.
let private nanyxProcess (workingDirectory: string option) (standardInput: string) (arguments: string list) : RunOutcome =
    let cliPath = Path.Combine(AppContext.BaseDirectory, "nanyx.dll")
    if not (File.Exists cliPath) then
        failwith $"The nanyx CLI was not found at {cliPath}"
    let startInfo =
        ProcessStartInfo("dotnet", RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false)
    workingDirectory |> Option.iter (fun directory -> startInfo.WorkingDirectory <- directory)
    for argument in cliPath :: arguments do
        startInfo.ArgumentList.Add argument
    use proc = Process.Start startInfo
    let stdout = proc.StandardOutput.ReadToEndAsync()
    let stderr = proc.StandardError.ReadToEndAsync()
    proc.StandardInput.Write standardInput
    proc.StandardInput.Close()
    proc.WaitForExit()
    { ExitCode = proc.ExitCode
      StandardOutput = stdout.Result
      StandardError = stderr.Result }

let nanyxWithInput (standardInput: string) (arguments: string list) : RunOutcome =
    nanyxProcess None standardInput arguments

/// Runs `nanyx` with `arguments` in `directory`, as a user does who has changed to it.
let nanyxIn (directory: string) (arguments: string list) : RunOutcome =
    nanyxProcess (Some directory) "" arguments

/// Does what `nanyx run <inputPath>` does, with `--check-memory` when `checkMemory` is set, through the same code
/// (`CliCommands.RunScript`) but in this process, which saves starting the CLI for every program. `standardInput` is
/// the program's stdin. Its stdout is captured, and so is its stderr, with the compiler's diagnostics and the run's own
/// errors in it as the CLI prints them.
let runScriptWith (checkMemory: bool) (standardInput: string) (inputPath: string) : RunOutcome =
    let inputFile = Path.Combine(Path.GetTempPath(), $"nanyx-stdin-{Guid.NewGuid():N}.txt")
    File.WriteAllText(inputFile, standardInput)
    try
        use output = new MemoryStream()
        use error = new MemoryStream()
        // Flushed at every write, so that the CLI's messages and the program's own stderr stay in the order written.
        use errors = new StreamWriter(error, Text.UTF8Encoding(false), AutoFlush = true)
        let exitCode =
            CliCommands.RunScript(inputPath, checkMemory, errors, Nanyx.Runtime.ProgramIo.Redirected(inputFile, output, error))
        { ExitCode = exitCode
          StandardOutput = Text.Encoding.UTF8.GetString(output.ToArray())
          StandardError = Text.Encoding.UTF8.GetString(error.ToArray()) }
    finally
        File.Delete inputFile

/// Runs the program in `inputPath` with its memory checked, so that every program a test runs also fails it when it
/// frees a block twice, uses an object after freeing it, or leaves anything on the heap.
let runFileWithInput (standardInput: string) (inputPath: string) : RunOutcome = runScriptWith true standardInput inputPath

let runFile (inputPath: string) : RunOutcome = runFileWithInput "" inputPath

/// A new, empty directory of its own, removed with everything in it when disposed.
type TempDirectory() =
    let path = Path.Combine(Path.GetTempPath(), $"nanyx-{Guid.NewGuid():N}")
    do Directory.CreateDirectory path |> ignore
    member _.Path = path
    interface IDisposable with
        member _.Dispose() = try Directory.Delete(path, true) with _ -> ()

/// Runs `nanyx` with `arguments` (with `{package}` standing for the package's directory) on a package of `files`
/// (paths under its `src` directory, with their text), made in a directory of its own and removed afterwards. Gives
/// what it printed and whether it wrote the program to out/app.wat.
let nanyxOnPackage (files: (string * string) list) (arguments: string list) : RunOutcome * bool =
    use directory = new TempDirectory()
    let root = directory.Path
    Directory.CreateDirectory(Path.Combine(root, "src")) |> ignore
    File.WriteAllText(Path.Combine(root, "app.nyxp"), "[package]\nname = \"app\"\nversion = \"0.1.0\"\nsources = [\"src/**/*.nyx\"]\n")
    for path, text in files do
        File.WriteAllText(Path.Combine(root, "src", path), text)
    let outcome = nanyxWithInput "" (arguments |> List.map (fun argument -> argument.Replace("{package}", root)))
    outcome, File.Exists(Path.Combine(root, "out", "app.wat"))

let runE2E testName =
    runFile (Path.Combine(__SOURCE_DIRECTORY__, "E2E", testName, "input.nyx"))

/// `source`, saved as a script and run (see `runScriptWith`).
let runSourceWith (checkMemory: bool) (standardInput: string) (source: string) : RunOutcome =
    let inputPath = Path.Combine(Path.GetTempPath(), $"nanyx-run-{Guid.NewGuid():N}.nyx")
    File.WriteAllText(inputPath, source)
    try runScriptWith checkMemory standardInput inputPath
    finally File.Delete inputPath

let runSourceWithInput (standardInput: string) (source: string) : RunOutcome = runSourceWith true standardInput source

let runSource (source: string) : RunOutcome = runSourceWithInput "" source

/// A program that prints `T` or `F` for each of `comparisons`, one per line, after the definitions in `definitions`.
/// The comparisons can use `s`, the string "aa" made at run time.
let comparing (definitions: string list) (comparisons: string list) =
    [ "import nanyx/console"
      ""
      "def show = { b -> if b then \"T\" else \"F\" }"
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

/// The options the in-process tests compile with: memory checks on, so that the counters are there to read.
let checkedOptions = { CheckMemory = true }

/// A program compiled with memory checks (see `WasmOptions.CheckMemory`), instantiated on Wasmtime with a WASI that
/// reads nothing and writes nowhere, and its top-level code run. The drop functions of some types are exported as
/// well, so that a test can call its functions, give up what they return, and see how far the heap has grown, how
/// often it allocated, and how many blocks are still in use.
type WasmProgram(engine: Wasmtime.Engine, store: Wasmtime.Store, instance: Wasmtime.Instance) =
    let memory = instance.GetMemory("memory")
    let counter (name: string) = instance.GetGlobal(name).GetValue() :?> int
    member _.Call(name: string, argument: int) = instance.GetFunction<int, int>(name).Invoke argument
    member _.Drop(ty: Transpiler.Wasm.CoreIr.IrType, value: int) =
        instance.GetAction<int>(Transpiler.Wasm.CoreIrDrop.dropName ty).Invoke value
    member _.HeapPointer = counter CheckedExports.heapPointer
    member _.Allocations = counter CheckedExports.allocations
    member _.LiveBlocks = counter CheckedExports.liveBlocks
    member _.Word(address: int) = memory.ReadInt32(int64 address)
    member _.Text(pointer: int) = memory.ReadString(int64 pointer, memory.ReadInt32(int64 (pointer - 4)))
    interface IDisposable with
        member _.Dispose() =
            store.Dispose()
            engine.Dispose()

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
