/// A package that depends on another in a directory on disk runs with the dependency's modules compiled into the same
/// program (#862).
module TranspilerWasmTests.Features.Packages.PathDependencyRun

open System.IO
open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``A package runs with the public module of its path dependency`` () =
    use directory = new TempDirectory()
    directory.Write "util/util.nyxp" """(package = (name = "acme.util", version = "0.1.0"))"""
    directory.Write "util/strings.nyx" "export module strings\n\nexport def shout = { s: string -> s ++ \"!\" }\n"
    directory.Write
        "app/app.nyxp"
        $"""(
  package = (name = "app", version = "0.1.0")
  dependencies = [(package = "acme.util", path = "../util")]
  {consoleHostEntry}
)
"""
    directory.Write "app/main.nyx" "module main\n\nimport acme.util/strings\n\n@entry\ndef main = {\n  println(shout(\"hello\"))\n}\n"
    assertRan "hello!\n" (nanyxIn (Path.Combine(directory.Path, "app")) [ "run" ])

[<Theory>]
[<InlineData("import acme.two/two", "describe")>]
[<InlineData("import acme.two/two as t", "t.describe")>]
let ``A dependency's module interpolates a tag union an earlier arm narrowed`` (import: string, describe: string) =
    use directory = new TempDirectory()
    directory.Write "lib2/acme.two.nyxp" """(package = (name = "acme.two", version = "0.1.0"))"""
    directory.Write
        "lib2/two.nyx"
        """export module two

export def describe: #some(string) | #error(#a | #other(string)) -> string = {
  | #some(text) -> text
  | #error(#a) -> "a"
  | #error(e) -> "unreadable: {e}"
}
"""
    directory.Write
        "main.nyx"
        $"""::dependency acme.two ./lib2

{import}

println({describe}(#error(#other("x"))))
"""
    assertRan "unreadable: #other(x)\n" (nanyxIn directory.Path [ "run"; Path.Combine(directory.Path, "main.nyx") ])

// The signature `a -> K` is read again at the call to find what `a` is, with `K` written by its own name, which only the
// alias brings in where the import has one (#2380).
[<Theory>]
[<InlineData("import lib/m", "ek", "K", "#num(1)")>]
[<InlineData("import lib/m as m", "m.ek", "K", "#num(1)")>]
[<InlineData("import lib/m as m", "m.ek", "#some(K)", "#some(#num(1))")>]
let ``A dependency's function that requires Typed is called where its result type is one of the dependency's aliases``
    (import: string, call: string, result: string, expected: string)
    =
    use directory = new TempDirectory()
    directory.Write "lib/lib.nyxp" """(package = (name = "lib", version = "0.1.0"))"""
    let wrap = if result = "K" then id else sprintf "#some(%s)"
    directory.Write
        "lib/m.nyx"
        $"""export module m

export type K = #nul | #num(int)

export def ek: [Typed(a)] a -> {result} = {{ x ->
  match typeOf
    | #int -> {wrap "#num(1)"}
    | else -> {wrap "#nul"}
}}
"""
    directory.Write "main.nyx" $"::dependency lib ./lib\n\n{import}\n\nprintln({call}(3))\n"
    assertRan $"{expected}\n" (nanyxIn directory.Path [ "run"; Path.Combine(directory.Path, "main.nyx") ])
