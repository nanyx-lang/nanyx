/// How the WebAssembly runtime writes a float as text (`__nanyx_float_to_string`), and the arithmetic on big
/// unsigned integers it needs to do that exactly.
///
/// A finite float is written as the shortest decimal that reads back as the same `f64`, found by Burger and
/// Dybvig's free-format algorithm ("Printing Floating-Point Numbers Quickly and Accurately", 1996) on exact
/// integers. Those integers are as large as 2^1080, so they are kept as fixed-size big integers in a scratch block
/// the helper allocates and frees again.
module internal Transpiler.Wasm.FloatText

let bigSet = "__nanyx_big_set"
let bigMulSmall = "__nanyx_big_mul_small"
let bigMulPow10 = "__nanyx_big_mul_pow10"
let bigAdd = "__nanyx_big_add"
let bigSub = "__nanyx_big_sub"
let bigCompare = "__nanyx_big_compare"

/// The bytes of one big integer: 40 little-endian 32-bit limbs, 1,280 bits, which holds every value the
/// algorithm makes (the largest is ten times its scale, which is below 2^1077).
let private bigBytes = 160

/// The scratch block: five big integers (r, s, m+, m- and a temporary), then the digits as ASCII.
let private digitsOffset = 5 * bigBytes

let private scratchBytes = digitsOffset + 24

/// The runtime helpers the float helpers call, which are defined with the rest of the runtime.
type Calls =
    { NewObject: string
      Alloc: string
      Free: string
      CopyBytes: string }

let private indent (pad: string) (items: string list) = items |> List.map (fun line -> pad + line)

// Loops `body` over each limb with `$i` the limb's byte offset, from the lowest limb up.
let private eachLimb (label: string) (body: string list) =
    [ "  i32.const 0"
      "  local.set $i"
      $"  loop ${label}"
      yield! indent "    " body
      "    local.get $i"
      "    i32.const 4"
      "    i32.add"
      "    local.tee $i"
      $"    i32.const {bigBytes}"
      "    i32.lt_u"
      $"    br_if ${label}"
      "  end" ]

// Leaves the address of the limb at `$i` of the big integer `$name`.
let private limb (name: string) = [ $"local.get ${name}"; "local.get $i"; "i32.add" ]

// `p = v << shift`: `p v shift ->`. `v` fits in 64 bits, so the result spans at most three limbs.
let private setHelper =
    [ $"(func ${bigSet} (param $p i32) (param $v i64) (param $shift i32)"
      "  (local $i i32)"
      "  (local $q i32)"
      "  (local $b i64)"
      yield! eachLimb "$zero" [ yield! limb "p"; "i32.const 0"; "i32.store" ]
      "  ;; $q is the address of the limb the shift starts in, $b the bits it moves within it"
      "  local.get $p"
      "  local.get $shift"
      "  i32.const 5"
      "  i32.shr_u"
      "  i32.const 2"
      "  i32.shl"
      "  i32.add"
      "  local.set $q"
      "  local.get $shift"
      "  i32.const 31"
      "  i32.and"
      "  i64.extend_i32_u"
      "  local.set $b"
      "  local.get $q"
      "  local.get $v"
      "  local.get $b"
      "  i64.shl"
      "  i64.store32"
      "  local.get $q"
      "  local.get $v"
      "  i64.const 32"
      "  local.get $b"
      "  i64.sub"
      "  i64.shr_u"
      "  i64.store32 offset=4"
      "  ;; a shift by 64 is a shift by 0 in WebAssembly, so the third limb is only written when $b is not 0"
      "  local.get $b"
      "  i64.eqz"
      "  i32.eqz"
      "  if"
      "    local.get $q"
      "    local.get $v"
      "    i64.const 64"
      "    local.get $b"
      "    i64.sub"
      "    i64.shr_u"
      "    i64.store32 offset=8"
      "  end"
      ")" ]

