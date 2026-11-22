module TranspilerWasmTests.Features.Names.RunImportedTypeInstance

open System.IO
open Xunit
open TranspilerWasmTestHelpers

// #1796: a value of a type from another module is written, and compared, through the `ToString` and `Eq` the compiler
// derives for it, whatever name the importing module knows the type by: under an import's alias, or by none, when a
// selective import leaves the type out.

/// What running `main` printed, beside the module `things`, written as `lib`.
let private runWith (lib: string) (main: string) =
    use directory = new TempDirectory()
    File.WriteAllText(Path.Combine(directory.Path, "things.nyx"), lib.ReplaceLineEndings "\n")
    let mainPath = Path.Combine(directory.Path, "main.nyx")
    File.WriteAllText(mainPath, main.ReplaceLineEndings "\n")
    runFile mainPath

let private things =
    """module things

export type Pair = (a: int, b: int)

export type Shape = #circle(float) | #group(list(Shape))

export type Id = protected int

export def pair: Pair = Pair(a = 1, b = 2)

export def shape: Shape = #group([#circle(1.5), #group([])])

export def id: int -> Id = { n -> Id(n) }
"""

[<Fact>]
let ``Run - a record and a recursive union from a module imported under an alias are written`` () =
    runWith
        things
        """import things as t

println(t.pair)
println("{t.shape}")
"""
    |> assertRan "(a = 1, b = 2)\n#group([#circle(1.5), #group([])])\n"

[<Fact>]
let ``Run - values of types from a module imported under an alias are compared with ==`` () =
    runWith
        things
        """import things as t

println(t.pair == t.pair)
println(t.shape == #group([#circle(1.5), #group([])]))
println(t.shape == #circle(1.5))
println(t.id(7) == t.id(8))
"""
    |> assertRan "true\ntrue\nfalse\nfalse\n"

[<Fact>]
let ``Run - a value of a type a selective import leaves out is written and compared`` () =
    runWith
        things
        """import things as (pair, shape)

println(pair)
println(shape == shape)
"""
    |> assertRan "(a = 1, b = 2)\ntrue\n"

[<Fact>]
let ``Run - a nominal type imported under an alias is compared as itself, not as a type of the same name in the importing module`` () =
    runWith
        things
        """import things as t

type Id = protected string

def mine: Id = Id("x")

println(t.id(7) == t.id(7))
println(mine == Id("y"))
"""
    |> assertRan "true\nfalse\n"
