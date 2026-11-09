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

let transpileWat (source: string) =
    match parseModule source with
    | Result.Ok ast -> transpileModuleToWat ast
    | Result.Error err -> failwith $"Parse error: {err}"

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
