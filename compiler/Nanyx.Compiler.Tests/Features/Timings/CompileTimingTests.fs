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

/// A listener that writes down what it is told, as "file <path>", "start <name>", "end <name>" and "reused <name>".
type private Recording() =
    let events = Collections.Generic.List<string>()
    member _.Events = List.ofSeq events
    interface CompileListener with
        member _.FileStarted file = events.Add $"file {file}"
        member _.ModuleStarted name = events.Add $"start {name}"
        member _.ModuleFinished(name, _) = events.Add $"end {name}"
        member _.ModuleReused name = events.Add $"reused {name}"

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
    // util is checked as main's import, so its own file reuses that check.
    Assert.Equal<string list>(
        [ $"file {main}"; $"start {main}"; $"start {util}"; $"end {util}"; $"end {main}"; $"file {util}"; $"reused {util}" ],
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

let private seconds (value: float) = TimeSpan.FromSeconds value

let private timing name total self = { Name = name; Total = seconds total; Self = seconds self }

/// The checks of `checks` of the modules whose names hold `root`, each with the same of its imports.
let rec private ownChecks (root: string) (checks: ModuleCheck list) =
    checks
    |> List.choose (function
        | Checked(timing, imports) when timing.Name.Contains root -> Some (Checked(timing, ownChecks root imports))
        | Reused name when name.Contains root -> Some (Reused name)
        | _ -> None)

[<Fact>]
let ``A build's timings are a tree of its files' checks, with a module imported again cached`` () =
    use package =
        new TempPackage(
            [ "demo.nyxp", manifest
              "src/main.nyx", "module main\n\nimport a\nimport b\n\n@entry\ndef main = {\n  println(\"{one + two}\")\n}\n"
              "src/a.nyx", "module a\n\nimport c\n\nexport def one = zero + 1\n"
              "src/b.nyx", "module b\n\nimport c\n\nexport def two = zero + 2\n"
              "src/c.nyx", "module c\n\nexport def zero = 0\n" ])
    let found = okOf (Package.find package.Root)
    let timings = CompileTimings()
    let build = Package.buildReporting timings found
    Assert.Empty(build.Diagnostics)
    // The files are compiled in order of their paths, and each module is checked once.
    let tree = ModuleCheck.tree Path.GetFileName (ownChecks package.Root timings.Checks)
    let shape = Text.RegularExpressions.Regex.Replace(tree, @" \((\d+\.\d\ds of )?\d+\.\d\ds\)", "")
    let expected =
        """a.nyx
└── c.nyx
b.nyx
└── c.nyx (cached)
c.nyx (cached)
main.nyx
├── a.nyx (cached)
└── b.nyx (cached)"""
    Assert.Equal(expected.ReplaceLineEndings "\n", shape)

[<Fact>]
let ``Timings nest each check under its importer's, and take its time off the importer's own`` () =
    let timings = CompileTimings()
    let listener = timings :> CompileListener
    listener.ModuleStarted "main"
    listener.ModuleStarted "util"
    listener.ModuleStarted "deep"
    listener.ModuleFinished("deep", seconds 1.0)
    listener.ModuleFinished("util", seconds 3.0)
    listener.ModuleFinished("main", seconds 10.0)
    Assert.Equal<ModuleCheck list>(
        [ Checked(timing "main" 10.0 7.0, [ Checked(timing "util" 3.0 2.0, [ Checked(timing "deep" 1.0 1.0, []) ]) ]) ],
        timings.Checks)

[<Fact>]
let ``Timings list a check's imports in the order they were checked, not by time`` () =
    let timings = CompileTimings()
    let listener = timings :> CompileListener
    listener.ModuleStarted "main"
    listener.ModuleStarted "fast"
    listener.ModuleFinished("fast", seconds 1.0)
    listener.ModuleStarted "slow"
    listener.ModuleFinished("slow", seconds 5.0)
    listener.ModuleFinished("main", seconds 7.0)
    Assert.Equal<ModuleCheck list>(
        [ Checked(timing "main" 7.0 1.0, [ Checked(timing "fast" 1.0 1.0, []); Checked(timing "slow" 5.0 5.0, []) ]) ],
        timings.Checks)

[<Fact>]
let ``Timings record a module needed again as reused, with nothing under it`` () =
    let timings = CompileTimings()
    let listener = timings :> CompileListener
    listener.ModuleStarted "main"
    listener.ModuleStarted "a"
    listener.ModuleStarted "c"
    listener.ModuleFinished("c", seconds 1.0)
    listener.ModuleFinished("a", seconds 2.0)
    listener.ModuleStarted "b"
    listener.ModuleReused "c"
    listener.ModuleFinished("b", seconds 1.0)
    listener.ModuleFinished("main", seconds 4.0)
    Assert.Equal<ModuleCheck list>(
        [ Checked(
              timing "main" 4.0 1.0,
              [ Checked(timing "a" 2.0 1.0, [ Checked(timing "c" 1.0 1.0, []) ]); Checked(timing "b" 1.0 1.0, [ Reused "c" ]) ]
          ) ],
        timings.Checks)

[<Fact>]
let ``Timings keep a root for each module needed outside any check`` () =
    let timings = CompileTimings()
    let listener = timings :> CompileListener
    listener.ModuleStarted "main"
    listener.ModuleFinished("main", seconds 2.0)
    listener.ModuleReused "main"
    listener.ModuleStarted "other"
    listener.ModuleFinished("other", seconds 1.0)
    Assert.Equal<ModuleCheck list>(
        [ Checked(timing "main" 2.0 2.0, []); Reused "main"; Checked(timing "other" 1.0 1.0, []) ],
        timings.Checks)

[<Fact>]
let ``Timings pass everything on to the listener they wrap`` () =
    let recording = Recording()
    let listener = CompileTimings(recording) :> CompileListener
    listener.FileStarted "a.nyx"
    listener.ModuleStarted "a"
    listener.ModuleFinished("a", TimeSpan.Zero)
    listener.ModuleReused "a"
    Assert.Equal<string list>([ "file a.nyx"; "start a"; "end a"; "reused a" ], recording.Events)

[<Fact>]
let ``The timings tree shows each module's own and total time, with what it needed under it`` () =
    let tree =
        ModuleCheck.tree
            (fun name -> name.Replace("/work/", ""))
            [ Checked(
                  timing "/work/src/main.nyx" 0.28 0.03,
                  [ Checked(timing "/work/src/dependencyA.nyx" 0.25 0.05, [ Checked(timing "/work/src/dependencyC.nyx" 0.2 0.2, []) ])
                    Reused "/work/src/dependencyB.nyx" ]
              )
              Checked(timing "nanyx/prelude" 1.1 0.8, [ Checked(timing "nanyx/list" 0.3 0.3, []) ]) ]
    let expected =
        """src/main.nyx (0.03s of 0.28s)
├── src/dependencyA.nyx (0.05s of 0.25s)
│   └── src/dependencyC.nyx (0.20s)
└── src/dependencyB.nyx (cached)
nanyx/prelude (0.80s of 1.10s)
└── nanyx/list (0.30s)"""
    Assert.Equal(expected.ReplaceLineEndings "\n", tree)

[<Fact>]
let ``The timings tree shows one time when a module's own and total time are the same as written`` () =
    let tree =
        ModuleCheck.tree
            id
            [ Checked(timing "main" 0.034 0.031, [ Checked(timing "util" 0.0012 0.0012, []) ])
              Checked(timing "other" 0.5 0.2, [ Checked(timing "leaf" 0.3 0.3, []) ]) ]
    let expected =
        """main (0.03s)
└── util (0.00s)
other (0.20s of 0.50s)
└── leaf (0.30s)"""
    Assert.Equal(expected.ReplaceLineEndings "\n", tree)

let private runningCheck name started imports = { Name = name; Started = seconds started; Imports = imports }

[<Fact>]
let ``The live view shows each check under way with its time so far, and its finished imports on one line each`` () =
    let frame =
        ModuleCheck.live
            (fun name -> name.Replace("/work/", ""))
            { Finished = []
              Running =
                [ runningCheck "/work/src/main.nyx" 0.0 [ Checked(timing "/work/src/dependencyA.nyx" 0.25 0.05, [ Checked(timing "/work/src/dependencyC.nyx" 0.2 0.2, []) ]) ]
                  runningCheck "/work/src/dependencyB.nyx" 0.3 []
                  runningCheck "nanyx/prelude" 0.4 [] ]
              Now = seconds 1.2 }
            24
    let expected =
        """src/main.nyx (1.2s…)
├── src/dependencyA.nyx (0.05s of 0.25s)
└── src/dependencyB.nyx (0.9s…)
    └── nanyx/prelude (0.8s…)"""
    Assert.Equal(expected.ReplaceLineEndings "\n", String.concat "\n" frame)

[<Fact>]
let ``The live view's times tick on as the checks under way go on`` () =
    let soFar now =
        { Finished = []; Running = [ runningCheck "main" 0.5 []; runningCheck "util" 1.0 [] ]; Now = seconds now }
    Assert.Equal<string list>([ "main (1.0s…)"; "└── util (0.5s…)" ], ModuleCheck.live id (soFar 1.5) 24)
    Assert.Equal<string list>([ "main (4.2s…)"; "└── util (3.7s…)" ], ModuleCheck.live id (soFar 4.7) 24)

[<Fact>]
let ``The live view draws a finished check, and a cached module, on one line under the check under way`` () =
    let frame =
        ModuleCheck.live
            id
            { Finished = [ Checked(timing "a" 2.0 1.0, [ Checked(timing "c" 1.0 1.0, []) ]); Checked(timing "d" 0.5 0.5, []) ]
              Running = [ runningCheck "main" 0.0 [ Checked(timing "b" 1.0 0.5, [ Reused "c" ]); Reused "a" ] ]
              Now = seconds 3.0 }
            24
    let expected =
        """a (1.00s of 2.00s)
d (0.50s)
main (3.0s…)
├── b (0.50s of 1.00s)
└── a (cached)"""
    Assert.Equal(expected.ReplaceLineEndings "\n", String.concat "\n" frame)

[<Fact>]
let ``The live view keeps the lines nearest the check under way when it is taller than the terminal`` () =
    let frame =
        ModuleCheck.live
            id
            { Finished = [ for name in [ "a"; "b"; "c"; "d" ] -> Checked(timing name 1.0 1.0, []) ]
              Running = [ runningCheck "main" 0.0 [ Reused "a"; Reused "b" ]; runningCheck "util" 1.0 [] ]
              Now = seconds 2.0 }
            4
    let expected =
        """… 5 more
├── a (cached)
├── b (cached)
└── util (1.0s…)"""
    Assert.Equal(expected.ReplaceLineEndings "\n", String.concat "\n" frame)

[<Fact>]
let ``Timings so far are the finished checks and the chain of checks under way, outermost first`` () =
    let timings = CompileTimings()
    let listener = timings :> CompileListener
    listener.ModuleStarted "done"
    listener.ModuleFinished("done", seconds 1.0)
    listener.ModuleStarted "main"
    listener.ModuleStarted "a"
    listener.ModuleFinished("a", seconds 1.0)
    listener.ModuleReused "done"
    listener.ModuleStarted "b"
    let soFar = timings.SoFar
    Assert.Equal<ModuleCheck list>([ Checked(timing "done" 1.0 1.0, []) ], soFar.Finished)
    Assert.Equal<string list>([ "main"; "b" ], soFar.Running |> List.map _.Name)
    Assert.Equal<ModuleCheck list>([ Checked(timing "a" 1.0 1.0, []); Reused "done" ], soFar.Running.Head.Imports)
    Assert.True(soFar.Running |> List.forall (fun check -> check.Started <= soFar.Now))
