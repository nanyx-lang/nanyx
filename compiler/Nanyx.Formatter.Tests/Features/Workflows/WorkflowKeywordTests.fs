module Nanyx.Formatter.Tests.Features.Workflows.WorkflowKeywords

open Xunit
open Nanyx.Formatter
open Nanyx.Formatter.Tests.TestHelpers

[<Fact>]
let ``A workflow keyword in an argument is written without brackets, as it ends at the argument's comma`` () =
    unchanged "def f = proc { return P(a = try num(x), b = try num(y)) }\n"
    unchanged "def f = proc { return g(try a + 1, try! b, return c, d) }\n"
    unchanged "def f = proc { return [try a, try b] }\n"
    unchanged "def f = proc { return [try a => 1, 2 => try b] }\n"
    unchanged "def f = proc { return r with (a = try a, b = try b) }\n"
    formatsTo "def f = proc { return P(a = try num(x), b = try num(y)) }" "def f = proc { return P(a = (try num(x)), b = (try num(y))) }"

[<Fact>]
let ``A workflow keyword in an argument on a line of its own is written without brackets`` () =
    let source (major: string) (minor: string) =
        lines [
            "def f = proc {"
            "  return SemanticVersion("
            $"    major = {major}"
            $"    minor = {minor}"
            "    patch = parseNumber(patch) \\orElse(0) \\withTheDefaultsOfTheLibrary(defaults)"
            "    tag = tag"
            "  )"
            "}"
        ]
        |> _.TrimEnd('\n')
    formatsTo (source "try parseNumber(major)" "try parseNumber(minor)") (source "(try parseNumber(major))" "(try parseNumber(minor))")

[<Fact>]
let ``A workflow keyword an operator follows keeps its brackets`` () =
    unchanged "def f = proc { return [(try a) + 1, b] }\n"
    unchanged "def f = proc { return g((try a) \\h, b) }\n"
