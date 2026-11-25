/// Reading a float, `float.fromString`: a decimal of at most 19 significant digits is read with the Eisel–Lemire
/// algorithm, on the table of powers of five a module that reads floats has (#2560, see `PowersOfFive`), and gives the
/// float .NET's `Double.Parse` gives; one of more digits is read exactly when it is too near halfway to tell. Reading
/// a decimal of 17 digits, or at either end of the range, allocates only the `#some`, as one of 16 digits does, and
/// costs little more.
module TranspilerWasmTests.Features.Floats.FloatReading

open System
open System.Globalization
open System.Numerics
open Xunit
open TranspilerWasmTestHelpers
open Transpiler.Wasm

/// The 128 bits of the table's entry for `q`, from its two words.
let private tableEntry (q: int) =
    let words = PowersOfFive.words.Value
    let index = 2 * (q - PowersOfFive.smallest)
    (BigInteger words.[index] <<< 64) + BigInteger words.[index + 1]

/// The top 128 bits of `5^q`, worked out here as fast_float's script works out its table, by doubling and halving
/// rather than from bit lengths: `5^q` brought between 2^127 and 2^128 for a `q` of 0 or more; for a negative one,
/// `2^b / 5^-q`, plus 1, for the `b` that brings it there, or for a `q` below -27 a larger `b`, then halved until it
/// is below 2^128.
let private independentEntry (q: int) =
    let top = BigInteger.One <<< 128
    let half = BigInteger.One <<< 127
    if q >= 0 then
        let mutable power = BigInteger.One
        for _ in 1..q do
            power <- power * BigInteger 5
        while power < half do
            power <- power * BigInteger 2
        while power >= top do
            power <- power / BigInteger 2
        power
    else
        let mutable power = BigInteger.One
        for _ in 1 .. -q do
            power <- power * BigInteger 5
        let mutable z = 0
        while BigInteger.Pow(BigInteger 2, z) < power do
            z <- z + 1
        let b = if q >= -27 then z + 127 else 2 * z + 128
        let mutable value = BigInteger.Pow(BigInteger 2, b) / power + BigInteger.One
        while value >= top do
            value <- value / BigInteger 2
        value

[<Fact>]
let ``The table holds the top 128 bits of each power of five, as Eisel–Lemire reads them`` () =
    for q in [ -342; -341; -300; -100; -28; -27; -26; -1; 0; 1; 27; 28; 55; 56; 100; 307; 308 ] do
        Assert.Equal(independentEntry q, tableEntry q)
    // fast_float's own table, at its two ends and either side of 1.
    let hex (text: string) = BigInteger.Parse("0" + text, NumberStyles.HexNumber)
    Assert.Equal(hex "eef453d6923bd65a113faa2906a13b3f", tableEntry -342)
    Assert.Equal(hex "9558b4661b6565f84ac7ca59a424c507", tableEntry -341)
    Assert.Equal(hex "cccccccccccccccccccccccccccccccd", tableEntry -1)
    Assert.Equal(hex "80000000000000000000000000000000", tableEntry 0)
    Assert.Equal(hex "a0000000000000000000000000000000", tableEntry 1)
    Assert.Equal(2 * (PowersOfFive.largest - PowersOfFive.smallest + 1), PowersOfFive.words.Value.Length)

/// The exact decimal of `value`, which is `odd * 2^power`, as digits and an exponent: `"125e-3"`.
let private exactDecimal (odd: BigInteger) (power: int) =
    if power >= 0 then (odd * BigInteger.Pow(BigInteger 2, power)).ToString()
    else $"{odd * BigInteger.Pow(BigInteger 5, -power)}e{power}"

/// The decimal halfway between the finite float `x`, above zero, and the float after it, and the decimals just below
/// and just above it, all of which take more than 19 digits.
let private nearHalfway (x: float) =
    let bits = BitConverter.DoubleToInt64Bits x
    let exponent = int (bits >>> 52)
    let fraction = bits &&& 0xFFFFFFFFFFFFFL
    let mantissa, power = if exponent = 0 then fraction, -1074 else fraction ||| (1L <<< 52), exponent - 1075
    let halfway = exactDecimal (BigInteger mantissa * BigInteger 2 + BigInteger.One) (power - 1)
    let digits, exponent10 =
        match halfway.IndexOf 'e' with
        | -1 -> halfway, 0
        | at -> halfway.Substring(0, at), int (halfway.Substring(at + 1))
    [ halfway
      $"{digits}0000000001e{exponent10 - 10}"
      $"{BigInteger.Parse digits * BigInteger.Pow(BigInteger 10, 10) - BigInteger.One}e{exponent10 - 10}" ]

