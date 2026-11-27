/// The helpers for `//` and `%` (see "Num(a)" on the docs' Contexts page): `x // y` divides and rounds down, toward
/// negative infinity, and `x % y` is what is left, with the sign of `y`, so that `x == (x // y) * y + x % y`.
/// WebAssembly's signed integer division and remainder truncate, so on a signed integer each adjusts its result when
/// the remainder is not zero and the operands' signs differ; on floats, which have no remainder instruction, they are
/// Python's. An unsigned integer's are the instructions themselves (see `SizedIntegerCode`), and `/` on integers is a
/// float's (see `CoreIrLower`).
module internal Transpiler.Wasm.DivisionCode

open Transpiler.Wasm.WatText

/// `//` on ints.
let intFloorDiv = "__nanyx_int_floor_div"

/// `%` on ints.
let intMod = "__nanyx_int_mod"

/// `//` on floats, and on `F32`s widened to floats.
let floatFloorDiv = "__nanyx_float_floor_div"

/// `%` on floats, and on `F32`s widened to floats.
let floatMod = "__nanyx_float_mod"


/// The code that leaves 1 when the values of the locals `left` and `right`, of the integer type `held`, have different
/// signs, and `remainder` is not zero.
let private adjusts (held: string) (remainder: string) (left: string) (right: string) =
    [ $"local.get {remainder}"
      $"{held}.const 0"
      $"{held}.ne"
      $"local.get {left}"
      $"local.get {right}"
      $"{held}.xor"
      $"{held}.const 0"
      $"{held}.lt_s"
      "i32.and" ]

/// `//` on two signed integers of the WebAssembly type `held` in a function of this `name`: the truncated quotient, less
/// one when it was rounded up. `div_s` stops the program on a zero divisor, and on the smallest value divided by -1,
/// whose quotient does not fit.
let signedFloorDiv (name: string) (held: string) =
    lines
        [ yield $"(func ${name} (param $a {held}) (param $b {held}) (result {held})"
          yield $"  (local $q {held})"
          yield $"  (local $r {held})"
          yield "  local.get $a"
          yield "  local.get $b"
          yield $"  {held}.div_s"
          yield "  local.set $q"
          yield "  local.get $a"
          yield "  local.get $b"
          yield $"  {held}.rem_s"
          yield "  local.set $r"
          yield "  local.get $q"
          yield! adjusts held "$r" "$a" "$b" |> indent
          if held = "i64" then yield "  i64.extend_i32_u"
          yield $"  {held}.sub"
          yield ")" ]

/// `%` on two signed integers of the WebAssembly type `held` in a function of this `name`: the truncated remainder, plus
/// the divisor when the two have different signs. `rem_s` stops the program on a zero divisor.
let signedMod (name: string) (held: string) =
    lines
        [ yield $"(func ${name} (param $a {held}) (param $b {held}) (result {held})"
          yield $"  (local $r {held})"
          yield "  local.get $a"
          yield "  local.get $b"
          yield $"  {held}.rem_s"
          yield "  local.set $r"
          yield! adjusts held "$r" "$r" "$b" |> List.map (fun line -> "  " + line)
          yield $"  if (result {held})"
          yield "    local.get $r"
          yield "    local.get $b"
          yield $"    {held}.add"
          yield "  else"
          yield "    local.get $r"
          yield "  end"
          yield ")" ]

/// The truncated remainder of the floats `$a` and `$b`, C's `fmod`, as code that leaves it: `a - trunc(a / b) * b`,
/// with the sign of `a`. When `b` is infinite the quotient is zero and `0 * b` NaN, so the remainder is `a` itself
/// (NaN when `a` is infinite).
let private truncatedRemainder =
    [ "local.get $b"
      "f64.abs"
      "f64.const inf"
      "f64.eq"
      "if (result f64)"
      "  local.get $a"
      "  local.get $a"
      "  local.get $a"
      "  f64.sub"
      "  f64.add"
      "else"
      "  local.get $a"
      "  local.get $a"
      "  local.get $b"
      "  f64.div"
      "  f64.trunc"
      "  local.get $b"
      "  f64.mul"
      "  f64.sub"
      "end"
      "local.get $a"
      "f64.copysign" ]

