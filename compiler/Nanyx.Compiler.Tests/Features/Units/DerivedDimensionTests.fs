/// Derived dimensions (#2816): products and quotients of dimensions in types and in arithmetic, a dimension that names
/// one, and the errors.
module Nanyx.Compiler.Tests.Features.DerivedDimension

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport
open Nanyx.Compiler.Tests.Features.UnitsFixture

let private length = "dimension Length\n@baseUnit unit m: Length\nunit km: Length = 1_000m\n"

/// The code and message of the one diagnostic compiling `source` after the dimensions gives.
let private only (source: string) =
    let code, message, _ = onlyDiagnostic (withFinalNewline (length + source))
    code, message

[<Fact>]
let ``A product or quotient of measured values adds or takes off the exponents`` () =
    Assert.Equal<string list>(
        [ "Length(float) / Time(float)"; "Length(int) * Length(int)"; "Length(float) / (Time(float) * Time(float))"; "Length(float)"; "float" ],
        typesAfter length
            "def a = 100.0m / 9.58s\ndef b = 2m * 3m\ndef c = 10.0m / 2.0s / 1.0s\ndef d = a * 1.0s\ndef e = 1.0km / 1.0m"
            [ "a"; "b"; "c"; "d"; "e" ])

[<Fact>]
let ``A quotient is of the storage's fraction, so dividing ints is not rounded`` () =
    Assert.Equal<string list>([ "Length(float) / Time(float)"; "1 / Time(float)" ], typesAfter length "def a = 100m / 3s\ndef b = 1.0 / 2.0s" [ "a"; "b" ])

[<Fact>]
let ``Derived dimensions are written in annotations with * and /, the storage on each part`` () =
    Assert.Equal<string list>(
        [ "Length(float), Time(float) -> Length(float) / Time(float)"; "Time(a) * Time(a) -> Time(a) * Time(a)" ],
        typesAfter length
            "def speed: Length(float), Time(float) -> Length(float) / Time(float) = { d, t -> d / t }\ndef square: Time(a) * Time(a) -> Time(a) * Time(a) = { t -> t }"
            [ "speed"; "square" ])

[<Fact>]
let ``A bare derived dimension in a signature is of one storage, shared by its parts`` () =
    Assert.Equal<string list>(
        [ "Length(a) / Time(a) -> Length(a) / Time(a)" ],
        typesAfter length "def same: Length / Time -> int = { v -> 1 }\ndef sameAgain: Length(a) / Time -> Length(a) / Time(a) = { v -> v }" [ "sameAgain" ])
    Assert.Equal<string list>([ "Length(a) / Time(a) -> int" ], typesAfter length "def same: Length / Time -> int = { v -> 1 }" [ "same" ])

[<Fact>]
let ``A dimension that names a derived dimension is the same dimension wherever it is written`` () =
    Assert.Equal<string list>(
        [ "Length(float), Time(float) -> Length(float) / Time(float)"; "Length(float) / Time(float)"; "Length(float)" ],
        typesAfter length
            "dimension Speed = Length / Time\ndef speed: Length(float), Time(float) -> Speed(float) = { d, t -> d / t }\ndef v: Length(float) / Time(float) = speed(100.0m, 9.58s)\ndef back: Length(float) * Time(float) / Time(float) = 2.0m"
            [ "speed"; "v"; "back" ])

[<Fact>]
let ``Two parts of a derived dimension stored differently are NYX0295`` () =
    Assert.Equal(
        (DiagnosticCodes.DerivedDimensionStorages.Code,
         "The parts of 'Length / Time' are stored as 'a' and as 'float', and a derived dimension's parts share one storage: write it on every part, or on none"),
        only "def f: Length(a) / Time(float) -> int = { _ -> 1 }")
    Assert.Equal(DiagnosticCodes.DerivedDimensionStorages.Code, fst (only "dimension Speed = Length(float) / Time"))

// A storage was told apart from another by how it is written, and one that names an associated type was written as its
// node's id, so two parts written `Num(a).fraction` were two storages, `#183` and `#187` (#2879).
[<Fact>]
let ``Parts stored as an associated type share it, and NYX0295 writes one as it is written`` () =
    Assert.Equal<string list>(
        [ "Length(a), Time(a) -> Length(fraction) / Time(fraction)" ],
        typesAfter length "def speed: Length(a), Time(a) -> Length(Num(a).fraction) / Time(Num(a).fraction) = { d, t -> d / t }" [ "speed" ])
    Assert.Equal(
        (DiagnosticCodes.DerivedDimensionStorages.Code,
         "The parts of 'Length / Time' are stored as 'a' and as 'Num(a).fraction', and a derived dimension's parts share one storage: write it on every part, or on none"),
        only "def f: Length(a) / Time(Num(a).fraction) -> int = { _ -> 1 }")

[<Fact>]
let ``A part of a derived dimension that is no dimension is NYX0296`` () =
    Assert.Equal(
        (DiagnosticCodes.NotADimension.Code, "'int' is no dimension, and a derived dimension multiplies and divides dimensions, as `Length / Time` does"),
        only "def f: Length / int -> int = { _ -> 1 }")
    Assert.Equal(DiagnosticCodes.NotADimension.Code, fst (only "dimension Speed = Length / string"))

[<Fact>]
let ``A part that is no dimension beside a constrained dimension is NYX0296 alone`` () =
    let code, message, at = onlyDiagnostic (withFinalNewline "dimension Mass(float)\n@baseUnit unit kg: Mass\ndef f: Mass / int -> int = { _ -> 1 }")
    Assert.Equal(DiagnosticCodes.NotADimension.Code, code)
    Assert.Equal("'int' is no dimension, and a derived dimension multiplies and divides dimensions, as `Length / Time` does", message)
    Assert.Equal("int", at)

[<Fact>]
let ``// across two dimensions, and a power, say how to write them`` () =
    Assert.Equal(
        (DiagnosticCodes.TypeMismatch.Code,
         "`//` rounds only between two values of one dimension, or by a number, and 'Length' // 'Time' would round a 'Length / Time': divide with `/`"),
        only "def x = 1.0m // 2.0s")
    Assert.Equal(
        (DiagnosticCodes.TypeMismatch.Code, "A power of a 'Length' has no syntax yet: multiply it by itself, as `t * t` does, for a 'Length * Length'"),
        only "def x = 1.0m ** 2")