// `p = p * k`, for `k` below 2^31: `p k ->`.
let private mulSmallHelper =
    [ $"(func ${bigMulSmall} (param $p i32) (param $k i64)"
      "  (local $i i32)"
      "  (local $t i64)"
      yield!
          eachLimb
              "$limb"
              [ yield! limb "p"
                yield! limb "p"
                "i64.load32_u"
                "local.get $k"
                "i64.mul"
                "local.get $t"
                "i64.const 32"
                "i64.shr_u"
                "i64.add"
                "local.tee $t"
                "i64.store32" ]
      ")" ]

// `p = p * 10^n`, nine digits at a time: `p n ->`.
let private mulPow10Helper =
    [ $"(func ${bigMulPow10} (param $p i32) (param $n i32)"
      "  block $nines_done"
      "    loop $nines"
      "      local.get $n"
      "      i32.const 9"
      "      i32.lt_s"
      "      br_if $nines_done"
      "      local.get $p"
      "      i64.const 1000000000"
      $"      call ${bigMulSmall}"
      "      local.get $n"
      "      i32.const 9"
      "      i32.sub"
      "      local.set $n"
      "      br $nines"
      "    end"
      "  end"
      "  block $ones_done"
      "    loop $ones"
      "      local.get $n"
      "      i32.eqz"
      "      br_if $ones_done"
      "      local.get $p"
      "      i64.const 10"
      $"      call ${bigMulSmall}"
      "      local.get $n"
      "      i32.const 1"
      "      i32.sub"
      "      local.set $n"
      "      br $ones"
      "    end"
      "  end"
      ")" ]

// `a = a + b`: `a b ->`. The carry is the top half of `$t`.
let private addHelper =
    [ $"(func ${bigAdd} (param $a i32) (param $b i32)"
      "  (local $i i32)"
      "  (local $t i64)"
      yield!
          eachLimb
              "$limb"
              [ yield! limb "a"
                yield! limb "a"
                "i64.load32_u"
                yield! limb "b"
                "i64.load32_u"
                "i64.add"
                "local.get $t"
                "i64.const 32"
                "i64.shr_u"
                "i64.add"
                "local.tee $t"
                "i64.store32" ]
      ")" ]

// `a = a - b`, for `a >= b`: `a b ->`. The borrow is the sign bit of `$t`.
let private subHelper =
    [ $"(func ${bigSub} (param $a i32) (param $b i32)"
      "  (local $i i32)"
      "  (local $t i64)"
      yield!
          eachLimb
              "$limb"
              [ yield! limb "a"
                yield! limb "a"
                "i64.load32_u"
                yield! limb "b"
                "i64.load32_u"
                "i64.sub"
                "local.get $t"
                "i64.const 63"
                "i64.shr_u"
                "i64.sub"
                "local.tee $t"
                "i64.store32" ]
      ")" ]

// -1, 0 or 1 as `a` is less than, equal to or greater than `b`: `a b -> order`.
let private compareHelper =
    [ $"(func ${bigCompare} (param $a i32) (param $b i32) (result i32)"
      "  (local $i i32)"
      "  (local $x i32)"
      "  (local $y i32)"
      $"  i32.const {bigBytes - 4}"
      "  local.set $i"
      "  loop $limb"
      yield! indent "    " (limb "a")
      "    i32.load"
      "    local.tee $x"
      yield! indent "    " (limb "b")
      "    i32.load"
      "    local.tee $y"
      "    i32.ne"
      "    if"
      "      local.get $x"
      "      local.get $y"
      "      i32.gt_u"
      "      i32.const 1"
      "      i32.shl"
      "      i32.const 1"
      "      i32.sub"
      "      return"
      "    end"
      "    local.get $i"
      "    i32.const 4"
      "    i32.sub"
      "    local.tee $i"
      "    i32.const 0"
      "    i32.ge_s"
      "    br_if $limb"
      "  end"
      "  i32.const 0"
      ")" ]

// Writes the byte `value` leaves at `$cursor`, and moves the cursor past it.
let private put (value: string list) =
    [ "local.get $cursor"; yield! value; "i32.store8"; "local.get $cursor"; "i32.const 1"; "i32.add"; "local.set $cursor" ]