/// Decimals of every kind there is to read: the shortest text of random floats, and the same floats with 17, 19 and 23
/// digits; random whole numbers of 19 and 20 digits at every power of ten; the halfway points of random floats and of
/// those at the ends of the range, and the decimals either side of them; and decimals of 800 digits.
let private decimals =
    let random = Random 2560
    let randomFloat () = BitConverter.Int64BitsToDouble(random.NextInt64(1L, 0x7FF0000000000000L))
    let randomDigits (count: int) =
        String(Array.init count (fun index -> char (int '0' + (if index = 0 then random.Next(1, 10) else random.Next 10))))
    [ for _ in 1..60 do
          let x = randomFloat ()
          yield x.ToString("R", CultureInfo.InvariantCulture)
          for format in [ "E16"; "E18"; "E22" ] do
              yield x.ToString(format, CultureInfo.InvariantCulture)
      for _ in 1..60 do
          yield $"{randomDigits (random.Next(19, 21))}e{random.Next(-362, 300)}"
      for x in [ Double.Epsilon; 2.2250738585072009e-308; 2.2250738585072014e-308; 1.0; 9007199254740992.0; Double.MaxValue ] do
          yield! nearHalfway x
      for _ in 1..30 do
          yield! nearHalfway (randomFloat ())
      for _ in 1..10 do
          yield $"0.{randomDigits 800}e{random.Next(-300, 300)}" ]

/// The float .NET reads `text` as, as a Nanyx expression.
let private expectedFloat (text: string) =
    let x = Double.Parse(text, CultureInfo.InvariantCulture)
    if Double.IsPositiveInfinity x then "1.0 / 0.0" else floatLiteral x

[<Fact>]
let ``Float.fromString reads a decimal as .NET does, whatever its digits`` () =
    let checks =
        decimals
        |> List.map (fun text -> $"""  reads("{text}", {expectedFloat text})""")
        |> String.concat ",\n"
    let source =
        normalized """def reads: [Console] string, float -> bool = { text, x ->
  def read = float.fromString(text)
  def ok = read == #some(x)

  if not ok then println("{text} is read as {read}, not {x}")
  ok
}

@entry
def main = {
  def results = [
"""
        + checks
        + normalized """
  ]

  println("{results \list.filter({ ok -> not ok }) \list.length} of {list.length(results)} misread")
}
"""
    runSource source |> assertRan $"0 of {decimals.Length} misread\n"

/// A program whose functions each read one decimal `n` times, and give how many times they read it.
let private reading =
    normalized
        """def count: string, int, int -> int = { s, n, acc ->
  if n == 0
    then acc
    else match float.fromString(s)
      | #some(_) -> count(s, n - 1, acc + 1)
      | #invalidFloat -> acc
}

def sixteen: int -> int = { n -> count("3.141592653589793", n, 0) }

def seventeen: int -> int = { n -> count("0.30000000000000004", n, 0) }

def splitSeventeen: int -> int = { n -> count("1234567.8901234567", n, 0) }

def small: int -> int = { n -> count("1.5e-30", n, 0) }

def largest: int -> int = { n -> count("1.7976931348623157e308", n, 0) }

def smallestNormal: int -> int = { n -> count("2.2250738585072014e-308", n, 0) }
"""

/// The decimals that took the slow path, which read with big numbers before Eisel–Lemire (#2560).
let private slowBefore = [ "seventeen"; "splitSeventeen"; "small"; "largest"; "smallestNormal" ]

[<Fact>]
let ``Reading a decimal of 17 digits, or at either end of the range, allocates no more than one of 16 digits`` () =
    use program = instantiateWasm reading []
    let liveBefore = program.LiveBlocks
    let forSixteen, _ = allocationsOf program "sixteen" 1000
    for name in slowBefore do
        // 38 allocations a read for 0.30000000000000004, and 833 for the smallest normal float, before.
        Assert.Equal((name, forSixteen), (name, fst (allocationsOf program name 1000)))
    Assert.Equal(liveBefore, program.LiveBlocks)

[<Fact>]
let ``Reading a decimal of 17 digits, or at either end of the range, takes less than twice one of 16 digits`` () =
    use program = instantiateWasm reading []
    let time (name: string) =
        program.Call(name, 1000) |> ignore
        GC.Collect()
        let clock = Diagnostics.Stopwatch.StartNew()
        program.Call(name, 50000) |> ignore
        clock.Elapsed.TotalMilliseconds
    // Each one's quickest run counts, and they are run again in turn until each holds or twenty rounds have run, so that
    // load on the machine, which slows one run and not another, can't make the test fail (#2388). Before,
    // 0.30000000000000004 took 5 times as long as 3.141592653589793, and the smallest normal float over 40 times.
    let quickest = Collections.Generic.Dictionary<string, float>()
    let note (name: string) (milliseconds: float) =
        quickest.[name] <- (match quickest.TryGetValue name with | true, best -> min best milliseconds | _ -> milliseconds)
    let holds (name: string) = quickest.[name] < 2.0 * quickest.["sixteen"]
    let mutable round = 0
    while round < 20 && (round < 3 || not (slowBefore |> List.forall holds)) do
        for name in slowBefore do
            note name (time name)
            note "sixteen" (time "sixteen")
        round <- round + 1
    let sixteen = quickest.["sixteen"]
    for name in slowBefore do
        Assert.True(holds name, $"{name} took {quickest.[name]:F1} ms, against {sixteen:F1} ms for a decimal of 16 digits")
