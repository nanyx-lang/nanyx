module Nanyx.Compiler.Tests.TestSupport

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program

/// The tests run with a package cache of their own (see `TestPackageCache`).
[<assembly: Nanyx.TestPackageCache.Framework("Nanyx.TestPackageCache+PackageCacheFramework", "Nanyx.Compiler.Tests")>]
do ()

/// Matches an `IntLit` whose value fits an `int64`, which a pattern can name: F# has no `bigint` constant patterns.
let (|IntLit64|_|) (literal: Literal) =
    match literal with
    | IntLit value when value >= bigint System.Int64.MinValue && value <= bigint System.Int64.MaxValue -> Some (int64 value)
    | _ -> None

/// `source` with `\n` line breaks, whatever the line endings of the test file's checkout.
let normalized (source: string) = source.ReplaceLineEndings "\n"

/// `normalized source` with a line break after it: for a source that ends in a line break, written with its closing
/// quotes, and the code after them, on its last line.
let withFinalNewline (source: string) = normalized source + "\n"

/// What `action` gives in a new directory of its own under the temporary directory, which is deleted after it.
let withTempDir (action: string -> 'T) : 'T =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    try
        action dir
    finally
        if Directory.Exists dir then
            Directory.Delete(dir, true)

/// The one diagnostic of `result`.
let single (result: CompileResult) =
    Assert.Single(result.Diagnostics) |> ignore
    result.Diagnostics.Head

/// The text `span` covers in `source` (whose line endings are already `\n`).
let textAt (source: string) (span: SourceSpan) = source.Substring(span.Start, span.End - span.Start)

let spanOf (diagnostic: Diagnostic) =
    Assert.True(diagnostic.Span.IsSome, $"Expected a span on %A{diagnostic}")
    diagnostic.Span.Value

/// The one diagnostic compiling `source` gives.
/// A manifest's `host` entry naming the console host the compiler ships, at its version: what an application with an
/// `@entry` writes.
let consoleHostEntry = $"host = (package = \"{Hosts.consolePackage}\", version = \"{Toolchain.version}\")"

/// The console host's file, in the package the compiler ships (see `ShippedPackages`).
let consoleHostFile = Path.Combine(ShippedPackages.directoryOf Hosts.consolePackage, "src", "cli.host.nyx")

/// `source` checked as the console host's file is, so that it may require `HostRuntime`, with `linters`.
let compileAsHost (linters: LinterOptions) (source: string) = Compiler.compileFrom linters (SourceFile consoleHostFile) source

let diagnosticOf (source: string) = single (Compiler.compile source)

/// The one diagnostic `result`, compiled from `source`, has: its code, its message, and the text its span covers.
/// Spans are offsets in the source with its line endings made `\n`, whatever the test file's are.
let onlyDiagnosticIn (source: string) (result: CompileResult) =
    let diagnostic = single result
    diagnostic.Code, diagnostic.Message, textAt (source.ReplaceLineEndings "\n") (spanOf diagnostic)

/// The one diagnostic compiling `source` gives: its code, its message, and the text it is reported at.
let onlyDiagnostic (source: string) = onlyDiagnosticIn source (Compiler.compile source)

/// The code and the text each diagnostic of `result`, compiled from `source`, is reported at, in order. Spans are offsets
/// in the source with its line endings made `\n`.
let reportedAtIn (source: string) (result: CompileResult) : (string * string) list =
    let source = normalized source
    result.Diagnostics |> List.map (fun diagnostic -> diagnostic.Code, textAt source (spanOf diagnostic))

/// The code and the text each diagnostic of compiling `source`, with a final line break, is reported at, in order.
let reportedAt (source: string) : (string * string) list =
    let source = withFinalNewline source
    reportedAtIn source (Compiler.compile source)

/// The official linter as a project that turns it on runs it: its package, `nanyx.lint`, seeded, built into a lint build
/// and run, beside the compiler's own rules for each rule the package doesn't implement yet (see `Linters.withPackage`).
let officialLinter : Lazy<Linter> =
    lazy
        (let store = SeededPackages.user ()
         let entry : LinterEntry =
             { Package = Linters.official.Package
               Path = None
               Version = VersionRequirement.tryParse Toolchain.version
               Span = SourceSpan.none
               Settings = None }
         let runner = Transpiler.Wasm.LintBuild.runner Nanyx.Runtime.WasmLinter.load
         match Package.scriptLinters store SourceSpan.none [ entry ] with
         | [ linter ], [] -> Linters.withPackage (LintBuilds.make store runner linter |> Result.defaultWith failwith) Linters.official
         | _, errors -> failwith $"nanyx.lint can't be read: %A{errors}")

/// Every linter the compiler has, each rule at its default severity, as `Linters.everyLinter` is, with the official one
/// run as its package (see `officialLinter`).
let everyLinter () : LinterOptions = { Linters.everyLinter with Linters = [ officialLinter.Value ] }

/// Compiles `source` with every linter the compiler has turned on, at its defaults, but for `unusedPrivateDefinitions`:
/// these tests write top-level defs nothing uses, and that rule has tests of its own.
let compileWithOfficial (source: string) =
    Compiler.compileWith
        { everyLinter () with Severities = Map.ofList [ "default/unusedPrivateDefinitions", None ] }
        source

/// A new directory holding `files` (relative path, contents), deleted when disposed: a package, with its manifest.
type TempPackage(files: (string * string) list) =
    let root = Path.Combine(Path.GetTempPath(), "nanyx-package-" + Guid.NewGuid().ToString("N"))
    do
        for (path, contents) in files do
            let full = Path.Combine(root, path)
            Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
            File.WriteAllText(full, contents)
    member _.Root = root
    member _.Path(relative: string) = Path.Combine(root, relative)
    interface IDisposable with
        member _.Dispose() = try Directory.Delete(root, true) with _ -> ()

/// The manifest of an application under the console host that depends on the console's interface package,
/// `nanyx.cli`, so that its files can import `nanyx.cli/console` and `nanyx.cli/env`.
let consoleCoreApp =
    $"""(
  package = (name = "app", version = "0.1.0", sources = ["src/**/*.nyx"])
  dependencies = [(package = "nanyx.cli", version = "{Toolchain.version}")]
  {consoleHostEntry}
)
"""

/// `source`, a module, compiled with `linters` as the file `src/main.nyx` of an application whose manifest is
/// `consoleCoreApp`.
let compileWithConsoleCoreUsing (linters: LinterOptions) (source: string) =
    use package = new TempPackage([ "app.nyxp", consoleCoreApp; "src/main.nyx", source ])
    Package.compileSourceWith (SeededPackages.user ()) linters (package.Path "src/main.nyx") source

/// `source`, a module, compiled as the file `src/main.nyx` of an application whose manifest is `consoleCoreApp`.
let compileWithConsoleCore (source: string) = compileWithConsoleCoreUsing Linters.none source

/// A listener that writes down what it is told, as "file <path>", "start <name>", "end <name>" and "reused <name>".
type Recording() =
    let events = Collections.Generic.List<string>()
    member _.Events = List.ofSeq events
    interface CompileListener with
        member _.FileStarted file = events.Add $"file {file}"
        member _.ModuleStarted name = events.Add $"start {name}"
        member _.ModuleFinished(name, _) = events.Add $"end {name}"
        member _.ModuleReused name = events.Add $"reused {name}"

let compileImporting (moduleSource: string) (mainSource: string) =
    let mutable result = Unchecked.defaultof<CompileResult>
    withTempDir (fun dir ->
        File.WriteAllText(Path.Combine(dir, "mod.nyx"), moduleSource)
        let mainPath = Path.Combine(dir, "main.nyx")
        File.WriteAllText(mainPath, mainSource)
        result <- Compiler.compileFile mainPath)
    result

let compileImportingAll (modules: (string * string) list) (mainSource: string) =
    let mutable result = Unchecked.defaultof<CompileResult>
    withTempDir (fun dir ->
        for fileName, source in modules do
            File.WriteAllText(Path.Combine(dir, fileName), source)
        let mainPath = Path.Combine(dir, "main.nyx")
        File.WriteAllText(mainPath, mainSource)
        result <- Compiler.compileFile mainPath)
    result

/// `source` with `edits` applied.
let applyEdits (source: string) (edits: TextEdit list) =
    edits
    |> List.sortByDescending (fun edit -> edit.Span.Start)
    |> List.fold (fun (text: string) edit -> text.Substring(0, edit.Span.Start) + edit.NewText + text.Substring(edit.Span.End)) source

/// The value of `result`, which must be `Ok`.
let okOf (result: Result<'T, #seq<'Error>>) =
    match result with
    | Result.Ok value -> value
    | Result.Error errors -> failwith (String.concat "\n" (errors |> Seq.map string))

/// The errors of `result`, which must be an `Error`.
let errorsOf (result: Result<'T, 'Errors>) =
    match result with
    | Result.Error errors -> errors
    | Result.Ok _ -> failwith "Expected errors"

/// The store of a package under test: its cache and index are its `cache` and `index` directories, it can't fetch
/// from GitHub, and it seeds the packages the compiler ships as any store does.
let storeOf (package: TempPackage) : PackageStore =
    SeededPackages.store (package.Path "cache") (package.Path "index") GitHub.unsupported

/// What `nanyx pull` does for the package whose manifest is at `manifestPath`, from `store`, with a conflict's
/// explanation as its error.
let pullFrom (store: PackageStore) (manifestPath: string) : Result<string list, string list> =
    PackageDependencies.pull store manifestPath
    |> Result.map _.Lines
    |> Result.mapError (function
        | NoVersions diagnostic -> [ diagnostic.Message ]
        | LockErrors errors -> errors)

/// Puts `bytes`, the packed `found`, in the `artifacts` directory of `package`'s index, and lists it in the index after
/// the versions listed already, with the dependencies its manifest names by their version, as a release would. Gives
/// its SHA-256.
let publishToIndex (package: TempPackage) (found: Package) (bytes: byte array) =
    let name, version = found.Manifest.Name, found.Manifest.Version
    let artifact = package.Path $"index/artifacts/{PackageArchive.fileName found.Manifest}"
    Directory.CreateDirectory(Path.GetDirectoryName artifact) |> ignore
    File.WriteAllBytes(artifact, bytes)
    let hash = PackageArchive.sha256 bytes
    File.WriteAllText(package.Path "index/config.json", """{ "format": 1, "index": "index/" }""")
    let listing = package.Path $"index/index/acme/{name}.json"
    let dependencies =
        found.Manifest.Dependencies
        |> List.choose (fun dependency ->
            match dependency.Source with
            | FromIndex requirement -> Some $"\"{dependency.Name}\": \"{requirement}\""
            | _ -> None)
        |> String.concat ", "
    Directory.CreateDirectory(Path.GetDirectoryName listing) |> ignore
    let earlier =
        if File.Exists listing then
            let text = File.ReadAllText listing
            text.Substring(text.IndexOf '[' + 1, text.LastIndexOf ']' - text.IndexOf '[' - 1).Trim() + ","
        else
            ""
    File.WriteAllText(
        listing,
        $$"""{ "name": "{{name}}", "versions": [{{earlier}} { "version": "{{version}}", "url": "../../artifacts/{{Path.GetFileName artifact}}", "sha256": "{{hash}}", "dependencies": { {{dependencies}} }, "unlisted": false, "available": true }] }"""
    )
    hash

/// `source` with the edits of `fix` applied.
let applyFix (source: string) (fix: Fix) = applyEdits source fix.Edits

/// `marked` with `\n` line endings and without its `$$`, and where the `$$` was.
let unmarked (marked: string) =
    let marked = normalized marked
    let offset = marked.IndexOf "$$"
    marked.Remove(offset, 2), offset

/// `marked` with `\n` line endings and without its `[[` and `]]`, and the span between them.
let unmarkedSelection (marked: string) =
    let marked = normalized marked
    let start = marked.IndexOf "[["
    let finish = marked.IndexOf "]]" - 2
    let selection : SourceSpan = { File = ""; Start = start; End = finish; StartLine = 0; StartColumn = 0 }
    marked.Replace("[[", "").Replace("]]", ""), selection

/// `source` with the edits of `refactoring` applied, checked to compile with no diagnostics and to be laid out as the
/// formatter lays it out.
let refactoredText (source: string) (refactoring: NyxCompiler.Refactorings.Refactoring) =
    let after = applyEdits source refactoring.Edits
    let result = Compiler.compile after
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%s\n%A" after result.Diagnostics)
    match Nanyx.Formatter.FormatCommand.formatText Nanyx.Formatter.Formatter.Options.defaults "" after with
    | Nanyx.Formatter.FormatCommand.Formatting.Formatted formatted -> Assert.Equal(formatted, after)
    | other -> failwithf "%A" other
    after

/// `source` with the edits of the only fix of the only diagnostic `compileWithOfficial` gives for it applied.
let fixed' (source: string) =
    let diagnostic = Assert.Single((compileWithOfficial source).Diagnostics)
    let fix = Assert.Single(diagnostic.Fixes)
    applyFix source fix, fix.Title

/// The offset of the `occurrence`th (zero-based) appearance of `text` in `source`, plus `within` characters.
let offsetOf (source: string) (text: string) (occurrence: int) (within: int) =
    let rec find from remaining =
        let index = source.IndexOf(text, from, StringComparison.Ordinal)
        if index < 0 then failwith $"'{text}' not found"
        elif remaining = 0 then index
        else find (index + 1) (remaining - 1)
    find 0 occurrence + within

/// `source` compiled, with no diagnostics.
let compileClean (source: string) =
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    result.Typed.Value

/// Asserts that `run` takes time that grows with the size of its input, not with its square: on `prepare n` it takes less
/// than ten times what it takes on `prepare (n / 4)`. Something linear takes about 4 times as long, and something
/// quadratic 16. Both inputs are prepared first, outside the timing, and each run starts with what earlier runs and
/// the preparing left collected, so that a collection of their garbage doesn't land in it (#2636). Each size is timed
/// twice, in turn, after a run to warm up, and its quickest run counts; when they miss, each is timed five times
/// more, so that load on the machine, which slows some runs and not others, can't make the test fail (#2388). It is a
/// timing test (see `TestTiming`).
let takesLinearTime (n: int) (prepare: int -> 'input) (run: 'input -> unit) =
    let quarter = n / 4
    let small, large = prepare quarter, prepare n
    let time (input: 'input) =
        System.GC.Collect()
        System.GC.WaitForPendingFinalizers()
        System.GC.Collect()
        Nanyx.TestTiming.millisecondsOf (fun () -> run input)
    let quickest (runs: int) = List.init runs (fun _ -> time small, time large) |> List.reduce (fun (a, b) (c, d) -> min a c, min b d)
    time small |> ignore
    let isLinear (atQuarter: float, atN: float) = atN < 10.0 * atQuarter
    let first = quickest 2
    let atQuarter, atN =
        if isLinear first then first
        else
            let again = quickest 5
            min (fst first) (fst again), min (snd first) (snd again)
    Assert.True(isLinear (atQuarter, atN), $"{n:N0} took {atN:F1} ms, {atN / atQuarter:F1} times the {atQuarter:F1} ms of {quarter:N0}")

/// Asserts that `run` allocates, given `prepare n`, memory that grows with `n` rather than with its square: less than 8
/// times what it allocates given a quarter of `n`, where the square would be 16 times. What this thread allocates
/// (`GC.GetAllocatedBytesForCurrentThread`) is the same however loaded the machine is, so this counts the work of a
/// function that has no count of its own, such as a walk of the syntax tree, rather than timing it.
let allocatesLinearly (n: int) (prepare: int -> 'input) (run: 'input -> unit) =
    let quarter = n / 4
    let small, large = prepare quarter, prepare n
    let allocated (input: 'input) =
        let before = System.GC.GetAllocatedBytesForCurrentThread()
        run input
        System.GC.GetAllocatedBytesForCurrentThread() - before
    // The first run compiles `run`, which allocates too.
    allocated small |> ignore
    let atQuarter, atN = allocated small, allocated large
    Assert.True(atN < 8L * atQuarter, $"{n:N0} allocated {atN:N0} bytes, {float atN / float atQuarter:F1} times the {atQuarter:N0} of {quarter:N0}")

/// Asserts that `source n` compiles in time that grows with `n`, not with its square (see `takesLinearTime`). What
/// `source` gives is compiled as it is, so a test checks its diagnostics itself; `checksInLinearTime` is for one that
/// has none.
let compilesInLinearTime (n: int) (source: int -> string) =
    takesLinearTime n source (Compiler.compile >> ignore)

/// `compilesInLinearTime`, for a `source` with no diagnostics at a quarter of `n`.
let checksInLinearTime (n: int) (source: int -> string) =
    compileClean (source (n / 4)) |> ignore
    compilesInLinearTime n source

/// The types of the top-level defs of `source`, its line endings made `\n`, which must compile with no diagnostics.
let definedTypes (source: string) = (compileClean (normalized source)).Types

/// The type of the top-level def `name` in `typed`, as `Unifier.tyToString` writes it.
let typeIn (typed: TypedModule) (name: string) = Unifier.tyToString typed.Types.[name]

/// The type of the top-level def `name` in `typed`, as the editor writes it in ASCII.
let asciiTypeIn (typed: TypedModule) (name: string) = Unifier.tyToDisplayString Ascii typed.Types.[name]

/// The type of the top-level def `name` in `source` (see `definedTypes`), as `Unifier.tyToString` writes it. Given
/// only `source`, it compiles it once, and gives the type of each name it is then given.
let typeOf (source: string) =
    let types = definedTypes source
    fun (name: string) -> Unifier.tyToString types.[name]

/// The types of the top-level defs `names` in `source`, as `typeOf` gives them.
let typesOf (source: string) (names: string list) = List.map (typeOf source) names

/// The type a hover shows at the first `text` in `source`, which must compile with no diagnostics.
let hoverType (source: string) (text: string) =
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let offset = source.ReplaceLineEndings("\n").IndexOf text
    Hover.typeAt Unicode result offset |> Option.map _.Type

/// The inlay hints the editor shows inside the top-level defs of `result`: those of `TypeHints.inlayHintsWithTopLevel`
/// without the top-level defs' own.
let localInlayHints (names: TypeNotation) (file: string) (result: CompileResult) =
    TypeHints.inlayHintsWithTopLevel names file result |> List.filter (fun hint -> hint.Kind <> TopLevelValue)

/// Asserts that `result` reports a diagnostic of `code`, and shows what it reported when it does not.
let assertReports (code: DiagnosticCode) (result: CompileResult) =
    Assert.True(
        result.Diagnostics |> List.exists (fun diagnostic -> diagnostic.Code = code.Code),
        $"Expected {code.Code} ({code.Title}), got %A{result.Diagnostics}")

/// The code of the one diagnostic `source` compiles to, and the text it is reported at.
let singleError (source: string) =
    let result = Compiler.compile source
    Assert.True(1 = result.Diagnostics.Length, $"Expected one diagnostic: %A{result.Diagnostics}")
    let diagnostic = result.Diagnostics.Head
    let span = diagnostic.Span.Value
    diagnostic.Code, source.ReplaceLineEndings("\n").Substring(span.Start, span.End - span.Start)

/// The one diagnostic `source` has, which must be a missing context: its message, and the text it is reported at.
let missingContext (source: string) =
    match (Compiler.compile source).Diagnostics with
    | [ diagnostic ] ->
        Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, diagnostic.Code)
        let span = diagnostic.Span.Value
        diagnostic.Message, source.Substring(span.Start, span.End - span.Start)
    | other -> failwith $"Expected one diagnostic, got %A{other}"

/// The instances the calls in `typed`'s own module found, by each call's node. `ContextInstances` also holds those of
/// the definitions the module imports, the prelude's among them, for the backend.
let ownContextInstances (typed: TypedModule) =
    typed.ContextInstances |> Map.filter (fun node _ -> typed.ExprTypes.ContainsKey node)

/// Every instance the calls (and holes) in `typed`'s own module found, in the order they are written.
let instancesOf (typed: TypedModule) =
    ownContextInstances typed |> Map.toList |> List.sortBy fst |> List.map snd |> List.concat |> List.choose id
