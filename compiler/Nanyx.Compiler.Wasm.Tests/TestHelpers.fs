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

/// `source`, type-checked and compiled. Errors the type checker defers to run time are compiled to traps, as
/// `nanyx run` does.
let transpileWat (source: string) =
    match Compiler.compile source with
    | { Typed = Some typed } -> transpileTypedModuleToWat typed
    | result ->
        let messages = result.Diagnostics |> List.map (fun d -> d.Message) |> String.concat "\n"
        failwith $"Compile error: {messages}"

let transpileWatFile (filePath: string) =
    match Compiler.compileFile filePath with
    | result when not result.Diagnostics.IsEmpty ->
        let messages = result.Diagnostics |> List.map (fun d -> d.Message) |> String.concat "\n"
        failwith $"Compile error: {messages}"
    | result ->
        match result.Typed with
        | Some typed -> transpileTypedModuleToWat typed
        | None -> failwith "Compile error: no typed module produced"

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
/// `standardInput` is written to the program's stdin, which is then closed.
let nanyxWithInput (standardInput: string) (arguments: string list) : RunOutcome =
    let cliPath = Path.Combine(AppContext.BaseDirectory, "nanyx.dll")
    if not (File.Exists cliPath) then
        failwith $"The nanyx CLI was not found at {cliPath}"
    let startInfo =
        ProcessStartInfo("dotnet", RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false)
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

let nanyxRunWithInput (standardInput: string) (inputPath: string) : RunOutcome =
    nanyxWithInput standardInput [ "run"; inputPath ]

let nanyxRun (inputPath: string) : RunOutcome = nanyxRunWithInput "" inputPath

/// Runs `nanyx` with `arguments` (with `{package}` standing for the package's directory) on a package of `files`
/// (paths under its `src` directory, with their text), made in a directory of its own and removed afterwards. Gives
/// what it printed and whether it wrote the program to out/app.wat.
let nanyxOnPackage (files: (string * string) list) (arguments: string list) : RunOutcome * bool =
    let root = Path.Combine(Path.GetTempPath(), $"nanyx-package-{Guid.NewGuid():N}")
    Directory.CreateDirectory(Path.Combine(root, "src")) |> ignore
    try
        File.WriteAllText(Path.Combine(root, "app.nyxpack"), "[package]\nname = \"app\"\nversion = \"0.1.0\"\nsources = [\"src/**/*.nyx\"]\n")
        for path, text in files do
            File.WriteAllText(Path.Combine(root, "src", path), text)
        let outcome = nanyxWithInput "" (arguments |> List.map (fun argument -> argument.Replace("{package}", root)))
        outcome, File.Exists(Path.Combine(root, "out", "app.wat"))
    finally
        Directory.Delete(root, true)

let runE2E testName =
    nanyxRun (Path.Combine(__SOURCE_DIRECTORY__, "E2E", testName, "input.nyx"))

let runSourceWithInput (standardInput: string) (source: string) : RunOutcome =
    let inputPath = Path.Combine(Path.GetTempPath(), $"nanyx-run-{Guid.NewGuid():N}.nyx")
    File.WriteAllText(inputPath, source)
    try nanyxRunWithInput standardInput inputPath
    finally File.Delete inputPath

let runSource (source: string) : RunOutcome = runSourceWithInput "" source

let assertRan (expectedOutput: string) (outcome: RunOutcome) =
    Assert.True((outcome.ExitCode = 0), $"Expected exit code 0, got {outcome.ExitCode}. stderr:\n{outcome.StandardError}")
    Assert.Equal(expectedOutput, outcome.StandardOutput)
    Assert.Equal("", outcome.StandardError)

/// A program compiled and instantiated on Wasmtime, without WASI, with the drop functions of some types and
/// `$heap_ptr` exported as well, and its allocator counting the blocks it hands out, so that a test can call its
/// functions, give up what they return, and see how far the heap has grown and how often it allocated.
type WasmProgram(engine: Wasmtime.Engine, store: Wasmtime.Store, instance: Wasmtime.Instance) =
    let memory = instance.GetMemory("memory")
    member _.Call(name: string, argument: int) = instance.GetFunction<int, int>(name).Invoke argument
    member _.Drop(ty: Transpiler.Wasm.CoreIr.IrType, value: int) =
        instance.GetAction<int>(Transpiler.Wasm.CoreIrDrop.dropName ty).Invoke value
    member _.HeapPointer = instance.GetGlobal("heap_ptr").GetValue() :?> int
    member _.Allocations = instance.GetGlobal("allocations").GetValue() :?> int
    member _.Word(address: int) = memory.ReadInt32(int64 address)
    member _.Text(pointer: int) = memory.ReadString(int64 pointer, memory.ReadInt32(int64 (pointer - 4)))
    interface IDisposable with
        member _.Dispose() =
            store.Dispose()
            engine.Dispose()

/// `source` compiled and instantiated (see `WasmProgram`), with the drop functions of `dropped` exported.
let instantiateWasm (source: string) (dropped: Transpiler.Wasm.CoreIr.IrType list) =
    let dropName = Transpiler.Wasm.CoreIrDrop.dropName
    // The allocator's first instruction, after its locals, counts the call.
    let allocator = "(func $__nanyx_alloc (param $size i32) (result i32)"
    let wat =
        let wat = transpileWat source
        match wat.IndexOf allocator with
        | -1 -> wat
        | start ->
            let afterLocals = wat.IndexOf("(local $ptr i32)", start) + "(local $ptr i32)".Length
            wat.Insert(afterLocals, "\n    global.get $allocations\n    i32.const 1\n    i32.add\n    global.set $allocations")
    let exports =
        [ yield! dropped |> List.map (fun ty -> $"  (export \"{dropName ty}\" (func ${dropName ty}))")
          if wat.Contains "(global $heap_ptr" then "  (export \"heap_ptr\" (global $heap_ptr))"
          "  (global $allocations (mut i32) (i32.const 0))"
          "  (export \"allocations\" (global $allocations))" ]
        |> String.concat "\n"
    let engine = new Wasmtime.Engine()
    let store = new Wasmtime.Store(engine)
    use module' = Wasmtime.Module.FromText(engine, "program", wat.Substring(0, wat.LastIndexOf ')') + exports + "\n)")
    use linker = new Wasmtime.Linker(engine)
    new WasmProgram(engine, store, linker.Instantiate(store, module'))
