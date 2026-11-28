/// A def that divides two of its parameters with their types unwritten, called with two dimensions (#2880): the program
/// runs with the mismatch deferred and stops where it is, with no internal error in the backend.
module TranspilerWasmTests.Features.Units.RunDimensionDivision

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a call that divides two dimensions in a def that writes no types stops at its deferred mismatch`` () =
    let outcome =
        runSource "dimension Distance\n@baseUnit unit cm: Distance\n\ndef speed = { d, t -> d / t }\n\nprintln(speed(100cm, 2s))\n"
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("", outcome.StandardOutput)
    Assert.Contains("error NYX0201", outcome.StandardError)
    Assert.Contains("write the parameters' types", outcome.StandardError)
    Assert.DoesNotContain("NYX0302", outcome.StandardError)
