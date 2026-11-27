/// Dimensions in types (#2623): `Time(a)` is a measured value stored as an `a`, `Time` alone one of the storage the
/// function's other bare parameters share, or in its result, one its body settles (#2918), and `Bytes` one of the
/// storage it is constrained to; a signature that names a dimension requires `Num` of its storage without writing it.
module Nanyx.Compiler.Tests.Features.DimensionTypes

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport
open Nanyx.Compiler.Tests.Features.UnitsFixture

[<Fact>]
let ``A signature that names a dimension requires Num of its storage`` () =
    let source =
        "def same: Time(a) -> Time(a) = { t -> t }\ndef isLong: Time -> bool = { t -> true }\ndef twice: Time(a), a -> Time(a) = { t, n -> t }"
    Assert.Equal<string list>(
        [ "Time(a) -> Time(a)"; "Time(a) -> bool"; "Time(a), a -> Time(a)" ], typesAfter dimensions source [ "same"; "isLong"; "twice" ])
    // Called at a storage that is no number, it is missing its `Num`.
    Assert.Equal<string list>(
        [ DiagnosticCodes.MissingContextMembers.Code ],
        reportedAt (dimensions + "def isLong: Time -> bool = { t -> true }\ndef f: Time(bool) -> bool = { t -> isLong(t) }") |> List.map fst)

[<Fact>]
let ``The dimensions a function is passed written without their storage share one`` () =
    Assert.Equal<string list>(
        [ "Time(a), Time(a) -> bool"; "Time(a), Time(b) -> bool"; "Time(a), Time(b), Time(b) -> bool" ],
        typesAfter dimensions
            "def overlap: Time, Time -> bool = { a, b -> true }\ndef apart: Time(a), Time(b) -> bool = { a, b -> true }\ndef mixed: Time(a), Time, Time -> bool = { s, t, u -> true }"
            [ "overlap"; "apart"; "mixed" ])

[<Fact>]
let ``A call that passes differently stored values to dimensions written without their storage is a mismatch`` () =
    let source = "def overlap: Time, Time -> bool = { a, b -> true }\ndef both = overlap(1ns, 2.0ns)"
    Assert.Equal<(string * string) list>([ DiagnosticCodes.TypeMismatch.Code, "2.0ns" ], reportedAt (dimensions + source))
    let message = (Compiler.compile (withFinalNewline (dimensions + source))).Diagnostics.Head.Message
    Assert.Equal("'float' vs 'int'", message)
    Assert.Empty(reportedAt (dimensions + "def apart: Time(a), Time(b) -> bool = { a, b -> true }\ndef both = apart(1ns, 2.0ns)"))

[<Fact>]
let ``A dimension in a result written without its storage is of the one the body makes it`` () =
    Assert.Equal<string list>(
        [ "list(Time(a)) -> Time(a)"; "Time(int)"; "int -> Time(int)" ],
        typesAfter dimensions
            "def total: list(Time) -> Time = { ts -> list.sum(ts) }\ndef second: Time = 1_000_000_000ns\ndef seconds: int -> Time = { n -> n * 1_000_000_000ns }"
            [ "total"; "second"; "seconds" ])

[<Fact>]
let ``Hover shows the one Num of the storage the parameters share`` () =
    let source = withFinalNewline (dimensions + "def overlap: Time, Time -> Time = { a, b -> a + b }")
    let result = Compiler.compile source
    Assert.Empty(result.Diagnostics)
    let hover = Hover.typeAt Unicode result (offsetOf source "overlap" 0 0) |> Option.map _.Type
    Assert.Equal(Some "def overlap: [Num(a)] Time(a), Time(a) -> Time(a)", hover)

[<Fact>]
let ``A constrained dimension is of its one storage, and a concrete storage requires nothing`` () =
    Assert.Equal<string list>(
        [ "Bytes(int) -> Bytes(int)"; "Time(int) -> Time(int)" ],
        typesAfter dimensions "def size: Bytes -> Bytes = { b -> b }\ndef ints: Time(int) -> Time(int) = { t -> t }" [ "size"; "ints" ])

[<Fact>]
let ``A generic function takes a measured value of any storage`` () =
    Assert.Empty(reportedAt (dimensions + "def same: Time(a) -> Time(a) = { t -> t }\ndef ints: Time(int) -> Time(int) = { t -> same(t) }\ndef floats: Time(float) -> Time(float) = { t -> same(t) }"))

[<Fact>]
let ``Measured values of other storages, other dimensions, or no dimension are different types`` () =
    let mismatch = DiagnosticCodes.TypeMismatch.Code
    Assert.Equal(mismatch, (reportedAt (dimensions + "def f: Time(int) -> Time(float) = { t -> t }")).Head |> fst)
    Assert.Equal(mismatch, (reportedAt (dimensions + "def f: Time(float) -> Length = { t -> t }")).Head |> fst)
    Assert.Equal(mismatch, (reportedAt (dimensions + "def f: Time(int) -> int = { t -> t }")).Head |> fst)
    Assert.Equal(mismatch, (reportedAt (dimensions + "def f: int -> Time(int) = { t -> t }")).Head |> fst)

[<Fact>]
let ``A storage no parameter fixes is NYX0293 at the dimension`` () =
    let code = DiagnosticCodes.StorageOnlyInResult.Code
    Assert.Equal<(string * string) list>([ code, "Time(a)" ], reportedAt (dimensions + "def make: int -> Time(a) = { n -> make(n) }") |> List.filter (fst >> (=) code))
    Assert.Empty(reportedAt (dimensions + "def average: list(Time(a)) -> Time(a) = { ts -> average(ts) }"))

[<Fact>]
let ``A storage written for a constrained dimension is NYX0294`` () =
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.ConstrainedDimensionStorage.Code, "Bytes(a)" ],
        reportedAt (dimensions + "def f: Bytes(a) -> Bytes = { b -> b }"))

[<Fact>]
let ``A local def's signature is read as a top-level one's`` () =
    Assert.Empty(reportedAt (dimensions + "def outer = { ->\n  def inner: Time -> bool = { t -> true }\n  inner\n}"))
    Assert.Contains(
        DiagnosticCodes.StorageOnlyInResult.Code,
        reportedAt (dimensions + "def make: int -> Time(int) = { n -> make(n) }\ndef outer = { ->\n  def inner: int -> Time(b) = { n -> make(n) }\n  inner\n}") |> List.map fst)