let private putChar (c: char) = put [ $"i32.const {int c}" ]

// Copies the `length` bytes at `source` to `$cursor`, and moves the cursor past them.
let private copyOut (calls: Calls) (source: string list) (length: string list) =
    [ "local.get $cursor"
      yield! source
      yield! length
      $"call ${calls.CopyBytes}"
      "local.get $cursor"
      yield! length
      "i32.add"
      "local.set $cursor" ]

// Writes the `width` bytes of the constant `value` at `$cursor` with one store of `store`, and moves the cursor
// past them. The store may write zeros beyond them, which the terminator or the next byte overwrites.
let private putWord (store: string) (value: string) (width: int) =
    [ "local.get $cursor"
      value
      store
      "local.get $cursor"
      $"i32.const {width}"
      "i32.add"
      "local.set $cursor" ]

// 1 when r + m+ reaches s (or passes it, for an odd mantissa), the test for the high end of the range of
// decimals that read back as the float. It leaves r + m+ in t.
let private reachesHigh (calls: Calls) =
    [ "local.get $t"
      "local.get $r"
      $"i32.const {bigBytes}"
      $"call ${calls.CopyBytes}"
      "local.get $t"
      "local.get $mp"
      $"call ${bigAdd}"
      "local.get $t"
      "local.get $s"
      $"call ${bigCompare}"
      "local.get $even"
      "i32.add"
      "i32.const 0"
      "i32.gt_s" ]

// Stores the digit `$d` as the next of the `$n` digits.
let private storeDigit =
    [ "local.get $digits"
      "local.get $n"
      "i32.add"
      "local.get $d"
      "i32.const 48"
      "i32.add"
      "i32.store8"
      "local.get $n"
      "i32.const 1"
      "i32.add"
      "local.set $n" ]

// Sets r, s, m+ and m- for the positive finite float `$bits`, so that the float is r / s and the floats either side
// of it are (r - m-) / s and (r + m+) / s, all times 2, and sets `$even` and `$k`, an estimate of where the decimal
// point goes that is never too high.
let private setUp =
    [ "local.get $bits"
      "i64.const 52"
      "i64.shr_u"
      "i32.wrap_i64"
      "local.set $biased"
      "local.get $bits"
      "i64.const 0xfffffffffffff"
      "i64.and"
      "local.set $f"
      ";; $u is 1 for a power of two above the smallest normal, whose gap to the float below is half the one above"
      "local.get $f"
      "i64.eqz"
      "local.get $biased"
      "i32.const 1"
      "i32.gt_u"
      "i32.and"
      "local.set $u"
      "local.get $biased"
      "if"
      "  local.get $f"
      "  i64.const 0x10000000000000"
      "  i64.or"
      "  local.set $f"
      "  local.get $biased"
      "  i32.const 1075"
      "  i32.sub"
      "  local.set $e"
      "else"
      "  i32.const -1074"
      "  local.set $e"
      "end"
      ";; the float is $f * 2^$e; a reader rounds a tie to the even mantissa, so its ends are in range when it is even"
      "local.get $f"
      "i32.wrap_i64"
      "i32.const 1"
      "i32.and"
      "i32.eqz"
      "local.set $even"
      ";; $es is max($e, 0), so $es - $e is max(-$e, 0)"
      "local.get $e"
      "i32.const 0"
      "local.get $e"
      "i32.const 0"
      "i32.gt_s"
      "select"
      "local.set $es"
      ";; r = f << (es + 1 + u)"
      "local.get $r"
      "local.get $f"
      "local.get $es"
      "i32.const 1"
      "i32.add"
      "local.get $u"
      "i32.add"
      $"call ${bigSet}"
      ";; s = 1 << (es - e + 1 + u)"
      "local.get $s"
      "i64.const 1"
      "local.get $es"
      "local.get $e"
      "i32.sub"
      "i32.const 1"
      "i32.add"
      "local.get $u"
      "i32.add"
      $"call ${bigSet}"
      ";; m+ = 1 << (es + u)"
      "local.get $mp"
      "i64.const 1"
      "local.get $es"
      "local.get $u"
      "i32.add"
      $"call ${bigSet}"
      ";; m- = 1 << es"
      "local.get $mm"
      "i64.const 1"
      "local.get $es"
      $"call ${bigSet}"
      ";; k = ceil((e + bits in f - 1) * log10(2) - 1e-10)"
      "local.get $e"
      "i32.const 63"
      "i32.add"
      "local.get $f"
      "i64.clz"
      "i32.wrap_i64"
      "i32.sub"
      "f64.convert_i32_s"
      "f64.const 0.30102999566398114"
      "f64.mul"
      "f64.const 1e-10"
      "f64.sub"
      "f64.ceil"
      "i32.trunc_f64_s"
      "local.set $k" ]

