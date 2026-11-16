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
let ``Run - a partial pattern returns _ for no match, and a match on it falls through to _`` () =
    """
pattern string -> #ParseInt(int) | #ParseBool(bool) | _ = { s ->
  if
    | int.fromString(s) is #some(i) -> #ParseInt(i)
    | bool.fromString(s) is #some(b) -> #ParseBool(b)
    | else -> _
}

def parse = { (input: string) ->
  match input
    | #ParseInt(i) -> "an integer: {i}"
    | #ParseBool(b) -> "a boolean: {b}"
    | _ -> "neither"
}

println(parse("42"))
println(parse("true"))
println(parse("x"))
"""
    |> printsAs """
an integer: 42
a boolean: true
neither
"""

[<Fact>]
let ``Run - a partial pattern with one tag and no payload may return a bool or its tag`` () =
    """
pattern int -> #Zero | _ = { n -> n == 0 }
pattern int -> #Positive | _ = { n -> if n > 0 then #Positive else _ }

def describe = { (num: int) ->
  match num
    | #Zero -> "zero"
    | #Positive -> "positive"
    | _ -> "negative"
}

println(describe(0))
println(describe(5))
println(describe(-5))
"""
    |> printsAs """
zero
positive
negative
"""

[<Fact>]
let ``Run - is tests a value against a pattern's tag`` () =
    """
pattern int -> #Zero | _ = { n -> n == 0 }
pattern string -> #ParseInt(int) | _ = { s ->
  match int.fromString(s)
    | #some(i) -> #ParseInt(i)
    | #invalidInt -> _
}

def num = 0

if num is #Zero then
  println("It's zero!")

if 3 is #Zero then println("wrong") else println("3 isn't zero")

if | "12" is #ParseInt(n) -> println("parsed {n}")
   | else -> println("not a number")
"""
    |> printsAs """
It's zero!
3 isn't zero
parsed 12
"""

[<Fact>]
let ``Run - patterns with arguments compose with &, as fizzbuzz`` () =
    """
pattern int -> int -> #DivisibleBy | _ = { divisor ->
  { n -> n % divisor == 0 }
}

def fizzbuzz = { (n: int) ->
  match n
    | #DivisibleBy(3) & #DivisibleBy(5) -> "fizzbuzz"
    | #DivisibleBy(3) -> "fizz"
    | #DivisibleBy(5) -> "buzz"
    | _ -> "{n}"
}

for 1..15 | n -> println(fizzbuzz(n))
"""
    |> printsAs """
1
2
fizz
4
buzz
fizz
7
8
fizz
buzz
11
fizz
13
14
fizzbuzz
"""

[<Fact>]
let ``Run - a pattern with arguments and a payload takes two groups of brackets`` () =
    """
pattern string -> string -> #SplitOn(string, string) | _ = { separator ->
  { s ->
    match s \split(on = separator)
      | [a, b] -> #SplitOn(a, b)
      | _ -> _
  }
}

def halves = { (s: string) ->
  match s
    | #SplitOn(",")(first, second) -> "First: {first}, Second: {second}"
    | _ -> "not two halves"
}

println(halves("hello,world"))
println(halves("hello"))
"""
    |> printsAs """
First: hello, Second: world
not two halves
"""

[<Fact>]
let ``Run - a pattern's arguments may be any expressions, several of them a tuple`` () =
    """
pattern (int, int) -> int -> #Between | _ = { low, high ->
  { n -> n >= low and n <= high }
}

def size = { n: int, limit: int ->
  match n
    | #Between(0, limit / 2) -> "small"
    | #Between(limit / 2 + 1, limit) -> "big"
    | _ -> "out of range"
}

println(size(3, 10))
println(size(8, 10))
println(size(20, 10))
"""
    |> printsAs """
small
big
out of range
"""

[<Fact>]
let ``Run - a pattern given the same literal arguments runs once per match`` () =
    """
pattern int -> int -> #DivisibleBy | _ = { divisor ->
  { n -> dbg(n % divisor) == 0 }
}

def fizz = { (n: int) ->
  match n
    | #DivisibleBy(3) & 0 -> "zero"
    | #DivisibleBy(3) -> "fizz"
    | _ -> "{n}"
}

println(fizz(4))
"""
    |> printsAs """
dbg: 1
4
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
