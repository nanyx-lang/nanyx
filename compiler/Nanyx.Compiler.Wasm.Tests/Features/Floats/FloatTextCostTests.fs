/// Writing a float as text sizes its big integers to the float, and keeps them in a static block (#2310, see
/// `FloatText`): the text is what it was, every float's the shortest that reads back, and writing one allocates only
/// the string, and costs a small multiple of writing an int.
module TranspilerWasmTests.Features.Floats.FloatTextCost

open System
open System.Globalization
open Xunit
open TranspilerWasmTestHelpers

/// The text Nanyx writes for the finite, non-negative float `x`, from .NET's shortest text that reads back: positional
/// from 1e-6 up to below 1e21, with a digit either side of the point, and otherwise one digit, a point, the rest (at
/// least one) and an exponent.
let private expected (x: float) =
    let shortest = x.ToString("R", CultureInfo.InvariantCulture)
    let mantissa, exponent =
        match shortest.Split 'E' with
        | [| mantissa; exponent |] -> mantissa, int exponent
        | _ -> shortest, 0
    let whole, fraction =
        match mantissa.Split '.' with
        | [| whole; fraction |] -> whole, fraction
        | _ -> mantissa, ""
    let all = whole + fraction
    let leading = all.Length - all.TrimStart('0').Length
    let digits = all.Trim('0')
    // The value is 0.digits times 10^k.
    let k = whole.Length - leading + exponent
    let n = digits.Length
    if x = 0.0 then "0.0"
    elif k < -5 || k > 21 then
        let rest = if n = 1 then "0" else digits.Substring 1
        $"{digits.[0]}.{rest}e{k - 1}"
    elif k <= 0 then "0." + String('0', -k) + digits
    elif k >= n then digits + String('0', k - n) + ".0"
    else digits.Substring(0, k) + "." + digits.Substring k

/// Floats from the smallest subnormal to the largest finite float: powers of two and of ten, whole numbers, and floats
/// of random bits.
let private samples =
    let random = Random 2310
    [ yield! [ Double.Epsilon; 2.2250738585072014e-308; 2.2250738585072009e-308; Double.MaxValue; 1.0; 0.1; 0.5 ]
      yield! [ -1074 .. 37 .. 1023 ] |> List.map (fun power -> Math.Pow(2.0, float power))
      yield! [ -323 .. 17 .. 308 ] |> List.map (fun power -> Double.Parse($"1e{power}", CultureInfo.InvariantCulture))
      yield! [ 1.0; 7.0; 123456.0; 9007199254740992.0; 1e20; 1e21; 1e-6; 1e-7 ]
      for _ in 1..120 do
          let bits = random.NextInt64(0L, 0x7FF0000000000000L)
          yield BitConverter.Int64BitsToDouble bits ]

[<Fact>]
let ``A float's text is the shortest that reads back, from the smallest subnormal to the largest float`` () =
    let values = samples |> List.map (fun x -> $"    {expected x}") |> String.concat ",\n"
    let source =
        [ "@entry"; "def main = {"; "  def values = ["; values; "  ]"; "  for values | x -> println(\"{x}\")"; "}" ]
        |> String.concat "\n"
    runSource source |> assertRan (samples |> List.map (fun x -> expected x + "\n") |> String.concat "")

/// A program whose `floats(n)` writes `n` floats of 17 digits as text, `wholes(n)` `n` whole numbers as floats, and
/// `ints(n)` `n` ints, each giving the total length of the text.
let private writing =
    normalized
        """def floatsFrom: int, int -> int = { n, acc ->
  if n == 0 then acc else floatsFrom(n - 1, acc + string.length("{int.toFloat(n) / 7.0}"))
}

def intsFrom: int, int -> int = { n, acc ->
  if n == 0 then acc else intsFrom(n - 1, acc + string.length("{n * 7}"))
}

def wholesFrom: int, int -> int = { n, acc ->
  if n == 0 then acc else wholesFrom(n - 1, acc + string.length("{int.toFloat(n)}"))
}

def floats: int -> int = { n -> floatsFrom(n, 0) }

def wholes: int -> int = { n -> wholesFrom(n, 0) }

def ints: int -> int = { n -> intsFrom(n, 0) }
"""

[<Fact>]
let ``Writing a float as text allocates only its string`` () =
    use program = instantiateWasm writing []
    let liveBefore = program.LiveBlocks
    let forFloats, _ = allocationsOf program "floats" 1000
    let forInts, _ = allocationsOf program "ints" 1000
    // An int's hole is written straight into the interpolated string; a float's text is a string of its own, which the
    // interpolation copies, and nothing else.
    Assert.Equal(forInts + 1000, forFloats)
    Assert.Equal(liveBefore, program.LiveBlocks)

[<Fact>]
let ``Writing a float as text costs a small multiple of writing an int`` () =
    use program = instantiateWasm writing []
    let time (name: string) =
        program.Call(name, 1000) |> ignore
        List.init 3 (fun _ ->
            let clock = Diagnostics.Stopwatch.StartNew()
            program.Call(name, 100000) |> ignore
            clock.Elapsed.TotalMilliseconds)
        |> List.min
    let floats = time "floats"
    let wholes = time "wholes"
    let ints = time "ints"
    let measured = $"100,000 floats took {floats:F1} ms, whole numbers {wholes:F1} ms, and ints {ints:F1} ms"
    // With big integers of 40 limbs each, a float of 17 digits took over 200 times as long as an int, and a whole
    // number about a hundred times (#2310).
    Assert.True(floats / ints < 60.0, measured)
    Assert.True(wholes / ints < 10.0, measured)