// Scales r, m+ and m- or s by 10^k, then raises k while it is too low, so that the float's decimal digits are
// those of r / s with the point k places from the left.
let private scale (calls: Calls) =
    [ "local.get $k"
      "i32.const 0"
      "i32.ge_s"
      "if"
      "  local.get $s"
      "  local.get $k"
      $"  call ${bigMulPow10}"
      "else"
      yield!
          [ "r"; "mp"; "mm" ]
          |> List.collect (fun name ->
              [ $"  local.get ${name}"; "  i32.const 0"; "  local.get $k"; "  i32.sub"; $"  call ${bigMulPow10}" ])
      "end"
      "loop $fixup"
      yield! indent "  " (reachesHigh calls)
      "  if"
      "    local.get $s"
      "    i64.const 10"
      $"    call ${bigMulSmall}"
      "    local.get $k"
      "    i32.const 1"
      "    i32.add"
      "    local.set $k"
      "    br $fixup"
      "  end"
      "end" ]

// Writes digits until the decimal so far is in range, the last one rounded to the nearer end when both are.
let private generateDigits (calls: Calls) =
    [ "loop $next_digit"
      yield!
          [ "r"; "mp"; "mm" ]
          |> List.collect (fun name -> [ $"  local.get ${name}"; "  i64.const 10"; $"  call ${bigMulSmall}" ])
      "  ;; $d is r / s, and r is left as the remainder"
      "  i32.const 0"
      "  local.set $d"
      "  block $divided"
      "    loop $divide"
      "      local.get $r"
      "      local.get $s"
      $"      call ${bigCompare}"
      "      i32.const 0"
      "      i32.lt_s"
      "      br_if $divided"
      "      local.get $r"
      "      local.get $s"
      $"      call ${bigSub}"
      "      local.get $d"
      "      i32.const 1"
      "      i32.add"
      "      local.set $d"
      "      br $divide"
      "    end"
      "  end"
      "  ;; low: the digits so far are within m- of the float"
      "  local.get $r"
      "  local.get $mm"
      $"  call ${bigCompare}"
      "  local.get $even"
      "  i32.lt_s"
      "  local.set $low"
      yield! indent "  " (reachesHigh calls)
      "  local.set $high"
      "  local.get $low"
      "  local.get $high"
      "  i32.or"
      "  i32.eqz"
      "  if"
      yield! indent "    " storeDigit
      "    br $next_digit"
      "  end"
      "end"
      ";; the last digit goes up when only the high end is in range, or both are and r is past half of s (or at half"
      ";; with an odd digit)"
      "local.get $high"
      "if"
      "  local.get $low"
      "  if (result i32)"
      "    local.get $t"
      "    local.get $r"
      $"    i32.const {bigBytes}"
      $"    call ${calls.CopyBytes}"
      "    local.get $t"
      "    local.get $r"
      $"    call ${bigAdd}"
      "    local.get $t"
      "    local.get $s"
      $"    call ${bigCompare}"
      "    local.get $d"
      "    i32.const 1"
      "    i32.and"
      "    i32.add"
      "    i32.const 0"
      "    i32.gt_s"
      "  else"
      "    i32.const 1"
      "  end"
      "  local.get $d"
      "  i32.add"
      "  local.set $d"
      "end"
      yield! storeDigit ]

