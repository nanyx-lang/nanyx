namespace NyxCompiler

/// An integer type of a fixed width other than `int`, which is `I64` (see "Sized numbers" in the docs' work in
/// progress). Each is a primitive type of its name, `TyPrimitive "I32"`.
[<RequireQualifiedAccess>]
type SizedInteger =
    | I8
    | I16
    | I32
    | U8
    | U16
    | U32
    | U64

module SizedInteger =
    let all = [ SizedInteger.I8; SizedInteger.I16; SizedInteger.I32; SizedInteger.U8; SizedInteger.U16; SizedInteger.U32; SizedInteger.U64 ]

    /// The type's name, as it is written and printed.
    let name (integer: SizedInteger) =
        match integer with
        | SizedInteger.I8 -> "I8"
        | SizedInteger.I16 -> "I16"
        | SizedInteger.I32 -> "I32"
        | SizedInteger.U8 -> "U8"
        | SizedInteger.U16 -> "U16"
        | SizedInteger.U32 -> "U32"
        | SizedInteger.U64 -> "U64"

    /// How many bits a value of the type has.
    let bits (integer: SizedInteger) =
        match integer with
        | SizedInteger.I8
        | SizedInteger.U8 -> 8
        | SizedInteger.I16
        | SizedInteger.U16 -> 16
        | SizedInteger.I32
        | SizedInteger.U32 -> 32
        | SizedInteger.U64 -> 64

    /// Whether the type has negative values.
    let isSigned (integer: SizedInteger) =
        match integer with
        | SizedInteger.I8
        | SizedInteger.I16
        | SizedInteger.I32 -> true
        | SizedInteger.U8
        | SizedInteger.U16
        | SizedInteger.U32
        | SizedInteger.U64 -> false

    /// The smallest value of the type.
    let minValue (integer: SizedInteger) : bigint = if isSigned integer then -(pown 2I (bits integer - 1)) else 0I

    /// The largest value of the type.
    let maxValue (integer: SizedInteger) : bigint =
        if isSigned integer then pown 2I (bits integer - 1) - 1I else pown 2I (bits integer) - 1I

    let private byName = all |> List.map (fun integer -> name integer, integer) |> Map.ofList

    /// The type `typeName` names, if it is one.
    let tryOfName (typeName: string) = byName |> Map.tryFind typeName

    /// The type as the checker has it.
    let ty (integer: SizedInteger) = TyPrimitive(name integer)

/// `F32`, the floating-point type of 32 bits: a primitive type of its name. `float` is `F64`.
module F32 =
    let name = "F32"

    let ty = TyPrimitive name

/// Whether `typeName` names a sized number type other than `int` and `float`: a sized integer or `F32`.
module SizedNumber =
    let isName (typeName: string) = typeName = F32.name || (SizedInteger.tryOfName typeName).IsSome

/// Matches the types of the sized numbers.
[<AutoOpen>]
module SizedIntegerPatterns =
    /// The type of a sized integer.
    let (|SizedIntegerType|_|) (ty: Ty) =
        match ty with
        | TyPrimitive name -> SizedInteger.tryOfName name
        | _ -> None

    /// The type of a sized number other than `int` and `float`: a sized integer or `F32`.
    let (|SizedNumberType|_|) (ty: Ty) =
        match ty with
        | TyPrimitive name when SizedNumber.isName name -> Some ty
        | _ -> None
