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
    write "util/util.nyxp" """(package = (name = "acme.util", version = "0.1.0", publicModules = ["strings"]))"""
    write "util/strings.nyx" "module strings\n\nexport def shout = { s: string -> s ++ \"!\" }\n"
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
