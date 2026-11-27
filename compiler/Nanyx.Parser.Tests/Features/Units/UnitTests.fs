/// Units of measure: a number with a unit straight after it is one token and one literal, and `dimension` and `unit`
/// begin declarations at the start of a top-level item, though they are not reserved.
module ParserTests.Features.Units

open Xunit
open Parser.Program
open Parser.Program.Lexer
open ParserTestHelpers

/// The tokens of `source` as their kind and text, without the end of the file.
let private tokens (source: string) =
    (lex "t.nyx" source).Tokens
    |> Array.filter (fun token -> token.Kind <> EndOfFile)
    |> Array.map (fun token -> token.Kind, source.Substring(token.Start, token.End - token.Start))
    |> List.ofArray

/// The value of `def x = <written>`.
let private valueOf (written: string) =
    match parseModule $"def x = {written}" with
    | Result.Ok [ Kind (Def (ValueDef(_, "x", None, value))) ] -> value.Kind
    | other -> failwith $"Unexpected parse result: %A{other}"

let private number digits exponent isDecimal = { Digits = bigint (digits: int64); Exponent = exponent; Decimal = isDecimal }

[<Fact>]
let ``A number with a unit straight after it is one token`` () =
    Assert.Equal<(TokenKind * string) list>(
        [ Number, "500ms"; Number, "1_000ns"; Number, "0.5s"; Number, "1.5e3ms"; Number, "50KiB" ],
        tokens "500ms 1_000ns 0.5s 1.5e3ms 50KiB")

[<Fact>]
let ``A number with a space before a name is a number and a name`` () =
    Assert.Equal<(TokenKind * string) list>([ Number, "100"; Name, "ms" ], tokens "100 ms")

[<Fact>]
let ``An exponent is part of the number, not a unit`` () =
    Assert.Equal<(TokenKind * string) list>([ Number, "1e5"; Number, "2.5E-3"; Number, "2em" ], tokens "1e5 2.5E-3 2em")

[<Fact>]
let ``A hexadecimal, octal or binary number takes no unit`` () =
    Assert.Equal<(TokenKind * string) list>([ Number, "0x1B"; Number, "0b101" ], tokens "0x1B 0b101")
    let lexed = lex "t.nyx" "0x1Bms"
    Assert.Equal<(TokenKind * string) list>([ Number, "0x1B"; Name, "ms" ], tokens "0x1Bms")
    match lexed.Errors with
    | [ error ] ->
        Assert.StartsWith("A hexadecimal, octal or binary number takes no unit", error.Message)
        Assert.Equal((4, 6), (error.Span.Start, error.Span.End))
    | other -> failwith $"Unexpected errors: %A{other}"

[<Fact>]
let ``A unit literal keeps its number exactly, and whether it was written whole or as a decimal`` () =
    Assert.Equal(LiteralExpr(UnitLit(number 500L 0 false, "ms")), valueOf "500ms")
    Assert.Equal(LiteralExpr(UnitLit(number 1000L 0 false, "ns")), valueOf "1_000ns")
    Assert.Equal(LiteralExpr(UnitLit(number 5L -1 true, "s")), valueOf "0.5s")
    Assert.Equal(LiteralExpr(UnitLit(number 15L 2 true, "ms")), valueOf "1.5e3ms")
    Assert.Equal(LiteralExpr(UnitLit(number 1L -3 true, "m")), valueOf "0.001m")

[<Fact>]
let ``A sign written straight before a unit literal is part of it`` () =
    Assert.Equal(LiteralExpr(UnitLit(number -500L 0 false, "ms")), valueOf "-500ms")
    Assert.Equal(LiteralExpr(UnitLit(number -15L -1 true, "s")), valueOf "-1.5s")

[<Fact>]
let ``A number without a unit is the literal it always was`` () =
    Assert.Equal(LiteralExpr(IntLit 1000I), valueOf "1_000")
    Assert.Equal(LiteralExpr(FloatLit 1.5), valueOf "1.5")
    Assert.Equal(LiteralExpr(IntLit 27I), valueOf "0x1B")

[<Fact>]
let ``An exponent comes after a fraction in a unit literal, as in a float`` () =
    Assert.Equal(("An exponent comes after a fraction: write '1.0e5', not '1e5'", "1e5"), parseErrorAt "def x = 1e5")
    Assert.Equal(("An exponent comes after a fraction: write '1.0e3ms', not '1e3ms'", "1e3ms"), parseErrorAt "def x = 1e3ms")
    Assert.Equal(("An exponent comes after a fraction: write '2.0E-3', not '2E-3'", "2E-3"), parseErrorAt "def x = -2E-3 + 1")

