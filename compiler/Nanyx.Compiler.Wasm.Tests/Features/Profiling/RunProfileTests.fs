/// `nanyx run --profile`: Wasmtime's guest profiler samples the run and writes the Firefox profiler's format, with the
/// functions Nanyx names (#2044, see `GuestProfiler`). A profiled run is in a process of its own (`nanyxProcess`): in
/// this one, a collection of garbage that other tests bring on stops the thread that times the samples, and a run of a
/// tenth of a second can end in the pause with no sample taken (#2491).
module TranspilerWasmTests.Features.Profiling.Run

open System.IO
open System.Text.Json.Nodes
open Xunit
open TranspilerWasmTestHelpers

/// A program that runs long enough, about a tenth of a second, to be sampled several times, mostly in `fib`.
let private busy =
    """def fib: int -> int = { n -> if n < 2 then n else fib(n - 1) + fib(n - 2) }

println(fib(35))
"""

[<Fact>]
let ``run --profile writes a profile whose samples name the program's functions`` () =
    use directory = new TempDirectory()
    directory.Write "main.nyx" busy
    let profile = Path.Combine(directory.Path, "out", "main.json")
    let outcome = nanyxProcess [ "run"; "--profile"; profile; Path.Combine(directory.Path, "main.nyx") ]
    Assert.Equal(0, outcome.ExitCode)
    Assert.Equal("9227465\n", outcome.StandardOutput.ReplaceLineEndings "\n")
    Assert.Contains("Profile written:", outcome.StandardError)
    let thread = JsonNode.Parse(File.ReadAllText profile).["threads"].[0]
    let names = thread.["stringArray"].AsArray() |> Seq.map (fun name -> name.GetValue<string>()) |> List.ofSeq
    Assert.Contains("fib", names)
    Assert.True(thread.["samples"].["length"].GetValue<int>() > 0)

[<Fact>]
let ``run --profile gives each function the file and line of its def`` () =
    use directory = new TempDirectory()
    directory.Write "main.nyx" ("-- fib, with its type on a line of its own.\ndef fib\n  : int -> int\n  = { n -> if n < 2 then n else fib(n - 1) + fib(n - 2) }\n\nprintln(fib(35))\n")
    let profile = Path.Combine(directory.Path, "main.json")
    let outcome = nanyxProcess [ "run"; "--profile"; profile; Path.Combine(directory.Path, "main.nyx") ]
    Assert.Equal(0, outcome.ExitCode)
    let thread = JsonNode.Parse(File.ReadAllText profile).["threads"].[0]
    let strings = thread.["stringArray"].AsArray() |> Seq.map (fun text -> text.GetValue<string>()) |> Array.ofSeq
    let functions = thread.["funcTable"]
    let placeOf (functionName: string) =
        let names = functions.["name"].AsArray() |> Seq.map (fun name -> strings.[name.GetValue<int>()]) |> List.ofSeq
        let index =
            match names |> List.tryFindIndex ((=) functionName) with
            | Some index -> index
            | None -> failwith $"""No sample is in {functionName}; the profile's functions are [{String.concat ", " names}]"""
        let file = functions.["fileName"].[index]
        let line = functions.["lineNumber"].[index]
        (if isNull file then None else Some strings.[file.GetValue<int>()]), (if isNull line then None else Some(line.GetValue<int>()))
    // The line its `def` starts on, not its lambda's.
    Assert.Equal((Some "main.nyx", Some 2), placeOf "fib")
    // A runtime helper has no place.
    Assert.Equal((None, None), placeOf "__nanyx_start")

[<Fact>]
let ``run --profile names a function as it is written, though the backend names a def that shadows the library's otherwise`` () =
    use directory = new TempDirectory()
    // The prelude exports an `ignore` too, so the backend names this one `main_ignore` (#3223).
    directory.Write "main.nyx" "def ignore: int -> int = { n -> if n < 2 then n else ignore(n - 1) + ignore(n - 2) }\n\nprintln(ignore(35))\n"
    let profile = Path.Combine(directory.Path, "main.json")
    let outcome = nanyxProcess [ "run"; "--profile"; profile; Path.Combine(directory.Path, "main.nyx") ]
    Assert.Equal(0, outcome.ExitCode)
    let thread = JsonNode.Parse(File.ReadAllText profile).["threads"].[0]
    let strings = thread.["stringArray"].AsArray() |> Seq.map (fun text -> text.GetValue<string>()) |> Array.ofSeq
    let names = thread.["funcTable"].["name"].AsArray() |> Seq.map (fun name -> strings.[name.GetValue<int>()]) |> List.ofSeq
    Assert.Contains("ignore", names)
    Assert.DoesNotContain("main_ignore", names)

[<Fact>]
let ``run --profile with a memory check is a usage error`` () =
    use directory = new TempDirectory()
    directory.Write "main.nyx" "println(1)\n"
    let outcome = nanyxWithInput "" [ "run"; "--check-memory"; "--profile"; "main.json"; Path.Combine(directory.Path, "main.nyx") ]
    Assert.Equal(2, outcome.ExitCode)
    Assert.Contains("--profile can't be given with --check-memory", outcome.StandardError)