// Writes the `$n` digits with the point `$k` places from their left: in positional notation when the float is
// at least 1e-6 and below 1e21, with at least one digit either side of the point, and otherwise as one digit, a
// point, the rest (at least one) and an exponent.
let private layOut (calls: Calls) =
    let digitsPlus (offset: string list) = [ "local.get $digits"; yield! offset; "i32.add" ]
    [ "local.get $k"
      "i32.const -5"
      "i32.lt_s"
      "local.get $k"
      "i32.const 21"
      "i32.gt_s"
      "i32.or"
      "if"
      yield! indent "  " (put [ "local.get $digits"; "i32.load8_u" ])
      yield! indent "  " (putChar '.')
      "  local.get $n"
      "  i32.const 1"
      "  i32.eq"
      "  if"
      yield! indent "    " (putChar '0')
      "  else"
      yield! indent "    " (copyOut calls (digitsPlus [ "i32.const 1" ]) [ "local.get $n"; "i32.const 1"; "i32.sub" ])
      "  end"
      yield! indent "  " (putChar 'e')
      "  local.get $k"
      "  i32.const 1"
      "  i32.sub"
      "  local.tee $exp"
      "  i32.const 0"
      "  i32.lt_s"
      "  if"
      yield! indent "    " (putChar '-')
      "    i32.const 0"
      "    local.get $exp"
      "    i32.sub"
      "    local.set $exp"
      "  end"
      "  local.get $exp"
      "  i32.const 100"
      "  i32.ge_s"
      "  if"
      yield! indent "    " (put [ "local.get $exp"; "i32.const 100"; "i32.div_s"; "i32.const 48"; "i32.add" ])
      "  end"
      "  local.get $exp"
      "  i32.const 10"
      "  i32.ge_s"
      "  if"
      yield!
          indent
              "    "
              (put [ "local.get $exp"; "i32.const 10"; "i32.div_s"; "i32.const 10"; "i32.rem_s"; "i32.const 48"; "i32.add" ])
      "  end"
      yield! indent "  " (put [ "local.get $exp"; "i32.const 10"; "i32.rem_s"; "i32.const 48"; "i32.add" ])
      "else"
      "  local.get $k"
      "  i32.const 0"
      "  i32.le_s"
      "  if"
      "    ;; 0., then -k zeros, then the digits"
      yield! indent "    " (putChar '0')
      yield! indent "    " (putChar '.')
      "    block $leading_done"
      "      loop $leading"
      "        local.get $k"
      "        i32.eqz"
      "        br_if $leading_done"
      yield! indent "        " (putChar '0')
      "        local.get $k"
      "        i32.const 1"
      "        i32.add"
      "        local.set $k"
      "        br $leading"
      "      end"
      "    end"
      yield! indent "    " (copyOut calls [ "local.get $digits" ] [ "local.get $n" ])
      "  else"
      "    local.get $k"
      "    local.get $n"
      "    i32.ge_s"
      "    if"
      "      ;; the digits, then k - n zeros, then .0"
      yield! indent "      " (copyOut calls [ "local.get $digits" ] [ "local.get $n" ])
      "      block $trailing_done"
      "        loop $trailing"
      "          local.get $n"
      "          local.get $k"
      "          i32.ge_s"
      "          br_if $trailing_done"
      yield! indent "          " (putChar '0')
      "          local.get $n"
      "          i32.const 1"
      "          i32.add"
      "          local.set $n"
      "          br $trailing"
      "        end"
      "      end"
      yield! indent "      " (putChar '.')
      yield! indent "      " (putChar '0')
      "    else"
      "      ;; k digits, the point, the rest"
      yield! indent "      " (copyOut calls [ "local.get $digits" ] [ "local.get $k" ])
      yield! indent "      " (putChar '.')
      yield!
          indent
              "      "
              (copyOut calls (digitsPlus [ "local.get $k" ]) [ "local.get $n"; "local.get $k"; "i32.sub" ])
      "    end"
      "  end"
      "end" ]

