module Nanyx.Compiler.Tests.Features.StdLib.StandardLibraryCache

open System.IO
open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// The standard library is checked once per process and shared by every compilation after (#694). These programs use
// the same generic prelude functions at different types, so a type variable of the shared prelude that leaked into a
// later compilation, or a change one compilation made to it, would show in the next one's types or diagnostics.

let private withInts =
    normalized """def name: #some(int) | #empty = #some(1)
def called = mapSome(name, { n -> n + 1 })
def r: Result(int, string) = #error("x")
def mapped = r \ Result.map({ n -> 1.5 })
def listed = name \ someToList"""

let private withStrings =
    normalized """def name: #some(string) | #empty = #some("a")
def called = mapSome(name, { s -> true })
def r: Result(string, int) = #error(1)
def mapped = r \ Result.map({ s -> [s] })
def listed = name \ someToList"""

let private withAnError =
    normalized """def name: #some(int) | #empty = #some(1)
def called: #some(string) | #empty = mapSome(name, { n -> n + 1 })"""

/// What a compilation found: its diagnostics, the type of each definition, and the type of each node of the
/// program in the order they are written (node ids differ between compilations; their order does not).
let private outcome (source: string) =
    let result = Compiler.compile source
    let typed = result.Typed.Value
    let shown (types: Map<_, Ty>) = types |> Map.toList |> List.map (snd >> Unifier.tyToString)
    result.Diagnostics |> List.map (fun diagnostic -> diagnostic.Code, diagnostic.Message, diagnostic.Span),
    typed.Types |> Map.map (fun _ ty -> Unifier.tyToString ty) |> Map.toList,
    shown typed.ExprTypes

[<Fact>]
let ``Compilations in a row that use the same generic prelude functions each get the types they get alone`` () =
    let ints = outcome withInts
    let strings = outcome withStrings
    let error = outcome withAnError
    let _, intTypes, _ = ints
    let _, stringTypes, _ = strings
    Assert.Equal<(string * string) list>(
        [ "called", "#some(int) | #empty"; "listed", "list(int)"; "mapped", "Result(float, string)"; "name", "#some(int) | #empty"; "r", "Result(int, string)" ],
        intTypes |> List.filter (fun (name, _) -> List.contains name [ "called"; "listed"; "mapped"; "name"; "r" ]))
    Assert.Equal<(string * string) list>(
        [ "called", "#some(bool) | #empty"; "listed", "list(string)"; "mapped", "Result(list(string), int)"; "name", "#some(string) | #empty"; "r", "Result(string, int)" ],
        stringTypes |> List.filter (fun (name, _) -> List.contains name [ "called"; "listed"; "mapped"; "name"; "r" ]))
    let errors, _, _ = error
    Assert.Equal<string list>([ DiagnosticCodes.TypeMismatch.Code ], errors |> List.map (fun (code, _, _) -> code))
    // Each again, after the others: everything the first compilation of each found is the same.
    Assert.Equal(strings, outcome withStrings)
    Assert.Equal(ints, outcome withInts)
    Assert.Equal(error, outcome withAnError)
    Assert.Equal(strings, outcome withStrings)

[<Fact>]
let ``A standard-library file's source is read once per process`` () =
    let first = StandardLibrary.tryLoadFile "nanyx/prelude.nyx"
    let second = StandardLibrary.tryLoadFile "nanyx/prelude.nyx"
    Assert.True(obj.ReferenceEquals(first.Value, second.Value))
    Assert.Equal(None, StandardLibrary.tryLoadFile "nanyx/missing.nyx")

// The build checks the library once and puts it beside the compiler (#837), so a compile does not check it at all.

let private preludeKey = StandardLibrary.importPrefix + StandardLibrary.preludeModuleName

let private preludeSource = StandardLibrary.tryLoadSource StandardLibrary.preludeModuleName |> Option.get

[<Fact>]
let ``The build puts the checked library beside the compiler`` () =
    Assert.True(File.Exists CheckedLibrary.path, $"{CheckedLibrary.path} is missing")
    let file = CheckedLibrary.read CheckedLibrary.path CheckedLibrary.libraryHash.Value
    Assert.True(file.IsSome, "the checked library was not read")
    Assert.NotEmpty(file.Value.Modules)
    Assert.NotEmpty(file.Value.Shipped)

[<Fact>]
let ``A compile takes the prelude from the checked library and does not check it`` () =
    let recording = Recording()
    let result = Compiler.compileReporting recording Linters.none "def x = 1\n"
    Assert.True(result.TypeChecked, sprintf "%A" result.Diagnostics)
    Assert.DoesNotContain($"start {preludeKey}", recording.Events)
    Assert.Contains($"reused {preludeKey}", recording.Events)

[<Fact>]
let ``A library module whose source has changed is not taken from the checked library`` () =
    Assert.True((CheckedLibrary.tryFind preludeKey preludeSource).IsSome)
    Assert.True((CheckedLibrary.tryFind preludeKey (preludeSource + "\ndef changed = 1\n")).IsNone)

[<Fact>]
let ``A checked library checked from another library is not read`` () =
    Assert.True((CheckedLibrary.read CheckedLibrary.path (CheckedLibrary.libraryHash.Value + "0")).IsNone)

// The build checks the packages the compiler ships too (#1637), so a loose script does not check the console host.

let private consoleFile = Path.Combine(ShippedPackages.directoryOf "nanyx.console.core", "src", "console.nyx")

[<Fact>]
let ``The checked library has the shipped packages' modules, with paths beside this compiler`` () =
    let shipped = (CheckedLibrary.read CheckedLibrary.path CheckedLibrary.libraryHash.Value).Value.Shipped
    Assert.Equal<string list>(
        [ "nanyx.console.core/src/console.nyx"
          "nanyx.console.core/src/env.nyx"
          "nanyx.console/src/console.host.nyx"
          "nanyx.console/src/console.prelude.nyx"
          "nanyx.fs/src/dir.nyx"
          "nanyx.fs/src/file.nyx"
          "nanyx.fs/src/filesystem.nyx"
          "nanyx.fs/src/path.nyx"
          "nanyx.http.core/src/client.nyx"
          "nanyx.http.core/src/http.nyx"
          "nanyx.http.core/src/request.nyx"
          "nanyx.http.core/src/response.nyx" ],
        shipped |> List.choose (fun entry -> ShippedPackages.tryDisplay entry.Key) |> List.sort)
    for entry in shipped do
        for file, _ in entry.Read do
            Assert.True(File.Exists file, $"{file} is missing")

[<Fact>]
let ``A compile of a loose script takes the console host's modules from the checked library`` () =
    let recording = Recording()
    let result = Compiler.compileReporting recording Linters.none """dbg("hello")"""
    Assert.True(result.TypeChecked, sprintf "%A" result.Diagnostics)
    Assert.Equal<string list>([ "start <source>" ], recording.Events |> Seq.filter (fun event -> event.StartsWith "start ") |> List.ofSeq)
    Assert.Contains($"reused {consoleHostFile}", recording.Events)

[<Fact>]
let ``A shipped module whose files have changed is not taken from the checked library`` () =
    Assert.True((CheckedLibrary.tryFindShipped consoleFile File.ReadAllText).IsSome)
    let changed (path: string) =
        if path = consoleFile then File.ReadAllText path + "\ndef changed = 1\n" else File.ReadAllText path
    Assert.True((CheckedLibrary.tryFindShipped consoleFile changed).IsNone)