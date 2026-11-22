module TranspilerWasmTests.Features.Commands.Parse

open System.IO
open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Parse - a script's syntax tree is printed`` () =
    use directory = new TempDirectory()
    let path = Path.Combine(directory.Path, "main.nyx")
    File.WriteAllText(path, "def answer = 42\n")
    let outcome = nanyxWithInput "" [ "parse"; path ]
    Assert.Equal(0, outcome.ExitCode)
    Assert.StartsWith("Parsed successfully:", normalizeOutput outcome.StandardOutput)

// Parsing recurses once per branch of an else-if, as checking does, so it runs on a large stack too (#2375): 4,000
// branches overflowed the main thread's.
[<Fact>]
let ``Parse - an else-if chain of 4,000 branches is parsed`` () =
    use directory = new TempDirectory()
    let path = Path.Combine(directory.Path, "main.nyx")
    let branches = [ for i in 1 .. 4_000 -> $"if n == {i} then {i * 2}" ] |> String.concat " else "
    File.WriteAllText(path, $"def f = {{ n: int -> {branches} else 0 }}\n")
    let outcome = nanyxWithInput "" [ "parse"; path ]
    Assert.Equal(0, outcome.ExitCode)
    Assert.StartsWith("Parsed successfully:", normalizeOutput outcome.StandardOutput)
