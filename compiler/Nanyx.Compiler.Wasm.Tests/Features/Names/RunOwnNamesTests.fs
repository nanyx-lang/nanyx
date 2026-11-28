/// A program's own definitions and imports named like a definition the library exports (#3221): the program's code calls
/// its own, or its import's, and the library's code and the host's keep calling the library's. An own definition that
/// shadows one of the library's is named after its module in the backend, and the program's own imports are inlined
/// under qualified names, as other packages' are (see `CheckedModule.OwnRenames`, `ModuleLinking.inlinedRenames`).
module TranspilerWasmTests.Features.Names.OwnNames

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``A script's own def named like a prelude function the host calls runs, and the host calls the prelude's`` () =
    // The console host ends with `ignore(provide(entry))`, the prelude's `ignore`.
    runSource "def ignore = { s: string -> s ++ \"!\" }\n\nprintln(ignore(\"hi\"))\n" |> assertRan "hi!\n"

[<Fact>]
let ``A prelude function that calls another keeps calling the prelude's, beside a def of the callee's name`` () =
    // The prelude's `orCrash` calls its `crash`.
    runSource "def crash = { s: string -> \"crashed: \" ++ s }\n\nprintln(#some(5) \\orCrash(\"none\"))\nprintln(crash(\"y\"))\n"
    |> assertRan "5\ncrashed: y\n"

[<Fact>]
let ``An import's function named like a prelude function is the one called, with none of the prelude's contexts`` () =
    // The prelude's `ignore` takes any value and its `memory` requires `Memory`: neither is what the program calls.
    let utils = "module utils\n\nexport def ignore: string -> string = { s -> s ++ \"?\" }\n\nexport def memory: string -> string = { s -> s ++ \"#\" }\n"
    let main = "module main\n\nimport utils\n\n@entry\ndef main = {\n  println(ignore(\"x\"))\n  println(memory(\"y\"))\n}\n"
    let ran, _ = nanyxOnPackage [ "utils.nyx", utils; "main.nyx", main ] [ "run"; "{package}" ]
    Assert.Equal("", ran.StandardError)
    Assert.Equal("x?\ny#\n", ran.StandardOutput)

[<Fact>]
let ``A module the program imports may define a name the prelude exports, and its own code calls its own`` () =
    let shapes = "module shapes\n\ndef crash = { s: string -> \"bent: \" ++ s }\n\nexport def describe: int -> string = { n -> crash(\"{n}\") }\n"
    let main = "module main\n\nimport shapes as (describe)\n\n@entry\ndef main = {\n  println(describe(3))\n  println(#some(1) \\orCrash(\"none\"))\n}\n"
    let ran, _ = nanyxOnPackage [ "shapes.nyx", shapes; "main.nyx", main ] [ "run"; "{package}" ]
    Assert.Equal("", ran.StandardError)
    Assert.Equal("bent: 3\n1\n", ran.StandardOutput)