[<Fact>]
let ``A misplaced underscore in a unit literal is reported`` () =
    Assert.Equal(("A `_` in a number goes between two digits: '1__000' is not a number", "1__000ms"), parseErrorOf "def x = 1__000ms")

[<Fact>]
let ``A unit literal is an operand and an argument`` () =
    match valueOf "sleep(500ms) + 2 * 1s" with
    | BinaryOp("+", _, Kind (FunctionCall("sleep", _, [ Kind (LiteralExpr(UnitLit(_, "ms"))) ])), Kind (BinaryOp("*", _, _, Kind (LiteralExpr(UnitLit(_, "s")))))) -> ()
    | other -> failwith $"Unexpected value: %A{other}"

[<Fact>]
let ``A dimension is declared, constrained to one storage type or not`` () =
    match parseModule "dimension Time\ndimension Bytes(int)\nexport dimension Length(float)" with
    | Result.Ok [ Kind (DimensionDecl(false, "Time", None, None))
                  Kind (DimensionDecl(false, "Bytes", Some (Kind (TypeName "int")), None))
                  Kind (DimensionDecl(true, "Length", Some (Kind (TypeName "float")), None)) ] -> ()
    | other -> failwith $"Unexpected parse result: %A{other}"

[<Fact>]
let ``A unit is declared as a multiple of another unit, or as the unit the others are defined from, marked on its line`` () =
    match parseModule "@baseUnit unit ns: Time\nunit us: Time = 1_000ns\nexport unit mm: Length = 0.001m" with
    | Result.Ok [ Kind (Attribute("baseUnit", None))
                  Kind (UnitDecl(false, "ns", Kind (TypeName "Time"), None))
                  Kind (UnitDecl(false, "us", Kind (TypeName "Time"), Some (Kind (LiteralExpr(UnitLit(whole, "ns"))))))
                  Kind (UnitDecl(true, "mm", Kind (TypeName "Length"), Some (Kind (LiteralExpr(UnitLit(fraction, "m")))))) ] ->
        Assert.Equal(number 1000L 0 false, whole)
        Assert.Equal(number 1L -3 true, fraction)
    | other -> failwith $"Unexpected parse result: %A{other}"

[<Fact>]
let ``A unit can be marked @storageUnit and @baseUnit on its line`` () =
    match parseModule "@storageUnit @baseUnit unit ns: Time\n@baseUnit unit s: Time = 1_000_000_000ns" with
    | Result.Ok [ Kind (Attribute("storageUnit", None))
                  Kind (Attribute("baseUnit", None))
                  Kind (UnitDecl(false, "ns", Kind (TypeName "Time"), None))
                  Kind (Attribute("baseUnit", None))
                  Kind (UnitDecl(false, "s", Kind (TypeName "Time"), Some _)) ] -> ()
    | other -> failwith $"Unexpected parse result: %A{other}"

[<Fact>]
let ``A unit of a derived dimension is numbers of units multiplied and divided, read from the left`` () =
    let source = "unit kmh: Speed = 1km / 1hr\nunit accel: Acceleration = 1m / 1s / 1s\nunit area: Area = 1m * 1m"
    match parseModule source with
    | Result.Ok [ Kind (UnitDecl(false, "kmh", Kind (TypeName "Speed"), Some (Kind (BinaryOp("/", _, Kind (LiteralExpr(UnitLit(_, "km"))), Kind (LiteralExpr(UnitLit(_, "hr"))))))))
                  Kind (UnitDecl(false, "accel", Kind (TypeName "Acceleration"), Some (Kind (BinaryOp("/", _, Kind (BinaryOp("/", _, _, _)), Kind (LiteralExpr(UnitLit(_, "s"))))))))
                  Kind (UnitDecl(false, "area", Kind (TypeName "Area"), Some (Kind (BinaryOp("*", _, _, _))))) as area ] ->
        Assert.Equal("unit area: Area = 1m * 1m", textOf source area.Span)
    | other -> failwith $"Unexpected parse result: %A{other}"

[<Fact>]
let ``A declaration spans its line`` () =
    let source = "unit us: Time = 1_000ns\ndimension Bytes(int)"
    match parseModule source with
    | Result.Ok [ unit; dimension ] ->
        Assert.Equal("unit us: Time = 1_000ns", textOf source unit.Span)
        Assert.Equal("dimension Bytes(int)", textOf source dimension.Span)
    | other -> failwith $"Unexpected parse result: %A{other}"

[<Fact>]
let ``A unit is a multiple of a unit literal`` () =
    Assert.Equal(("A unit is a number of another unit, with no space between the number and the unit, as in `unit ms: Time = 1_000us`", "1_000 us"),
                 parseErrorOf "unit ms: Time = 1_000 us")
    Assert.Equal(("Expected a number with a unit, such as `1_000us`", "us"), parseErrorOf "unit ms: Time = us")

