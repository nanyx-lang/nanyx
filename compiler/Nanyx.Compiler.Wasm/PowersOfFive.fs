/// The table of powers of five that reading a float uses (`float.fromString`, in the prelude's
/// `prelude-numbers.nyx`), and the runtime helper that reads it (`__nanyx_power_of_five`).
///
/// The prelude reads a decimal of at most 19 significant digits, `w * 10^q`, with the Eisel–Lemire algorithm (Lemire,
/// "Number Parsing at a Gigabyte per Second", 2021; Mushtak and Lemire, "Fast Number Parsing Without Fallback", 2023),
/// as Go, Rust and C++'s `from_chars` do: `w`, shifted so that its top bit is set, times the top 128 bits of `5^q`,
/// gives the float's 53 bits and how to round them. The table holds those 128 bits for each `q` from -342 to 308, outside
/// which a decimal of at most 19 digits is 0 or infinity: for a `q` of 0 or more, `5^q` shifted so that
/// its top bit is bit 127, truncated; for a negative one, `2^b / 5^-q` for the `b` that puts its top bit there, rounded
/// up, as fast_float's table is. Each entry is two 64-bit words, the high one first.
///
/// A module places the table in its data, below its heap, only when it reads a float (see `CodeGen`), and the global
/// `$__nanyx_powers_of_five` holds its address.
module Transpiler.Wasm.PowersOfFive

open System
open System.Numerics

/// The smallest and largest powers of ten the table has an entry for.
let smallest = -342
let largest = 308

/// The helper that gives the 64-bit word at an index of the table: the high word of `q`'s entry is at `2 * (q + 342)`
/// and the low one after it.
let helper = NyxCompiler.Intrinsics.powerOfFive

/// The global that holds the table's address.
let addressGlobal = "$__nanyx_powers_of_five"

/// The top 128 bits of `5^q`, as Eisel–Lemire reads them (see the module's comment).
let private entry (q: int) =
    if q >= 0 then
        let power = BigInteger.Pow(BigInteger 5, q)
        let bits = int (power.GetBitLength())
        if bits <= 128 then power <<< (128 - bits) else power >>> (bits - 128)
    else
        let power = BigInteger.Pow(BigInteger 5, -q)
        // The smallest z with 2^z at least 5^-q, which is its bit length, as no power of five but 1 is a power of two.
        let z = int (power.GetBitLength())
        if q >= -27 then
            BigInteger.Pow(BigInteger 2, z + 127) / power + BigInteger.One
        else
            let mutable rounded = BigInteger.Pow(BigInteger 2, 2 * z + 128) / power + BigInteger.One
            let limit = BigInteger.One <<< 128
            while rounded >= limit do
                rounded <- rounded >>> 1
            rounded

/// The table's words, two for each `q` from `smallest` to `largest`, the high one first.
let words: Lazy<uint64 array> =
    lazy
        (let mask = (BigInteger.One <<< 64) - BigInteger.One
         [| for q in smallest..largest do
                let value = entry q
                yield uint64 (value >>> 64)
                yield uint64 (value &&& mask) |])

/// How many bytes the table takes: 651 entries of 16 bytes.
let bytes = (largest - smallest + 1) * 16

/// The table as the bytes of a data segment, each word little-endian.
let data () =
    words.Value |> Array.collect (fun word -> BitConverter.GetBytes word)

/// The helper, which takes an index into the table and gives the word there.
let helperCode =
    [ $"(func ${helper} (param $index i64) (result i64)"
      "  local.get $index"
      "  i32.wrap_i64"
      "  i32.const 3"
      "  i32.shl"
      $"  global.get {addressGlobal}"
      "  i32.add"
      "  i64.load"
      ")" ]
