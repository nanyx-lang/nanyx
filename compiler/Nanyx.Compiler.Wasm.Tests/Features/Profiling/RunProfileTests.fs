/// `nanyx run --profile`: Wasmtime's guest profiler samples the run and writes the Firefox profiler's format, with the
/// functions Nanyx names (#2044, see `GuestProfiler`).
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
    let outcome = nanyxWithInput "" [ "run"; "--profile"; profile; Path.Combine(directory.Path, "main.nyx") ]
    Assert.Equal(0, outcome.ExitCode)
    Assert.Equal("9227465\n", outcome.StandardOutput.ReplaceLineEndings "\n")
    Assert.Contains("Profile written:", outcome.StandardError)
    let thread = JsonNode.Parse(File.ReadAllText profile).["threads"].[0]
    let names = thread.["stringArray"].AsArray() |> Seq.map (fun name -> name.GetValue<string>()) |> List.ofSeq
    Assert.Contains("fib", names)
    Assert.True(thread.["samples"].["length"].GetValue<int>() > 0)

[<Fact>]
let ``run --profile with a memory check is a usage error`` () =
    use directory = new TempDirectory()
    directory.Write "main.nyx" "println(1)\n"
    let outcome = nanyxWithInput "" [ "run"; "--check-memory"; "--profile"; "main.json"; Path.Combine(directory.Path, "main.nyx") ]
    Assert.Equal(2, outcome.ExitCode)
    Assert.Contains("--profile can't be given with --check-memory", outcome.StandardError)