[<Fact>]
let ``A declaration ends its line`` () =
    Assert.Equal(("Expected the end of the line", "Length"), parseErrorOf "dimension Time Length")
    Assert.Equal(("Expected `:`", "Time"), parseErrorOf "unit ms Time")

[<Fact>]
let ``dimension and unit are names anywhere but at the start of a declaration`` () =
    let source =
        String.concat
            "\n"
            [ "def unit = 1"
              "def dimension = 2"
              "def f = { unit, dimension -> unit + dimension }"
              "unit \\println"
              "dimension(3)"
              "def g = { x -> x + unit * dimension }" ]
    match parseModule source with
    | Result.Ok [ Kind (Def (ValueDef(false, "unit", None, _))); Kind (Def (ValueDef(false, "dimension", None, _))); Kind (Def _)
                  Kind (Expr _); Kind (Expr (Kind (FunctionCall("dimension", _, _)))); Kind (Def _) ] -> ()
    | other -> failwith $"Unexpected parse result: %A{other}"

[<Fact>]
let ``export dimension with a bracket after it re-exports a module named dimension`` () =
    match parseModule "export dimension (Time)" with
    | Result.Ok [ Kind (ReExport { Source = ModuleImport "dimension" }) ] -> ()
    | other -> failwith $"Unexpected parse result: %A{other}"

[<Fact>]
let ``A module block declares dimensions and units as the top level does`` () =
    match parseModule "module m =\n  export dimension Time\n  @baseUnit unit ns: Time\n  unit us: Time = 1_000ns\n" with
    // The block's items are lifted to the top level, after a declaration of its module.
    | Result.Ok [ Kind (ModuleDecl(false, "m"))
                  Kind (DimensionDecl(true, "Time", None, None))
                  Kind (Attribute("baseUnit", None))
                  Kind (UnitDecl(false, "ns", Kind (TypeName "Time"), None))
                  Kind (UnitDecl(false, "us", Kind (TypeName "Time"), Some _)) ] -> ()
    | other -> failwith $"Unexpected parse result: %A{other}"

[<Fact>]
let ``A number with a unit is not a pattern yet, but a guard compares with one`` () =
    Assert.Equal(
        ("A number with a unit is not a pattern yet: compare the value in a guard instead, as in `| == 0ms ->`", "0ms -> true"),
        parseErrorOf "def f = { t ->\n  match t\n    | 0ms -> true\n    | _ -> false\n}")
    readsWhole "def f = { t ->\n  match t\n    | == 0ms -> true\n    | _ -> false\n}"

[<Fact>]
let ``A derived dimension is dimensions joined by * and /, read from the left, with brackets`` () =
    match parseModule "def f: Length / Time, Time * Time, Length / (Time * Time), Length / Time / Time -> int = { _ -> 1 }" with
    | Result.Ok [ Kind (Def (ValueDef(_, "f", Some (Kind (TypeFunc(Kind (TypeTuple [ first; second; third; fourth ]), _))), _))) ] ->
        match first.Kind, second.Kind, third.Kind, fourth.Kind with
        | TypeQuotient(Kind (TypeName "Length"), Kind (TypeName "Time")),
          TypeProduct(Kind (TypeName "Time"), Kind (TypeName "Time")),
          TypeQuotient(Kind (TypeName "Length"), Kind (TypeProduct(Kind (TypeName "Time"), Kind (TypeName "Time")))),
          TypeQuotient(Kind (TypeQuotient(Kind (TypeName "Length"), Kind (TypeName "Time"))), Kind (TypeName "Time")) -> ()
        | other -> failwith $"Unexpected types: %A{other}"
    | other -> failwith $"Unexpected parse result: %A{other}"

[<Fact>]
let ``Each part of a derived dimension can be written with its storage`` () =
    match parseModule "def f: Length(float) / Time(float) -> int = { _ -> 1 }" with
    | Result.Ok [ Kind (Def (ValueDef(_, "f", Some (Kind (TypeFunc(Kind (TypeQuotient(Kind (TypeApply("Length", [ _ ])), Kind (TypeApply("Time", [ _ ])))), _))), _))) ] -> ()
    | other -> failwith $"Unexpected parse result: %A{other}"

[<Fact>]
let ``A dimension can name a derived dimension`` () =
    match parseModule "export dimension Speed = Length / Time\ndimension Area = Length * Length\n" with
    | Result.Ok [ Kind (DimensionDecl(true, "Speed", None, Some (Kind (TypeQuotient _))))
                  Kind (DimensionDecl(false, "Area", None, Some (Kind (TypeProduct _)))) ] -> ()
    | other -> failwith $"Unexpected parse result: %A{other}"
