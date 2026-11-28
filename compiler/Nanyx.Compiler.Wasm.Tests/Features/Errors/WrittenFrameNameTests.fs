/// A stack names each function as the program writes it, not by the name the backend gives it (#3223): an import's
/// export, `perimeter`, not `shapes_perimeter`; a private helper, `helper`; a library function, `list.map`; and a
/// module's own def that shadows a library name, `ignore`, not `main_ignore` (see `TypedModule.WrittenNames`,
/// `SpanTable.shownName`).
module TranspilerWasmTests.Features.Errors.WrittenFrameNames

open System.Text.RegularExpressions
open Xunit
open TranspilerWasmTestHelpers

let private shapes = "module shapes\n\ndef helper: int -> int = { n -> if n > 2 then crash(\"too big: {n}\") else n }\n\nexport def perimeter: int -> int = { n -> helper(n) * 4 }\n"

/// `ignore` adds to what `perimeter` gives, so that its call isn't a tail call, which would leave its frame out.
let private main = "module main\n\nimport shapes as (perimeter)\n\ndef ignore = { n: int -> perimeter(n) + 1 }\n\n@entry\ndef main = {\n  println([1, 2, 3] \\list.map(ignore))\n}\n"

/// `stderr` with the standard library's frames' files named without `nanyx/` and at line 1, so that the test doesn't
/// depend on their lines, nor on how the library's files are named.
let private withLibraryLines (stderr: string) =
    Regex.Replace(stderr.ReplaceLineEndings "\n", @"\((?:nanyx/)?([a-z-]+\.nyx):\d+\)", "($1:1)")

/// The frames of the program's own code and of the standard library's `list.map`, which `crash` stops in.
let private expected =
    "crash: too big: 3\n  crash (prelude.nyx:1)\n  helper (src/shapes.nyx:3)\n  perimeter (src/shapes.nyx:5)\n  ignore (src/main.nyx:5)\n  mapReversed (prelude-lists.nyx:1)\n  list.map (prelude-lists.nyx:1)\n  main (src/main.nyx:9)\n"

[<Fact>]
let ``A crash names an import's export, a private helper, a library function and a shadowing def as written`` () =
    let ran, _ = nanyxOnPackage [ "shapes.nyx", shapes; "main.nyx", main ] [ "run"; "{package}" ]
    Assert.Equal(1, ran.ExitCode)
    Assert.Equal(expected, withLibraryLines ran.StandardError)

[<Fact>]
let ``Without inlining, the functions a crash passes through are named as written too`` () =
    let ran, _ = nanyxOnPackage [ "shapes.nyx", shapes; "main.nyx", main ] [ "run"; "--no-inline"; "{package}" ]
    Assert.Equal(1, ran.ExitCode)
    Assert.Equal(expected, withLibraryLines ran.StandardError)

[<Fact>]
let ``A block's definition is named in a stack by its name in the block`` () =
    let lib = "module lib\n\nexport outer\n\nmodule outer =\n  export def check: int -> int = { n -> if n > 1 then crash(\"no\") else n }\n"
    let entry = "module main\n\nimport lib\n\n@entry\ndef main = { println(check(2)) }\n"
    let ran, _ = nanyxOnPackage [ "lib.nyx", lib; "main.nyx", entry ] [ "run"; "{package}" ]
    Assert.Contains("  check (src/lib.nyx:6)\n  main (src/main.nyx:6)\n", ran.StandardError.ReplaceLineEndings "\n")
