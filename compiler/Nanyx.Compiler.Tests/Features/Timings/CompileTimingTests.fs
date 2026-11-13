module Nanyx.Compiler.Tests.Features.Timings.CompileTimings

open System
open System.IO
open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

let private manifest = "[package]\nname = \"demo\"\nversion = \"0.1.0\"\n"

let private okOf result =
    match result with
    | Result.Ok value -> value
    | Result.Error errors -> failwith (String.concat "\n" errors)

/// A listener that writes down what it is told, as "file <path>", "start <name>" and "end <name>".
type private Recording() =
    let events = Collections.Generic.List<string>()
    member _.Events = List.ofSeq events
    interface CompileListener with
        member _.FileStarted file = events.Add $"file {file}"
        member _.ModuleStarted name = events.Add $"start {name}"
        member _.ModuleFinished(name, _) = events.Add $"end {name}"

/// The standard library's modules are checked once per process, so which of them a compile reports depends on the
/// tests that ran before it: only the package's own modules are compared.
let private ownEvents (root: string) (events: string list) =
    events |> List.filter (fun event -> event.Contains root)

[<Fact>]
let ``A build reports each module's check as it starts and ends, inside its importer's, and each module once`` () =
    use package =
        new TempPackage(
            [ "demo.nyxp", manifest
              "src/main.nyx", "module main\n\nimport util\n\n@entry\ndef main = {\n  println(\"{one}\")\n}\n"
              "src/util.nyx", "module util\n\nexport def one = 1\n" ])
    let found = okOf (Package.find package.Root)
    let main, util = Path.GetFullPath(package.Path "src/main.nyx"), Path.GetFullPath(package.Path "src/util.nyx")
    let recording = Recording()
    let build = Package.buildReporting recording found
    Assert.Empty(build.Diagnostics)
    // util is checked as main's import, so its own file has nothing left to check.
    Assert.Equal<string list>(
        [ $"file {main}"; $"start {main}"; $"start {util}"; $"end {util}"; $"end {main}"; $"file {util}" ],
        ownEvents package.Root recording.Events)

[<Fact>]
let ``A script's compile reports its own module, with any standard-library check inside it`` () =
    withTempDir (fun dir ->
        let file = Path.Combine(dir, "script.nyx")
        File.WriteAllText(file, "@entry\ndef main = {\n  println(\"hi\")\n}\n")
        let recording = Recording()
        let result = Script.compileFileReporting recording file
        Assert.True(result.TypeChecked, sprintf "%A" result.Diagnostics)
        let events = recording.Events
        Assert.Equal($"start {Path.GetFullPath file}", List.head events)
        Assert.Equal($"end {Path.GetFullPath file}", List.last events)
        Assert.Equal<string list>([], ownEvents dir events |> List.filter (fun event -> event.StartsWith "file ")))

[<Fact>]
let ``Timings take an import's time off its importer's own`` () =
    let timings = CompileTimings()
    let listener = timings :> CompileListener
    listener.ModuleStarted "main"
    listener.ModuleStarted "util"
    listener.ModuleStarted "deep"
    listener.ModuleFinished("deep", TimeSpan.FromSeconds 1.0)
    listener.ModuleFinished("util", TimeSpan.FromSeconds 3.0)
    listener.ModuleFinished("main", TimeSpan.FromSeconds 10.0)
    Assert.Equal<ModuleTiming list>(
        [ { Name = "deep"; Total = TimeSpan.FromSeconds 1.0; Self = TimeSpan.FromSeconds 1.0 }
          { Name = "util"; Total = TimeSpan.FromSeconds 3.0; Self = TimeSpan.FromSeconds 2.0 }
          { Name = "main"; Total = TimeSpan.FromSeconds 10.0; Self = TimeSpan.FromSeconds 7.0 } ],
        timings.Timings)

[<Fact>]
let ``Timings pass everything on to the listener they wrap`` () =
    let recording = Recording()
    let listener = CompileTimings(recording) :> CompileListener
    listener.FileStarted "a.nyx"
    listener.ModuleStarted "a"
    listener.ModuleFinished("a", TimeSpan.Zero)
    Assert.Equal<string list>([ "file a.nyx"; "start a"; "end a" ], recording.Events)

[<Fact>]
let ``The timings table lists the slowest module first, with its total and own time`` () =
    let table =
        ModuleTiming.table
            [ { Name = "src/util.nyx"; Total = TimeSpan.FromSeconds 0.0315; Self = TimeSpan.FromSeconds 0.0315 }
              { Name = "nanyx/prelude"; Total = TimeSpan.FromSeconds 1.2; Self = TimeSpan.FromSeconds 1.0 }
              { Name = "src/main.nyx"; Total = TimeSpan.FromSeconds 1.5; Self = TimeSpan.FromSeconds 0.3 } ]
    let expected =
        """    total      self  module
   1.500s    0.300s  src/main.nyx
   1.200s    1.000s  nanyx/prelude
   0.032s    0.032s  src/util.nyx"""
    Assert.Equal(expected.ReplaceLineEndings "\n", table)
