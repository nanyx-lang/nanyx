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
/// traps, as `nanyx run` does.
let transpileWatWith (options: WasmOptions) (source: string) =
    match Compiler.compile source with
    | { Typed = Some typed } -> transpileTypedModuleWithOptions options typed |> fst
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
let private inProcess (workingDirectory: string) (standardInput: string) (variables: (string * string) list) (command: Shell -> int) : RunOutcome =
    let inputFile = Path.Combine(Path.GetTempPath(), $"nanyx-stdin-{Guid.NewGuid():N}.txt")
    File.WriteAllText(inputFile, standardInput)
    try
        use output = new MemoryStream()
        use error = new MemoryStream()
        // Flushed at every write, so that the CLI's messages and the program's own output stay in the order written.
        use outputs = new StreamWriter(output, Text.UTF8Encoding(false), AutoFlush = true)
        use errors = new StreamWriter(error, Text.UTF8Encoding(false), AutoFlush = true)
        let exitCode = command (Shell(workingDirectory, outputs, errors, Nanyx.Runtime.ProgramIo.Redirected(inputFile, output, error, variables)))
        { ExitCode = exitCode
          StandardOutput = Text.Encoding.UTF8.GetString(output.ToArray())
          StandardError = Text.Encoding.UTF8.GetString(error.ToArray()) }
    finally
        File.Delete inputFile

/// The `nanyx` command `arguments` names, with its options, as a call of what the CLI runs for it (see `CliCommands`).
/// Only the commands and options the tests use are read.
let private command (arguments: string list) : Shell -> int =
    let rec options (outputPath: string, release: bool, timings: bool, host: string, paths: string list) (rest: string list) =
        match rest with
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
        fun shell -> CliCommands.ManifestIn(shell, List.contains "--json" rest, rest |> List.except [ "--json" ] |> Array.ofList)
    | "check" :: rest ->
        let _, _, timings, host, path = options (null, false, false, null, []) rest
        fun shell -> CliCommands.CheckIn(shell, path, timings, host)
    | "run" :: rest ->
        let _, _, timings, host, path = options (null, false, false, null, []) (List.except [ "--no-inline" ] rest)
        fun shell -> CliCommands.RunIn(shell, false, path, timings, host, not (List.contains "--no-inline" rest))
    | "build" :: rest ->
        let outputPath, release, timings, host, path = options (null, false, false, null, []) (List.except [ "--no-inline" ] rest)
        fun shell -> CliCommands.BuildIn(shell, outputPath, release, path, timings, host, not (List.contains "--no-inline" rest))
    | "wasm" :: inputFile :: rest ->
        let outputPath, release, _, _, _ = options (null, false, false, null, []) rest
        fun shell -> CliCommands.WasmIn(shell, inputFile, outputPath, release)
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

/// Does what `nanyx run <inputPath>` does, with `--check-memory` when `checkMemory` is set (see `nanyxWithInput`).
/// `standardInput` is the program's stdin. Its stdout is captured, and so is its stderr, with the compiler's diagnostics
/// and the run's own errors in it as the CLI prints them.
/// Without `inlineCalls`, as `--no-inline` does, every call is compiled as a call.
let runScriptWithOptions (checkMemory: bool) (inlineCalls: bool) (standardInput: string) (inputPath: string) : RunOutcome =
    inProcess (Directory.GetCurrentDirectory()) standardInput [] (fun shell -> CliCommands.RunScript(shell, inputPath, checkMemory, false, null, inlineCalls))

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
    interface IDisposable with
        member _.Dispose() = try Directory.Delete(path, true) with _ -> ()

/// Runs `nanyx` with `arguments` (with `{package}` standing for the package's directory) on a package of `files`
/// (paths under its `src` directory, with their text), made in a directory of its own and removed afterwards. Gives
/// what it printed and whether it wrote the program to out/app.wat.
let nanyxOnPackage (files: (string * string) list) (arguments: string list) : RunOutcome * bool =
    use directory = new TempDirectory()
    let root = directory.Path
    Directory.CreateDirectory(Path.Combine(root, "src")) |> ignore
    File.WriteAllText(Path.Combine(root, "app.nyxp"), """(package = (name = "app", version = "0.1.0", sources = ["src/**/*.nyx"]))""")
    for path, text in files do
        File.WriteAllText(Path.Combine(root, "src", path), text)
    let outcome = nanyxWithInput "" (arguments |> List.map (fun argument -> argument.Replace("{package}", root)))
    outcome, File.Exists(Path.Combine(root, "out", "app.wat"))

/// What `nanyx check` printed for a script holding `source`, saved as `main.nyx` in a directory of its own.
let checkScript (source: string) =
    use directory = new TempDirectory()
    let path = Path.Combine(directory.Path, "main.nyx")
    File.WriteAllText(path, source)
    nanyxWithInput "" [ "check"; path ]

/// The docs site's pages, under docs/src/content.
let docsContentDirectory =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "docs", "src", "content"))

/// What a docs example's fence says it is: the words after `nanyx` on its opening line. The conventions are in
/// docs/src/content/work-in-progress/compilation.md#tour-examples.
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
    let inputPath = Path.Combine(Path.GetTempPath(), $"nanyx-run-{Guid.NewGuid():N}.nyx")
    File.WriteAllText(inputPath, source)
    try runScriptWithOptions checkMemory inlineCalls standardInput inputPath
    finally File.Delete inputPath

let runSourceWith (checkMemory: bool) (standardInput: string) (source: string) : RunOutcome =
    runSourceWithOptions checkMemory true standardInput source

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

/// `source` checked as the standard library's `console.host`, a host, which may require `HostRuntime`, and compiled
/// with `options`; any diagnostic fails.
let hostWatWith (options: WasmOptions) (source: string) =
    match Compiler.compileFrom Linters.none (StandardLibraryModule "console.host") (normalized source) with
    | { Typed = Some typed } as result when result.Diagnostics.IsEmpty -> transpileTypedModuleWithOptions options typed |> fst
    | result -> failwith $"""Compile error: {result.Diagnostics |> List.map _.Message |> String.concat "\n"}"""

/// What the module `wat` writes to stdout when the CLI's runtime runs it with the command-line arguments `arguments`,
/// its name first, or why it did not run.
let runHost (arguments: string list) (wat: string) : Result<string, Nanyx.Runtime.RunError> =
    let input = Path.GetTempFileName()
    try
        use output = new MemoryStream()
        use error = new MemoryStream()
        Nanyx.Runtime.WasmRunner.run (Nanyx.Runtime.Redirected(input, output, error, [])) arguments wat
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
        Nanyx.Runtime.WasmRunner.runReading (Nanyx.Runtime.Redirected(input, output, error, [])) arguments CheckedExports.start [| CheckedExports.liveBlocks |] wat
        |> Result.map (fun counters -> Text.Encoding.UTF8.GetString(output.ToArray()), counters.[0])
    finally
        File.Delete input
