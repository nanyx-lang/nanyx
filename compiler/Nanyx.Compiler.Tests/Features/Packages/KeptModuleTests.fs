module Nanyx.Compiler.Tests.Features.Packages.KeptModules

open System.IO
open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

let private manifest = $"""(package = (name = "demo", version = "0.1.0"), {consoleHostEntry})"""

let private mainSource =
    withFinalNewline """module main

import util

@entry
def main = {
  println("{one}")
}"""

let private utilSource (value: string) =
    withFinalNewline $"""module util

import deep

export def one = zero + {value}"""

let private deepSource = withFinalNewline """module deep

export def zero = 0"""

/// A package whose main.nyx imports util.nyx, which imports deep.nyx.
let private package () =
    new TempPackage(
        [ "demo.nyxp", manifest
          "src/main.nyx", mainSource
          "src/util.nyx", utilSource "1"
          "src/deep.nyx", deepSource ])

/// What the compile of main.nyx, as the language server compiles it with `readFile`, reports of the package's own
/// modules other than main's (see `Recording`), with the result.
let private compileMain (package: TempPackage) (readFile: string -> string) =
    let main = package.Path "src/main.nyx"
    let recording = Recording()
    let result = Package.compileSourceReading (SeededPackages.user ()) recording readFile Linters.none main (File.ReadAllText main)
    Assert.True(result.TypeChecked, sprintf "%A" result.Diagnostics)
    let others = recording.Events |> List.filter (fun event -> event.Contains package.Root && not (event.Contains "main.nyx"))
    others |> List.map (fun event -> event.Replace(Path.GetFullPath(package.Root), "").Replace('\\', '/')), result

[<Fact>]
let ``A package module a compile imports is not checked again in the next compile`` () =
    use package = package ()
    compileMain package File.ReadAllText |> ignore
    let events, _ = compileMain package File.ReadAllText
    // deep comes in with util, as what a standard-library module imports does.
    Assert.Equal<string list>([ "reused /src/util.nyx" ], events)

[<Fact>]
let ``A package module whose file changed is checked again`` () =
    use package = package ()
    compileMain package File.ReadAllText |> ignore
    File.WriteAllText(package.Path "src/util.nyx", utilSource "41")
    let events, _ = compileMain package File.ReadAllText
    Assert.Equal<string list>([ "start /src/util.nyx"; "reused /src/deep.nyx"; "end /src/util.nyx" ], events)

[<Fact>]
let ``A package module is checked again when a module it imports changed`` () =
    use package = package ()
    compileMain package File.ReadAllText |> ignore
    File.WriteAllText(package.Path "src/deep.nyx", deepSource.Replace("= 0", "= 10"))
    let events, _ = compileMain package File.ReadAllText
    Assert.Equal<string list>([ "start /src/util.nyx"; "start /src/deep.nyx"; "end /src/deep.nyx"; "end /src/util.nyx" ], events)

[<Fact>]
let ``A package module is checked again when the editor's text of its file differs from the last check's`` () =
    use package = package ()
    compileMain package File.ReadAllText |> ignore
    let util = Path.GetFullPath(package.Path "src/util.nyx")
    let edited (file: string) = if file = util then utilSource "2" else File.ReadAllText file
    let events, _ = compileMain package edited
    Assert.Equal<string list>([ "start /src/util.nyx"; "reused /src/deep.nyx"; "end /src/util.nyx" ], events)
