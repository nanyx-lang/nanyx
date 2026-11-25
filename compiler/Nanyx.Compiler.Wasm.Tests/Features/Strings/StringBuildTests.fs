/// `string.build` makes a string of the pieces a block emits, through `Emit(string)`, copying each into one buffer that
/// doubles when it is full (#2597): pieces come out in order, it takes time in proportion to the string's length, and it
/// allocates a number of blocks that grows with the log of that length, not one per piece.
module TranspilerWasmTests.Features.Strings.StringBuild

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``string.build joins the pieces a block emits, in order`` () =
    let source =
        normalized
            """def numbered = string.build {
  for 1..4 | i ->
    emit("{i}")
    emit("")
    emit(": é, ")
}

println(numbered)
println(string.build { () } == "")
println(string.build { emit("one piece") })
println(string.build { emit("ünïcödé "); emit("日本語") })
"""
    runSource source |> assertRan "1: é, 2: é, 3: é, 4: é, \ntrue\none piece\nünïcödé 日本語\n"

/// `built(n)` builds a string of `n` pieces of 6 bytes and gives its length; `appended(n)` makes the same string with `++`.
let private building =
    normalized
        """def built: int -> int = { n ->
  string.length(string.build { for 1..n | _ -> emit("piece ") })
}

def appendedFrom: int, string -> string = { n, s ->
  if n == 0 then s else appendedFrom(n - 1, s ++ "piece ")
}

def appended: int -> int = { n -> string.length(appendedFrom(n, "")) }
"""

[<Fact; Trait("Category", "Timing")>]
let ``string.build makes the string of 100,000 pieces in time that grows with their number`` () =
    use program = instantiateWasm building []
    Assert.Equal(600_000, program.Call("built", 100_000))
    callsInLinearTime program "built" 100_000

[<Fact>]
let ``string.build allocates a number of blocks that grows with the log of the string's length`` () =
    use program = instantiateWasm building []
    let liveBefore = program.LiveBlocks
    let forThousand, length = allocationsOf program "built" 1_000
    Assert.Equal(6_000, length)
    let forSixteenThousand, _ = allocationsOf program "built" 16_000
    // Sixteen times the pieces is four more doublings of the buffer.
    Assert.Equal(forThousand + 4, forSixteenThousand)
    // `++` copies the string so far for each piece.
    let appending, _ = allocationsOf program "appended" 1_000
    Assert.True(forThousand * 10 < appending, $"string.build made {forThousand} blocks, and ++ {appending}")
    Assert.Equal(liveBefore, program.LiveBlocks)