// Formats a boxed float as the shortest decimal that reads back as the same float, always with a point: 6.28, 4.0,
// -1.5, 0.30000000000000004, 1.0e-7, 1.0e21. NaN, the infinities and the zeros are NaN, Infinity, -Infinity, 0.0
// and -0.0.
let private floatToStringHelper (floatToString: string) (calls: Calls) =
    [ $"(func ${floatToString} (param $box i32) (result i32)"
      yield!
          [ "bits i64"; "f i64"; "biased i32"; "e i32"; "es i32"; "u i32"; "even i32"; "k i32"; "scratch i32"
            "r i32"; "s i32"; "mp i32"; "mm i32"; "t i32"; "digits i32"; "n i32"; "d i32"; "low i32"; "high i32"
            "exp i32"; "base i32"; "cursor i32" ]
          |> List.map (fun local -> $"  (local ${local})")
      "  local.get $box"
      "  i64.load"
      "  local.set $bits"
      "  ;; a string of a sign and up to 24 characters with a terminator; $base is its length"
      "  i32.const 32"
      "  i32.const 0"
      $"  call ${calls.NewObject}"
      "  i32.const 4"
      "  i32.sub"
      "  local.set $base"
      "  local.get $base"
      "  i32.const 4"
      "  i32.add"
      "  local.set $cursor"
      "  block $written"
      "    ;; NaN, whatever its sign"
      "    local.get $bits"
      "    i64.const 0x7fffffffffffffff"
      "    i64.and"
      "    i64.const 0x7ff0000000000000"
      "    i64.gt_u"
      "    if"
      yield! indent "      " (putWord "i32.store" "i32.const 0x4e614e" 3)
      "      br $written"
      "    end"
      "    local.get $bits"
      "    i64.const 0"
      "    i64.lt_s"
      "    if"
      yield! indent "      " (putChar '-')
      "      local.get $bits"
      "      i64.const 0x7fffffffffffffff"
      "      i64.and"
      "      local.set $bits"
      "    end"
      "    local.get $bits"
      "    i64.const 0x7ff0000000000000"
      "    i64.eq"
      "    if"
      yield! indent "      " (putWord "i64.store" "i64.const 0x7974696e69666e49" 8)
      "      br $written"
      "    end"
      "    local.get $bits"
      "    i64.eqz"
      "    if"
      yield! indent "      " (putWord "i32.store" "i32.const 0x302e30" 3)
      "      br $written"
      "    end"
      $"    i32.const {scratchBytes}"
      $"    call ${calls.Alloc}"
      "    local.tee $scratch"
      "    local.set $r"
      yield!
          [ "s", 1; "mp", 2; "mm", 3; "t", 4 ]
          |> List.collect (fun (name, index) ->
              [ "    local.get $scratch"; $"    i32.const {index * bigBytes}"; "    i32.add"; $"    local.set ${name}" ])
      "    local.get $scratch"
      $"    i32.const {digitsOffset}"
      "    i32.add"
      "    local.set $digits"
      yield! indent "    " setUp
      yield! indent "    " (scale calls)
      yield! indent "    " (generateDigits calls)
      yield! indent "    " (layOut calls)
      "    local.get $scratch"
      $"    call ${calls.Free}"
      "  end"
      "  local.get $base"
      "  local.get $cursor"
      "  local.get $base"
      "  i32.const 4"
      "  i32.add"
      "  i32.sub"
      "  i32.store"
      "  local.get $cursor"
      "  i32.const 0"
      "  i32.store8"
      "  local.get $base"
      "  i32.const 4"
      "  i32.add"
      ")" ]

/// The float-to-text helper, named `floatToString`, and the big-integer helpers it calls, each as its lines.
let helpers (floatToString: string) (calls: Calls) =
    [ floatToString, floatToStringHelper floatToString calls
      bigSet, setHelper
      bigMulSmall, mulSmallHelper
      bigMulPow10, mulPow10Helper
      bigAdd, addHelper
      bigSub, subHelper
      bigCompare, compareHelper ]
