/// A package that depends on another in a directory on disk runs with the dependency's modules compiled into the same
/// program (#862).
module TranspilerWasmTests.Features.Packages.PathDependencyRun

open System.IO
open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``A package runs with the public module of its path dependency`` () =
    use directory = new TempDirectory()
    let write (path: string) (text: string) =
        let full = Path.Combine(directory.Path, path)
        Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
        File.WriteAllText(full, text.ReplaceLineEndings "\n")
    write "util/util.nyxp" """(package = (name = "acme.util", version = "0.1.0"))"""
    write "util/strings.nyx" "export module strings\n\nexport def shout = { s: string -> s ++ \"!\" }\n"
    write
        "app/app.nyxp"
        $"""(
  package = (name = "app", version = "0.1.0")
  dependencies = [(package = "acme.util", path = "../util")]
  {consoleHostEntry}
)
"""
    write "app/main.nyx" "module main\n\nimport acme.util/strings\n\n@entry\ndef main = {\n  println(shout(\"hello\"))\n}\n"
    assertRan "hello!\n" (nanyxIn (Path.Combine(directory.Path, "app")) [ "run" ])

[<Theory>]
[<InlineData("import acme.two/two", "describe")>]
[<InlineData("import acme.two/two as t", "t.describe")>]
let ``A dependency's module interpolates a tag union an earlier arm narrowed`` (import: string, describe: string) =
    use directory = new TempDirectory()
    let write (path: string) (text: string) =
        let full = Path.Combine(directory.Path, path)
        Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
        File.WriteAllText(full, text.ReplaceLineEndings "\n")
    write "lib2/acme.two.nyxp" """(package = (name = "acme.two", version = "0.1.0"))"""
    write
        "lib2/two.nyx"
        """export module two

export def describe: #some(string) | #error(#a | #other(string)) -> string = {
  | #some(text) -> text
  | #error(#a) -> "a"
  | #error(e) -> "unreadable: {e}"
}
"""
    write
        "main.nyx"
        $"""::dependency acme.two ./lib2

{import}

println({describe}(#error(#other("x"))))
"""
    assertRan "unreadable: #other(x)\n" (nanyxIn directory.Path [ "run"; Path.Combine(directory.Path, "main.nyx") ])
