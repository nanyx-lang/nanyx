/// A type or name error in a module a program imports is deferred, as one in the file being run is: the module is
/// imported, its other definitions work, and the program stops where it reaches the error, which names the file and
/// line in that module. An error that can't be deferred, such as a syntax error, still stops the build (#2967). A frame
/// of a module the program imports names its function as the backend does, after its module (#3221).
module TranspilerWasmTests.Features.Errors.ImportedDeferredErrors

open Xunit
open TranspilerWasmTestHelpers

/// A module whose `perimeter` has a type error and whose `area` has none.
let private shapes =
    "export module shapes\n\nexport def area: int -> int = { n -> n * n }\n\nexport def perimeter: int -> int = { n -> n * \"4\" }\n"

let private deferredAt = "src/shapes.nyx:5:43: error NYX0201: Type mismatch: 'string' is not a number, so it cannot be used with * (deferred from compile time)\n"

[<Fact>]
let ``nanyx run of an entry that imports a module with a type error runs up to the call that reaches it`` () =
    let main = "module main\n\nimport shapes as (area, perimeter)\n\n@entry\ndef main = {\n  println(area(3))\n  println(perimeter(3))\n  println(\"done\")\n}\n"
    let outcome, _ = nanyxOnPackage [ "shapes.nyx", shapes; "main.nyx", main ] [ "run"; "{package}" ]
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("9\n", outcome.StandardOutput.ReplaceLineEndings "\n")
    let errors = outcome.StandardError.ReplaceLineEndings "\n"
    Assert.Contains("Running with 1 deferred error\n", errors)
    Assert.Contains(deferredAt + "  shapes_perimeter (src/shapes.nyx:5)\n  main (src/main.nyx:8)\n", errors)

[<Fact>]
let ``A module's deferred error reached through a module that imports it names the module it is in`` () =
    let edges = "export module edges\n\nimport shapes as (perimeter)\n\nexport def edge: int -> int = { n -> perimeter(n) + 1 }\n"
    let main = "module main\n\nimport edges as (edge)\n\n@entry\ndef main = {\n  println(\"start\")\n  println(edge(3))\n}\n"
    let outcome, _ = nanyxOnPackage [ "shapes.nyx", shapes; "edges.nyx", edges; "main.nyx", main ] [ "run"; "{package}" ]
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("start\n", outcome.StandardOutput.ReplaceLineEndings "\n")
    Assert.Contains(deferredAt + "  shapes_perimeter (src/shapes.nyx:5)\n  edges_edge (src/edges.nyx:5)\n  main (src/main.nyx:8)\n", outcome.StandardError.ReplaceLineEndings "\n")

[<Fact>]
let ``A module two others import reports its deferred error once`` () =
    let left = "export module left\n\nimport shapes as (perimeter)\n\nexport def leftEdge: int -> int = { n -> perimeter(n) }\n"
    let right = "export module right\n\nimport shapes as (perimeter)\n\nexport def rightEdge: int -> int = { n -> perimeter(n) + 1 }\n"
    let main =
        "module main\n\nimport shapes as (area)\nimport left as (leftEdge)\nimport right as (rightEdge)\n\n@entry\ndef main = {\n  println(area(3))\n  println(rightEdge(3))\n  println(leftEdge(3))\n}\n"
    let outcome, _ = nanyxOnPackage [ "shapes.nyx", shapes; "left.nyx", left; "right.nyx", right; "main.nyx", main ] [ "run"; "{package}" ]
    let errors = outcome.StandardError.ReplaceLineEndings "\n"
    Assert.Contains("Running with 1 deferred error\n", errors)
    // The diagnostic, then the crash: the error's span is in each once.
    let occurrences (text: string) (part: string) = (text.Split part).Length - 1
    Assert.Equal(1, occurrences errors "error NYX0201: Type mismatch\n")
    Assert.Equal(1, occurrences errors deferredAt)
    Assert.Equal("9\n", outcome.StandardOutput.ReplaceLineEndings "\n")
    Assert.Contains("  right_rightEdge (src/right.nyx:5)\n", errors)

[<Fact>]
let ``nanyx check and a release build of an entry that imports a module with a type error fail`` () =
    let main = "module main\n\nimport shapes as (area)\n\n@entry\ndef main = { println(area(3)) }\n"
    let checkedOutcome, _ = nanyxOnPackage [ "shapes.nyx", shapes; "main.nyx", main ] [ "check"; "{package}" ]
    Assert.Equal(1, checkedOutcome.ExitCode)
    Assert.Contains("NYX0201", checkedOutcome.StandardOutput)
    let released, wrote = nanyxOnPackage [ "shapes.nyx", shapes; "main.nyx", main ] [ "build"; "--release"; "{package}" ]
    Assert.Equal(1, released.ExitCode)
    Assert.False(wrote)

[<Fact>]
let ``An imported module with a syntax error still stops the run`` () =
    let broken = "export module shapes\n\nexport def area: int -> int = { n -> n * }\n"
    let main = "module main\n\nimport shapes as (area)\n\n@entry\ndef main = {\n  println(\"start\")\n  println(area(3))\n}\n"
    let outcome, _ = nanyxOnPackage [ "shapes.nyx", broken; "main.nyx", main ] [ "run"; "{package}" ]
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("", outcome.StandardOutput)
    Assert.DoesNotContain("deferred", outcome.StandardError)