/// The code that leaves 1 when the float `$r` is not zero and has a sign other than `$b`'s.
let private signsDiffer =
    [ "local.get $r"
      "f64.const 0"
      "f64.ne"
      "local.get $r"
      "f64.const 0"
      "f64.lt"
      "local.get $b"
      "f64.const 0"
      "f64.lt"
      "i32.ne"
      "i32.and" ]


/// `%` on floats, as Python's: the truncated remainder, plus `b` when the two have different signs, and a zero has the
/// sign of `b`.
let private floatModCode =
    lines
        [ yield $"(func ${floatMod} (param $a f64) (param $b f64) (result f64)"
          yield "  (local $r f64)"
          yield! indent truncatedRemainder
          yield "  local.set $r"
          yield! indent signsDiffer
          yield "  if (result f64)"
          yield "    local.get $r"
          yield "    local.get $b"
          yield "    f64.add"
          yield "  else"
          yield "    local.get $r"
          yield "    local.get $b"
          yield "    f64.copysign"
          yield "  end"
          yield ")" ]

/// `//` on floats, as Python's: `(a - r) / b` for the truncated remainder `r`, less one when `r` and `b` have different
/// signs, and then floored, or rounded up where it is within a half of the next whole number, as the division may leave
/// it just below one. A zero has the sign of `a / b`. Dividing by zero is `floor(a / b)`: infinite, or NaN for `0 // 0`.
let private floatFloorDivCode =
    lines
        [ yield $"(func ${floatFloorDiv} (param $a f64) (param $b f64) (result f64)"
          yield "  (local $r f64)"
          yield "  (local $q f64)"
          yield "  (local $floor f64)"
          yield "  local.get $b"
          yield "  f64.const 0"
          yield "  f64.eq"
          yield "  if"
          yield "    local.get $a"
          yield "    local.get $b"
          yield "    f64.div"
          yield "    f64.floor"
          yield "    return"
          yield "  end"
          yield! indent truncatedRemainder
          yield "  local.set $r"
          yield "  local.get $a"
          yield "  local.get $r"
          yield "  f64.sub"
          yield "  local.get $b"
          yield "  f64.div"
          yield "  local.set $q"
          yield! indent signsDiffer
          yield "  if"
          yield "    local.get $q"
          yield "    f64.const 1"
          yield "    f64.sub"
          yield "    local.set $q"
          yield "  end"
          yield "  local.get $q"
          yield "  f64.const 0"
          yield "  f64.eq"
          yield "  if"
          yield "    f64.const 0"
          yield "    local.get $a"
          yield "    local.get $b"
          yield "    f64.div"
          yield "    f64.copysign"
          yield "    return"
          yield "  end"
          yield "  local.get $q"
          yield "  f64.floor"
          yield "  local.tee $floor"
          yield "  local.get $q"
          yield "  local.get $floor"
          yield "  f64.sub"
          yield "  f64.const 0.5"
          yield "  f64.gt"
          yield "  f64.convert_i32_u"
          yield "  f64.add"
          yield ")" ]

/// The helper `op`, `//` or `%`, calls on ints, if it is one of them.
let intHelper (op: string) =
    match op with
    | "//" -> Some intFloorDiv
    | "%" -> Some intMod
    | _ -> None

/// The helper `op`, `//` or `%`, calls on floats, and on `F32`s widened to floats, if it is one of them.
let floatHelper (op: string) =
    match op with
    | "//" -> Some floatFloorDiv
    | "%" -> Some floatMod
    | _ -> None

/// The helpers, keyed by name. None touches linear memory.
let helpers : (string * string) list =
    [ intFloorDiv, signedFloorDiv intFloorDiv "i64"
      intMod, signedMod intMod "i64"
      floatFloorDiv, floatFloorDivCode
      floatMod, floatModCode ]
