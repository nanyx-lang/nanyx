/// The operators on the sized integers other than `int` (see "Sized numbers" in the docs' work in progress), and the
/// helpers they call. A `U64` is held in an `i64`, and any other in an `i32`, sign-extended or zero-extended from its
/// width (see `CoreIrLayout`). `+`, `-` and `*` stop the program when the result does not fit the type, as they do on an
/// int, and so does `/` on a signed type, whose smallest value divided by -1 does not fit.
module internal Transpiler.Wasm.SizedIntegerCode

open NyxCompiler

/// What an operator on two sized integers on the stack is.
type Operation =
    | Instruction of string
    /// A call to the helper of this name, which `helpers` has.
    | CallHelper of string

/// The WebAssembly type a value of `integer` is held in.
let private valueType (integer: SizedInteger) = if integer = SizedInteger.U64 then "i64" else "i32"

/// The suffix of an instruction that reads its operands as `integer` is signed or not.
let private signedness (integer: SizedInteger) = if SizedInteger.isSigned integer then "s" else "u"

/// The name of the helper that does `operation` on values of `integer`.
let helperName (integer: SizedInteger) (operation: string) = $"__nanyx_{(SizedInteger.name integer).ToLowerInvariant()}_{operation}"

/// The helper that gives an int as a value of `integer`, which stops the program when it does not fit. It takes the int
/// as the `i64` it is held in.
let fromIntHelper (integer: SizedInteger) = helperName integer "from_int"

/// The operator `op` on two values of `integer`: an arithmetic one leaves a value of `integer`, a comparison a bool.
let binary (integer: SizedInteger) (op: string) : Operation option =
    let held = valueType integer
    let signed = signedness integer
    match op with
    | "+" -> Some (CallHelper(helperName integer "add"))
    | "-" -> Some (CallHelper(helperName integer "sub"))
    | "*" -> Some (CallHelper(helperName integer "mul"))
    | "/" when SizedInteger.isSigned integer -> Some (CallHelper(helperName integer "div"))
    | "/" -> Some (Instruction $"{held}.div_{signed}")
    | "%" -> Some (Instruction $"{held}.rem_{signed}")
    | "==" -> Some (Instruction $"{held}.eq")
    | "!=" -> Some (Instruction $"{held}.ne")
    | "<" -> Some (Instruction $"{held}.lt_{signed}")
    | "<=" -> Some (Instruction $"{held}.le_{signed}")
    | ">" -> Some (Instruction $"{held}.gt_{signed}")
    | ">=" -> Some (Instruction $"{held}.ge_{signed}")
    | _ -> None

/// The code that keeps the low bits of `integer` of the value on the stack, held as `integer` is held: sign-extended
/// from them for a signed type, zero-extended for an unsigned one.
let private narrowed (integer: SizedInteger) =
    match integer with
    | SizedInteger.I8 -> [ "i32.extend8_s" ]
    | SizedInteger.I16 -> [ "i32.extend16_s" ]
    | SizedInteger.U8 -> [ "i32.const 255"; "i32.and" ]
    | SizedInteger.U16 -> [ "i32.const 65535"; "i32.and" ]
    | SizedInteger.I32
    | SizedInteger.U32
    | SizedInteger.U64 -> []

/// The operation `operation` (`add`, `sub` or `mul`) on two values of `integer` on the stack that keeps the low bits of
/// the result, as the type holds them: wrapping around past its range rather than stopping the program.
let wrapping (integer: SizedInteger) (operation: string) : string list =
    $"{valueType integer}.{operation}" :: narrowed integer

/// The code that turns an integer of the type `from` on the stack into one of the type `into`, where `None` is `int`:
/// the source's value sign- or zero-extended as the source is signed or not, or its low bits, held as the target holds
/// them. A conversion that may not keep the value checks that it fits first (see the prelude's).
let cast (from: SizedInteger option) (into: SizedInteger option) : string list =
    let held = Option.map valueType >> Option.defaultValue "i64"
    let signed = Option.map SizedInteger.isSigned >> Option.defaultValue true
    let resized =
        match held from, held into with
        | "i64", "i32" -> [ "i32.wrap_i64" ]
        | "i32", "i64" -> [ $"""i64.extend_i32_{if signed from then "s" else "u"}""" ]
        | _ -> []
    resized @ (into |> Option.map narrowed |> Option.defaultValue [])

/// The instruction that converts an integer of the type `from` on the stack, where `None` is `int`, to the nearest
/// floating-point number of the WebAssembly type `floatType`.
let toFloat (from: SizedInteger option) (floatType: string) =
    let held = from |> Option.map valueType |> Option.defaultValue "i64"
    let signed = from |> Option.map SizedInteger.isSigned |> Option.defaultValue true
    $"""{floatType}.convert_{held}_{if signed then "s" else "u"}"""

