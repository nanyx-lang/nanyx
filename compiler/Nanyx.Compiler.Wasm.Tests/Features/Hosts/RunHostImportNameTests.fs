/// A name a host, or a library, brings in from one of its own imports unqualified is its own: a program that defines a
/// def of the same name keeps its own, and the host keeps its (#2995). In a module that isn't the program's own, an
/// import's definitions are inlined under qualified names (see `ModuleLinking.inlinedRenames`).
module TranspilerWasmTests.Features.Hosts.HostImportNames

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``A script may define names the console host selects from its own imports, and both run with their own`` () =
    // The console host selects `within`, `bytesAt` and `stateAt` from `nanyx.wasi`, and reads standard input with them.
    let source =
        "def within: int -> int = { n -> n + 1 }\n\ndef stateAt: string -> string = { s -> s ++ \"!\" }\n\ndef bytesAt: int -> int = { n -> n * 2 }\n\ndef greeting = stateAt(\"hi\")\n\nprintln(\"{within(1)} {greeting} {bytesAt(21)}\")\n\nmatch readLine()\n  | #some(line) -> println(line)\n  | _ -> ()\n"
    runSourceWithInput "typed\n" (normalized source) |> assertRan "2 hi! 42\ntyped\n"

[<Fact>]
let ``A package under the console host may define a name its host selects, built as a core module and as a component`` () =
    let main = "module main\n\ndef within: int -> int = { n -> n + 1 }\n\n@entry\ndef main = { println(\"{within(1)}\") }\n"
    let ran, _ = nanyxOnPackage [ "main.nyx", main ] [ "run"; "{package}" ]
    Assert.Equal("2\n", ran.StandardOutput)
    let built, wrote = nanyxOnPackage [ "main.nyx", main ] [ "build"; "{package}" ]
    Assert.Equal(0, built.ExitCode)
    Assert.True(wrote)

[<Fact>]
let ``A program may define a name a library it depends on selects from one of its own modules`` () =
    use directory = new TempDirectory()
    directory.Write "util/acme.util.nyxp" """(package = (name = "acme.util", version = "0.1.0", sources = ["src/**/*.nyx"]))"""
    directory.Write "util/src/helpers.nyx" "export module helpers\n\nexport def helper: int -> int = { n -> n * 10 }\n"
    directory.Write "util/src/util.nyx" "export module util\n\nimport helpers as (helper)\n\nexport def twice: int -> int = { n -> helper(n) + helper(n) }\n"
    directory.Write
        "app/app.nyxp"
        $"""(package = (name = "app", version = "0.1.0", sources = ["src/**/*.nyx"]), dependencies = [(package = "acme.util", path = "../util")], {consoleHostEntry})"""
    directory.Write "app/src/main.nyx" "module main\n\nimport acme.util/util as (twice)\n\ndef helper: int -> int = { n -> n + 1 }\n\n@entry\ndef main = { println(\"{twice(1)} {helper(1)}\") }\n"
    let outcome = nanyxIn (System.IO.Path.Combine(directory.Path, "app")) [ "run" ]
    Assert.Equal("20 2\n", outcome.StandardOutput)

[<Fact>]
let ``Two modules of a library that each select a within from a different module keep their own, beside the program's`` () =
    use directory = new TempDirectory()
    directory.Write "pair/acme.pair.nyxp" """(package = (name = "acme.pair", version = "0.1.0", sources = ["src/**/*.nyx"]))"""
    directory.Write "pair/src/ones.nyx" "export module ones\n\nexport def within: int -> int = { n -> n + 1 }\n"
    directory.Write "pair/src/tens.nyx" "export module tens\n\nexport def within: int -> int = { n -> n + 10 }\n"
    directory.Write "pair/src/left.nyx" "export module left\n\nimport ones as (within)\n\nexport def step: int -> int = { n -> within(n) }\n"
    directory.Write "pair/src/right.nyx" "export module right\n\nimport tens as (within)\n\nexport def leap: int -> int = { n -> within(n) }\n"
    directory.Write
        "app/app.nyxp"
        $"""(package = (name = "app", version = "0.1.0", sources = ["src/**/*.nyx"]), dependencies = [(package = "acme.pair", path = "../pair")], {consoleHostEntry})"""
    directory.Write
        "app/src/main.nyx"
        "module main\n\nimport acme.pair/left as (step)\nimport acme.pair/right as (leap)\n\ndef within: int -> int = { n -> n * 100 }\n\n@entry\ndef main = { println(\"{step(1)} {leap(1)} {within(1)}\") }\n"
    let outcome = nanyxIn (System.IO.Path.Combine(directory.Path, "app")) [ "run" ]
    Assert.Equal("2 11 100\n", outcome.StandardOutput)
