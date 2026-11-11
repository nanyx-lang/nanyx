module TranspilerWasmTests.Features.Examples.Run

open System.IO
open Xunit
open TranspilerWasmTestHelpers

let private examplePath (example: string) =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "..", "..", "examples", example))

let private assertExamplePrints (expected: string) (example: string) =
    let outcome = nanyxRun (examplePath example)
    Assert.True((outcome.ExitCode = 0), $"Expected exit code 0, got {outcome.ExitCode}. stderr:\n{outcome.StandardError}")
    Assert.Equal(expected.ReplaceLineEndings "\n", outcome.StandardOutput.ReplaceLineEndings "\n")
    Assert.Equal("", outcome.StandardError)

[<Fact>]
let ``Running - source-generator.nyx prints the generated C#`` () =
    "source-generator.nyx"
    |> assertExamplePrints
        """using System;

namespace Demo
{
partial record Person
{
public object this[string name]
{
    get => name switch
    {
        "Name" => Name,
        "Age" => Age,
        _ => throw new IndexOutOfRangeException(name)
    };
    set
    {
        switch (name)
        {
            case "Name": Name = (string) value; break;
            default: throw new IndexOutOfRangeException(name);
        }
    }
}
}
}
"""

[<Fact>]
let ``Running - fizzbuzzbaz.nyx prints the numbers with their words`` () =
    "fizzbuzzbaz.nyx"
    |> assertExamplePrints
        """1
2
fizz
4
buzz
fizz
baz
8
fizz
buzz
11
fizz
13
baz
fizzbuzz
16
17
fizz
19
buzz
fizzbaz
22
23
fizz
buzz
26
fizz
baz
29
fizzbuzz
"""
