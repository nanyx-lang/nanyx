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
def r: #some(int) | #failed(string) = #failed("x")
def mapped = r \ mapSome({ n -> 1.5 })
def listed = name \ someToList"""

let private withStrings =
    normalized """def name: #some(string) | #empty = #some("a")
def called = mapSome(name, { s -> true })
def r: #some(string) | #failed(int) = #failed(1)
def mapped = r \ mapSome({ s -> [s] })
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
        [ "called", "#some(int) | #empty"; "listed", "list(int)"; "mapped", "#some(float) | #failed(string)"; "name", "#some(int) | #empty"; "r", "#some(int) | #failed(string)" ],
        intTypes |> List.filter (fun (name, _) -> List.contains name [ "called"; "listed"; "mapped"; "name"; "r" ]))
    Assert.Equal<(string * string) list>(
        [ "called", "#some(bool) | #empty"; "listed", "list(string)"; "mapped", "#some(list(string)) | #failed(int)"; "name", "#some(string) | #empty"; "r", "#some(string) | #failed(int)" ],
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
    let file = CheckedLibrary.read CheckedLibrary.path CheckedLibrary.build
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
    Assert.True((CheckedLibrary.read CheckedLibrary.path (CheckedLibrary.build + "0")).IsNone)

// A process reads a module of the checked library only when it imports it (#1852).

[<Fact>]
let ``Reading the checked library reads none of its modules until one is asked for`` () =
    let file = (CheckedLibrary.read CheckedLibrary.path CheckedLibrary.build).Value
    let read () = file.Checked |> Map.filter (fun _ checking -> checking.IsValueCreated) |> Map.keys |> List.ofSeq
    Assert.Empty(read ())
    match file.Checked[preludeKey].Value with
    | Ok prelude -> Assert.NotEmpty(prelude.Exports)
    | Error (phase, diagnostics) -> failwith $"the prelude stopped at {phase}: %A{diagnostics}"
    Assert.Equal<string list>([ preludeKey ], read ())

[<Fact>]
let ``A value read back from its part refers to what was written before the part, and shares what it holds twice`` () =
    let name = "shared"
    let inner = [ name; name ]
    let value = (inner, lazy (inner, [ inner; inner ]), inner)
    use stream = new MemoryStream()
    use writer = new BinaryWriter(stream)
    BinaryData.write writer id (value.GetType()) value
    let bytes = stream.ToArray()
    let before, later, after = BinaryData.read bytes 0 id (value.GetType()) :?> string list * Lazy<string list * string list list> * string list
    Assert.False(later.IsValueCreated)
    Assert.Equal<string list>(inner, after)
    let held, twice = later.Value
    Assert.Equal<string list>(inner, held)
    Assert.True(obj.ReferenceEquals(before, held))
    Assert.True(obj.ReferenceEquals(twice[0], twice[1]))

type private Shape =
    | Circle of radius: float
    | Square of side: int * label: string
    | Empty

type private Sample =
    { Name: string
      Counts: Map<string, int>
      Seen: Set<int64>
      Shapes: Shape list
      Day: System.DayOfWeek
      Letter: char
      Flag: bool
      Huge: bigint
      Small: byte
      Unsigned: uint32 * uint64
      Maybe: string option
      Nothing: unit }

[<Fact>]
let ``Each kind of value is read back as it was written`` () =
    let value =
        { Name = "sample"
          Counts = Map.ofList [ "b", 2; "a", -1; "c", System.Int32.MaxValue ]
          Seen = Set.ofList [ 5L; -3L; System.Int64.MinValue ]
          Shapes = [ Circle 1.5; Square(3, "three"); Empty; Square(-4, "sample") ]
          Day = System.DayOfWeek.Friday
          Letter = 'é'
          Flag = true
          Huge = bigint.Parse "-123456789012345678901234567890"
          Small = 200uy
          Unsigned = (System.UInt32.MaxValue, System.UInt64.MaxValue)
          Maybe = Some "maybe"
          Nothing = () }
    let roundTrip (value: 'T) =
        use stream = new MemoryStream()
        use writer = new BinaryWriter(stream)
        BinaryData.write writer id typeof<'T> value
        BinaryData.read (stream.ToArray()) 0 id typeof<'T> :?> 'T
    Assert.Equal(value, roundTrip value)
    Assert.Equal({ value with Maybe = None; Shapes = [] }, roundTrip { value with Maybe = None; Shapes = [] })

[<Fact>]
let ``FSharp.Core still has the internal tree types a map or set read back is built with`` () =
    Assert.True(
        BinaryData.treesBuiltDirectly (),
        "FSharp.Core's internal MapTree, MapTreeNode, SetTree or SetTreeNode constructors, or Map's or Set's constructor "
        + "from a comparer and a tree, were not found (has FSharp.Core been upgraded?). Reading the checked library still "
        + "works, but builds each map and set by adding its items one by one: see SortedMaps in BinaryData.fs."
    )

[<Fact>]
let ``A map or set read back is built from its sorted items and works as one built by adding them`` () =
    let roundTrip (value: 'T) =
        use stream = new MemoryStream()
        use writer = new BinaryWriter(stream)
        BinaryData.write writer id typeof<'T> value
        BinaryData.read (stream.ToArray()) 0 id typeof<'T> :?> 'T
    for count in 0 .. 40 do
        let map = Map.ofList [ for i in 1 .. count -> $"key{i}", i ]
        let read = roundTrip map
        Assert.Equal<Map<string, int>>(map, read)
        for KeyValue(k, v) in map do
            Assert.Equal(Some v, read.TryFind k)
        Assert.Equal<Map<string, int>>(map.Add("new", 0).Remove "key1", read.Add("new", 0).Remove "key1")
        let set = Set.ofList [ for i in 1 .. count -> int64 (i * 7 % 41) ]
        let readSet = roundTrip set
        Assert.Equal<Set<int64>>(set, readSet)
        Assert.True(set |> Set.forall readSet.Contains)
        Assert.Equal<Set<int64>>(set.Add(100L).Remove 7L, readSet.Add(100L).Remove 7L)

[<Fact>]
let ``Thousands of values held twice are shared when read back, in their part and from a later one`` () =
    let items = [ for i in 1 .. 5000 -> [ $"item{i}" ] ]
    let value = (items, lazy items, items)
    use stream = new MemoryStream()
    use writer = new BinaryWriter(stream)
    BinaryData.write writer id (value.GetType()) value
    let before, later, after = BinaryData.read (stream.ToArray()) 0 id (value.GetType()) :?> string list list * Lazy<string list list> * string list list
    Assert.Equal<string list list>(items, after)
    Assert.True(List.forall2 (fun a b -> obj.ReferenceEquals(a, b)) before after)
    Assert.True(List.forall2 (fun a b -> obj.ReferenceEquals(a, b)) before later.Value)

// The build checks the packages the compiler ships too (#1637), so a loose script does not check the console host.

let private consoleFile = Path.Combine(ShippedPackages.directoryOf "nanyx.cli", "src", "console.nyx")

[<Fact>]
let ``The checked library has the shipped packages' modules, with paths beside this compiler`` () =
    let shipped = (CheckedLibrary.read CheckedLibrary.path CheckedLibrary.build).Value.Shipped
    Assert.Equal<string list>(
        [ "nanyx.cli.host/src/cli.host.memory.nyx"
          "nanyx.cli.host/src/cli.host.nyx"
          "nanyx.cli.host/src/cli.host.wasi.files.nyx"
          "nanyx.cli.host/src/cli.prelude.nyx"
          "nanyx.cli/src/console.nyx"
          "nanyx.cli/src/env.nyx"
          "nanyx.fs/src/dir.nyx"
          "nanyx.fs/src/file.nyx"
          "nanyx.fs/src/filesystem.nyx"
          "nanyx.fs/src/path.nyx"
          "nanyx.http/src/client.nyx"
          "nanyx.http/src/http.nyx"
          "nanyx.http/src/request.nyx"
          "nanyx.http/src/response.nyx"
          "nanyx.http/src/wire.nyx"
          "nanyx.wasi/src/abi.nyx"
          "nanyx.wasi/src/cli.nyx"
          "nanyx.wasi/src/clocks.nyx"
          "nanyx.wasi/src/files.nyx"
          "nanyx.wasi/src/http.nyx"
          "nanyx.wasi/src/http.service.nyx"
          "nanyx.wasi/src/preview1.nyx"
          "nanyx.wasi/src/preview1.system.files.nyx"
          "nanyx.wasi/src/random.nyx"
          "nanyx.wasi/src/runtime.nyx"
          "nanyx.wasi/src/stdin.nyx"
          "nanyx.wasi/src/waits.nyx" ],
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
    // The console host's module is kept under its first file by name (see the test above).
    Assert.Contains($"""reused {consoleHostFile}""", recording.Events)

[<Fact>]
let ``A shipped module whose files have changed is not taken from the checked library`` () =
    Assert.True((CheckedLibrary.tryFindShipped consoleFile File.ReadAllText).IsSome)
    let changed (path: string) =
        if path = consoleFile then File.ReadAllText path + "\ndef changed = 1\n" else File.ReadAllText path
    Assert.True((CheckedLibrary.tryFindShipped consoleFile changed).IsNone)
// A compile needs the library's exports, for the hint an unknown name gets, and the modules the shipped packages' files
// declare, to resolve a loose script's host: the checked library has both, so that no compile parses every library file
// for them (#1852).

[<Fact>]
let ``The checked library has the library's exports, as its sources give them`` () =
    let file = (CheckedLibrary.read CheckedLibrary.path CheckedLibrary.build).Value
    Assert.Equal<Map<string, string list>>(ImportFixes.standardLibraryExports.Value, file.Exports)
    Assert.Equal<Map<string, string list>>(ImportFixes.standardLibraryExportPaths.Value, CheckedLibrary.standardLibraryExportPaths.Value)

[<Fact>]
let ``The checked library has the module each file of the console host's packages declares`` () =
    let files = (CheckedLibrary.read CheckedLibrary.path CheckedLibrary.build).Value.Files
    Assert.Equal(Some (Some ("console", true)), files |> List.tryFind (fun file -> file.Path = consoleFile) |> Option.map _.Declares)
    for file in files do
        Assert.Equal(File.ReadAllText file.Path, file.Text)
        Assert.Equal(Some file.Declares, PackageModules.declaration file.Path)

[<Fact>]
let ``A file's declaration is taken from elsewhere only while the file reads the same`` () =
    withTempDir (fun dir ->
        let path = Path.Combine(dir, "shapes.nyx")
        File.WriteAllText(path, "module shapes\n")
        PackageModules.knowDeclaration path "module other\n" (Some ("other", false))
        Assert.Equal(Some (Some ("shapes", false)), PackageModules.declaration path)
        let known = Path.Combine(dir, "known.nyx")
        File.WriteAllText(known, "module shapes\n")
        // The text is the file's, so the declaration given is taken, and the file is not parsed for it.
        PackageModules.knowDeclaration known "module shapes\n" (Some ("taken", true))
        Assert.Equal(Some (Some ("taken", true)), PackageModules.declaration known))
