/// Dimensions in types (#2623): `Time(a)` is a measured value stored as an `a`, `Time` alone one of a storage of its
/// own, and `Bytes` one of the storage it is constrained to; a signature that names a dimension requires `Num` of its
/// storage without writing it.
module Nanyx.Compiler.Tests.Features.DimensionTypes

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

let private dimensions =
    "dimension Time\n@baseUnit unit ns: Time\ndimension Length(float)\n@baseUnit unit m: Length\ndimension Bytes(int)\n@baseUnit unit B: Bytes\n"

/// The code and the text each diagnostic of compiling `source`, after the dimensions, is reported at.
let private reported (source: string) =
    let source = withFinalNewline (dimensions + source)
    (Compiler.compile source).Diagnostics |> List.map (fun diagnostic -> diagnostic.Code, textAt source (spanOf diagnostic))

/// The types of `names`, as compiling `source` after the dimensions infers them, which reports nothing.
let private typesIn (source: string) (names: string list) =
    let result = Compiler.compile (withFinalNewline (dimensions + source))
    Assert.Empty(result.Diagnostics)
    names |> List.map (fun name -> Unifier.tyToString result.Typed.Value.Types[name])

[<Fact>]
let ``A signature that names a dimension requires Num of its storage`` () =
    let source =
        "def same: Time(a) -> Time(a) = { t -> t }\ndef isLong: Time -> bool = { t -> true }\ndef twice: Time(a), a -> Time(a) = { t, n -> t }"
    Assert.Equal<string list>(
        [ "Time(a) -> Time(a)"; "Time(a) -> bool"; "Time(a), a -> Time(a)" ], typesIn source [ "same"; "isLong"; "twice" ])
    // Called at a storage that is no number, it is missing its `Num`.
    Assert.Equal<string list>(
        [ DiagnosticCodes.MissingContextMembers.Code ],
        reported "def isLong: Time -> bool = { t -> true }\ndef f: Time(bool) -> bool = { t -> isLong(t) }" |> List.map fst)

[<Fact>]
let ``Each dimension written without its storage has a storage of its own`` () =
    let found = typesIn "def overlap: Time, Time -> bool = { a, b -> true }" [ "overlap" ]
    Assert.Contains("Time(a), Time(b) -> bool", found.Head)

[<Fact>]
let ``A constrained dimension is of its one storage, and a concrete storage requires nothing`` () =
    Assert.Equal<string list>(
        [ "Bytes(int) -> Bytes(int)"; "Time(int) -> Time(int)" ],
        typesIn "def size: Bytes -> Bytes = { b -> b }\ndef ints: Time(int) -> Time(int) = { t -> t }" [ "size"; "ints" ])

[<Fact>]
let ``A generic function takes a measured value of any storage`` () =
    Assert.Empty(reported "def same: Time(a) -> Time(a) = { t -> t }\ndef ints: Time(int) -> Time(int) = { t -> same(t) }\ndef floats: Time(float) -> Time(float) = { t -> same(t) }")

[<Fact>]
let ``Measured values of other storages, other dimensions, or no dimension are different types`` () =
    let mismatch = DiagnosticCodes.TypeMismatch.Code
    Assert.Equal(mismatch, (reported "def f: Time(int) -> Time(float) = { t -> t }").Head |> fst)
    Assert.Equal(mismatch, (reported "def f: Time(float) -> Length = { t -> t }").Head |> fst)
    Assert.Equal(mismatch, (reported "def f: Time(int) -> int = { t -> t }").Head |> fst)
    Assert.Equal(mismatch, (reported "def f: int -> Time(int) = { t -> t }").Head |> fst)

[<Fact>]
let ``A storage no parameter fixes is NYX0293 at the dimension`` () =
    let code = DiagnosticCodes.StorageOnlyInResult.Code
    Assert.Equal<(string * string) list>([ code, "Time" ], reported "def average: list(Time) -> Time = { ts -> average(ts) }" |> List.filter (fst >> (=) code))
    Assert.Equal<(string * string) list>([ code, "Time(a)" ], reported "def make: int -> Time(a) = { n -> make(n) }" |> List.filter (fst >> (=) code))
    Assert.Equal<(string * string) list>([ code, "Time" ], reported "def ints: Time(int) -> Time(int) = { t -> t }\ndef value: Time = ints" |> List.filter (fst >> (=) code))
    Assert.Empty(reported "def average: list(Time(a)) -> Time(a) = { ts -> average(ts) }")

[<Fact>]
let ``A storage written for a constrained dimension is NYX0294`` () =
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.ConstrainedDimensionStorage.Code, "Bytes(a)" ],
        reported "def f: Bytes(a) -> Bytes = { b -> b }")

[<Fact>]
let ``A local def's signature is read as a top-level one's`` () =
    Assert.Empty(reported "def outer = { ->\n  def inner: Time -> bool = { t -> true }\n  inner\n}")
    Assert.Contains(
        DiagnosticCodes.StorageOnlyInResult.Code,
        reported "def make: int -> Time(int) = { n -> make(n) }\ndef outer = { ->\n  def inner: int -> Time = { n -> make(n) }\n  inner\n}" |> List.map fst)
