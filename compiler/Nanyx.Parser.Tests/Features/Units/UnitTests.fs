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
    Assert.True(isError (parseModule "def x = 1e5"))
    Assert.True(isError (parseModule "def x = 1e3ms"))

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
    | Result.Ok [ Kind (DimensionDecl(false, "Time", None))
                  Kind (DimensionDecl(false, "Bytes", Some (Kind (TypeName "int"))))
                  Kind (DimensionDecl(true, "Length", Some (Kind (TypeName "float")))) ] -> ()
    | other -> failwith $"Unexpected parse result: %A{other}"

[<Fact>]
let ``A unit is declared as a multiple of another unit, or as the base unit, marked on its line`` () =
    match parseModule "@baseUnit unit ns: Time\nunit us: Time = 1_000ns\nexport unit mm: Length = 0.001m" with
    | Result.Ok [ Kind (Attribute("baseUnit", None))
                  Kind (UnitDecl(false, "ns", "Time", None))
                  Kind (UnitDecl(false, "us", "Time", Some (Kind (LiteralExpr(UnitLit(whole, "ns"))))))
                  Kind (UnitDecl(true, "mm", "Length", Some (Kind (LiteralExpr(UnitLit(fraction, "m")))))) ] ->
        Assert.Equal(number 1000L 0 false, whole)
        Assert.Equal(number 1L -3 true, fraction)
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
    Assert.Equal(("Expected a number with a unit, such as `1_000us`, found `us`", "us"), parseErrorOf "unit ms: Time = us")

[<Fact>]
let ``A declaration ends its line`` () =
    Assert.Equal(("Expected the end of the line, found `Length`", "Length"), parseErrorOf "dimension Time Length")
    Assert.Equal(("Expected `:`, found `Time`", "Time"), parseErrorOf "unit ms Time")

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
                  Kind (DimensionDecl(true, "Time", None))
                  Kind (Attribute("baseUnit", None))
                  Kind (UnitDecl(false, "ns", "Time", None))
                  Kind (UnitDecl(false, "us", "Time", Some _)) ] -> ()
    | other -> failwith $"Unexpected parse result: %A{other}"

[<Fact>]
let ``A number with a unit is not a pattern yet, but a guard compares with one`` () =
    Assert.Equal(
        ("A number with a unit is not a pattern yet: compare the value in a guard instead, as in `| == 0ms ->`", "0ms -> true"),
        parseErrorOf "def f = { t ->\n  match t\n    | 0ms -> true\n    | _ -> false\n}")
    readsWhole "def f = { t ->\n  match t\n    | == 0ms -> true\n    | _ -> false\n}"