/// Stops the program as an int's overflow does: dividing the smallest int by -1 traps with "integer overflow".
let private overflow = [ $"i64.const {System.Int64.MinValue}"; "i64.const -1"; "i64.div_s"; "drop" ]

let private lines (code: string list) = String.concat "\n" code

/// The code that leaves 1 when the `i64` in `$r`, read as `integer` is signed or not, is outside `integer`'s range.
let private outOfRange (integer: SizedInteger) =
    if SizedInteger.isSigned integer then
        [ "local.get $r"
          $"i64.const {SizedInteger.minValue integer}"
          "i64.lt_s"
          "local.get $r"
          $"i64.const {SizedInteger.maxValue integer}"
          "i64.gt_s"
          "i32.or" ]
    else
        [ "local.get $r"; $"i64.const {SizedInteger.maxValue integer}"; "i64.gt_u" ]

/// The helper `name` for a sized integer held in an `i32`: both operands widened to `i64`s, where `instruction` cannot
/// overflow, and the result checked against the type's range. An unsigned result below 0 is past the range read
/// unsigned.
let private narrowHelper (integer: SizedInteger) (name: string) (instruction: string) =
    let widen = $"i64.extend_i32_{signedness integer}"
    lines
        [ yield $"(func ${name} (param $a i32) (param $b i32) (result i32)"
          yield "  (local $r i64)"
          yield "  local.get $a"
          yield $"  {widen}"
          yield "  local.get $b"
          yield $"  {widen}"
          yield $"  {instruction}"
          yield "  local.set $r"
          yield! outOfRange integer |> List.map (fun line -> "  " + line)
          yield "  if"
          yield! overflow |> List.map (fun line -> "    " + line)
          yield "  end"
          yield "  local.get $r"
          yield "  i32.wrap_i64"
          yield ")" ]

/// The helper `name` for a `U64`: `instruction` on the two, and `overflowed` leaves 1 when its result `$r` did not
/// fit.
let private u64Helper (name: string) (instruction: string) (overflowed: string list) =
    lines
        [ yield $"(func ${name} (param $a i64) (param $b i64) (result i64)"
          yield "  (local $r i64)"
          yield "  local.get $a"
          yield "  local.get $b"
          yield $"  {instruction}"
          yield "  local.set $r"
          yield! overflowed |> List.map (fun line -> "  " + line)
          yield "  if"
          yield! overflow |> List.map (fun line -> "    " + line)
          yield "  end"
          yield "  local.get $r"
          yield ")" ]

/// The helper that gives the int `$r` as a value of `integer`.
let private fromInt (integer: SizedInteger) =
    let name = fromIntHelper integer
    let check = if integer = SizedInteger.U64 then [ "local.get $r"; "i64.const 0"; "i64.lt_s" ] else outOfRange integer
    lines
        [ yield $"(func ${name} (param $r i64) (result {valueType integer})"
          yield! check |> List.map (fun line -> "  " + line)
          yield "  if"
          yield! overflow |> List.map (fun line -> "    " + line)
          yield "  end"
          yield "  local.get $r"
          if integer <> SizedInteger.U64 then yield "  i32.wrap_i64"
          yield ")" ]

/// The helpers of every sized integer, keyed by name.
let helpers : (string * string) list =
    SizedInteger.all
    |> List.collect (fun integer ->
        let name = helperName integer
        let arithmetic =
            if integer = SizedInteger.U64 then
                // A sum below either operand wrapped; so did a difference above the first. A product overflowed when
                // dividing it by `$a` does not give `$b` back.
                [ name "add", u64Helper (name "add") "i64.add" [ "local.get $r"; "local.get $a"; "i64.lt_u" ]
                  name "sub", u64Helper (name "sub") "i64.sub" [ "local.get $a"; "local.get $b"; "i64.lt_u" ]
                  name "mul",
                  u64Helper
                      (name "mul")
                      "i64.mul"
                      [ "local.get $a"; "i64.eqz"; "if (result i32)"; "  i32.const 0"; "else"; "  local.get $r"; "  local.get $a"; "  i64.div_u"; "  local.get $b"; "  i64.ne"; "end" ] ]
            else
                [ yield name "add", narrowHelper integer (name "add") "i64.add"
                  yield name "sub", narrowHelper integer (name "sub") "i64.sub"
                  yield name "mul", narrowHelper integer (name "mul") "i64.mul"
                  if SizedInteger.isSigned integer then yield name "div", narrowHelper integer (name "div") "i64.div_s" ]
        (fromIntHelper integer, fromInt integer) :: arithmetic)
