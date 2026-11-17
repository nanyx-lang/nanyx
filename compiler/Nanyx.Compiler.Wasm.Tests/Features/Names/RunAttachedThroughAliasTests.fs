module TranspilerWasmTests.Features.Names.RunAttachedThroughAlias

open System.IO
open Xunit
open TranspilerWasmTestHelpers

// #1833: a definition attached to a type of another module is reached through the import's alias, `t.Encoder.string`,
// as a value as well as a call.

/// What running `main` printed, beside the module `things`.
let private run (main: string) =
    use directory = new TempDirectory()
    let things =
        """module things

export type Encoder(a) = private (write: a -> string)

export def Encoder.string: Encoder(string) = Encoder(write = { s -> "s:{s}" })

export def Encoder.of: (a -> string) -> Encoder(a) = { f -> Encoder(write = f) }

export def encode: a, Encoder(a) -> string = { x, e ->
  def write = e.write
  write(x)
}
"""
    File.WriteAllText(Path.Combine(directory.Path, "things.nyx"), things.ReplaceLineEndings "\n")
    let mainPath = Path.Combine(directory.Path, "main.nyx")
    File.WriteAllText(mainPath, main.ReplaceLineEndings "\n")
    runFile mainPath

[<Fact>]
let ``Run - an attached value and an attached function are reached through the import's alias`` () =
    run
        """import things as t

println(t.encode("x", t.Encoder.string))
println(t.encode(1, t.Encoder.of({ n -> "n:{n}" })))
"""
    |> assertRan "s:x\nn:1\n"

[<Fact>]
let ``Run - an attached value reached through the import's alias is a value like any other`` () =
    run
        """import things as t

def encoder = t.Encoder.string

println(t.encode("y", encoder))
"""
    |> assertRan "s:y\n"
