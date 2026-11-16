/// Custom patterns (see the custom patterns page), run: a match on a pattern's tags runs the pattern on the value.
module TranspilerWasmTests.Features.Matching.RunCustomPatterns

open System.IO
open Xunit
open TranspilerWasmTestHelpers

/// Runs `source` with its memory checked, and asserts it printed `expected`. Both are normalised to `\n`, and the
/// line break that starts `expected` is left out.
let private printsAs (expected: string) (source: string) =
    runSource (source.ReplaceLineEndings "\n") |> assertRan (expected.ReplaceLineEndings("\n").TrimStart('\n'))

[<Fact>]
let ``Run - a total pattern with many choices picks the arm of the tag it returns`` () =
    """
pattern int -> #Even | #Odd = { n ->
  if n % 2 == 0 then #Even else #Odd
}

def parity = { n: int ->
  match n
    | #Even -> "even"
    | #Odd -> "odd"
}

println(parity(4))
println(parity(7))
"""
    |> printsAs """
even
odd
"""

[<Fact>]
let ``Run - a total pattern with one choice binds its payload`` () =
    """
type Rgb = (r: int, g: int, b: int)

pattern Rgb -> #Total(sum: int, brightest: int) = { rgb ->
  def sum = rgb.r + rgb.g + rgb.b
  def brightest = if rgb.r > rgb.g then rgb.r else rgb.g

  #Total(sum = sum, brightest = brightest)
}

match Rgb(r = 1, g = 5, b = 3)
  | #Total(sum, brightest) -> println("{sum} {brightest}")
"""
    |> printsAs """
9 5
"""

[<Fact>]
let ``Run - a one-choice total pattern takes a value apart in a def`` () =
    """
pattern (int, int) -> #Hsl(h: int, s: int, l: int) = { rgb ->
  def (r, g) = rgb
  def h = r + g
  def s = r - g
  def l = r * g

  #Hsl(h = h, s = s, l = l)
}

def #Hsl(h, s, l) = (3, 2)

println("{h} {s} {l}")
"""
    |> printsAs """
5 1 6
"""

[<Fact>]
let ``Run - a pattern's tags mix with ordinary patterns and and-patterns, tried top to bottom`` () =
    """
pattern int -> #Even | #Odd = { n -> if n % 2 == 0 then #Even else #Odd }

def describe = { n: int ->
  match n
    | 0 -> "zero"
    | #Even & big -> "even {big}"
    | #Odd -> "odd"
}

println(describe(0))
println(describe(10))
println(describe(11))
"""
    |> printsAs """
zero
even 10
odd
"""

[<Fact>]
let ``Run - a pattern without arguments runs at most once per match, however many arms use it`` () =
    """
pattern int -> #Even | #Odd = { n -> if dbg(n) % 2 == 0 then #Even else #Odd }

def describe = { n: int ->
  match n + 1
    | #Even & 0 -> "even zero"
    | #Even -> "even"
    | 8 -> "eight"
    | #Odd -> "odd"
}

println(describe(6))
"""
    |> printsAs """
dbg: 7
odd
"""

[<Fact>]
let ``Run - a match on two values runs a pattern on each value it is used on`` () =
    """
pattern int -> #Even | #Odd = { n -> if n % 2 == 0 then #Even else #Odd }

def both = { a: int, b: int ->
  match a, b
    | #Even, #Even -> "both even"
    | #Odd, #Odd -> "both odd"
    | _, _ -> "mixed"
}

println(both(2, 4))
println(both(1, 3))
println(both(1, 2))
"""
    |> printsAs """
both even
both odd
mixed
"""

[<Fact>]
let ``Run - an imported pattern runs as the module's own does`` () =
    let directory = Directory.CreateTempSubdirectory "nanyx-patterns-"
    try
        let write name (text: string) = File.WriteAllText(Path.Combine(directory.FullName, name), text.ReplaceLineEndings "\n")
        write "parity.nyx" """
module parity

export pattern int -> #Even | #Odd = { n -> if n % 2 == 0 then #Even else #Odd }
"""
        write "main.nyx" """
import parity

def parityOf = { n: int ->
  match n
    | #Even -> "even"
    | #Odd -> "odd"
}

println(parityOf(3))
"""
        runFile (Path.Combine(directory.FullName, "main.nyx")) |> assertRan "odd\n"
    finally
        directory.Delete true
