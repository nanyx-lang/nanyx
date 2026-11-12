module ParserTests.Features.Literals

open Xunit
open FsUnit.Xunit
open Parser.Program
open ParserTestHelpers

[<Fact>]
let ``list spread desugars to concatenation`` () =
    match parseModule "def x = [1, ...xs, 2, 3, ...ys]" with
    | Result.Ok [ Kind (Def (ValueDef (_, "x", _, Kind (BinaryOp ("++", _, Kind (ListExpr [ Kind (LiteralExpr (IntLit 1L)) ]),
                                                                 Kind (BinaryOp ("++", _, Kind (IdentifierExpr "xs"),
                                                                    Kind (BinaryOp ("++", _, Kind (ListExpr [ Kind (LiteralExpr (IntLit 2L)); Kind (LiteralExpr (IntLit 3L)) ]),
                                                                                          Kind (IdentifierExpr "ys")))))))))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``a list of only a spread is the spread itself, and no spread is a plain list`` () =
    match parseModule "def x = [...xs]\ndef y = [1, 2]\ndef z = []" with
    | Result.Ok [ Kind (Def (ValueDef (_, "x", _, Kind (IdentifierExpr "xs"))))
                  Kind (Def (ValueDef (_, "y", _, Kind (ListExpr [ _; _ ]))))
                  Kind (Def (ValueDef (_, "z", _, Kind (ListExpr [])))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``list items may be written one per line, without commas, with the ] on a line of its own`` () =
    let oneLine = parseModule "def x = [1, 2, ...xs]"
    let byLine = parseModule "def x = [\n  1\n  2\n  ...xs\n]"
    let byLineWithCommas = parseModule "def x = [\n  1,\n  2,\n  ...xs\n]"
    match oneLine, byLine, byLineWithCommas with
    | Result.Ok expected, Result.Ok actual, Result.Ok withCommas ->
        actual |> should equal expected
        withCommas |> should equal expected
    | other -> failwith $"Unexpected: %A{other}"

[<Fact>]
let ``a list in a block may put its ] on a line of its own, at the indent of the line that opened it`` () =
    let oneLine = parseModule """
def main = {
  def xs = [1, 2]
  f([3, 4])
}
"""
    let byLine = parseModule """
def main = {
  def xs = [
    1,
    2
  ]
  f([
    3,
    4
  ])
}
"""
    match oneLine, byLine with
    | Result.Ok expected, Result.Ok actual -> actual |> should equal expected
    | other -> failwith $"Unexpected: %A{other}"

[<Fact>]
let ``an empty list may span lines`` () =
    match parseModule "def z = [\n]" with
    | Result.Ok [ Kind (Def (ValueDef (_, "z", _, Kind (ListExpr [])))) ] -> ()
    | other -> failwith $"Unexpected: %A{other}"

[<Fact>]
let ``a list item on a line indented differently is an error, not a new item`` () =
    parseModule "def x = [\n  1\n    2\n]" |> isError |> should equal true

[<Fact>]
let ``Parse literals.nyx file`` () =
    let result = parseTestFile "literals.nyx"

    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 6  // 1 module + 5 defs
        match module'.[0] with
        | Kind (ModuleDecl name) -> name |> should equal "Literals"
        | _ -> failwith "Expected ModuleDecl"

        let defs = module' |> List.tail
        defs |> should haveLength 5
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``Parse interpolated string literal`` () =
    let source = "def msg = \"Hello {name}\""
    match parseModule source with
    | Result.Error err -> failwith $"Parse failed: {err}"
    | Result.Ok module' ->
        match module' with
        | [Kind (Def (ValueDef(_, "msg", _, Kind (InterpolatedString parts))))] ->
            parts |> should haveLength 2
        | _ -> failwith "Expected interpolated string"

[<Fact>]
let ``Parse single-quoted string without interpolation`` () =
    let source = "def msg = 'Hello {name}'"
    match parseModule source with
    | Result.Error err -> failwith $"Parse failed: {err}"
    | Result.Ok module' ->
        match module' with
        | [Kind (Def (ValueDef(_, "msg", _, Kind (LiteralExpr (StringLit value)))))] ->
            value |> should equal "Hello {name}"
        | _ -> failwith "Expected plain string literal"

[<Fact>]
let ``Parse multiline string literal`` () =
    let source = "def msg = \"Hello\nworld\""
    match parseModule source with
    | Result.Error err -> failwith $"Parse failed: {err}"
    | Result.Ok module' ->
        match module' with
        | [Kind (Def (ValueDef(_, "msg", _, Kind (LiteralExpr (StringLit value)))))] ->
            value |> should equal "Hello\nworld"
        | _ -> failwith "Expected multiline string literal"

[<Fact>]
let ``Parse dotted def name`` () =
    let source = "def Person.fullName = { p -> p.firstName }"
    match parseModule source with
    | Result.Error err -> failwith $"Parse failed: {err}"
    | Result.Ok module' ->
        match module' with
        | [Kind (Def (ValueDef(_, name, _, _)))] -> name |> should equal "Person.fullName"
        | _ -> failwith "Expected dotted def name"

[<Fact>]
let ``a name that starts with true or false is a name, not a boolean`` () =
    match parseModule "def trueish = 1\ndef falsey = 2\ndef total = trueish + falsey" with
    | Result.Ok [ Kind (Def (ValueDef (_, "trueish", _, Kind (LiteralExpr (IntLit 1L)))))
                  Kind (Def (ValueDef (_, "falsey", _, Kind (LiteralExpr (IntLit 2L)))))
                  Kind (Def (ValueDef (_, "total", _, Kind (BinaryOp ("+", _, Kind (IdentifierExpr "trueish"),
                                                                        Kind (IdentifierExpr "falsey")))))) ] -> ()
    | other -> failwith $"Unexpected: %A{other}"

[<Theory>]
[<InlineData("nan")>]
[<InlineData("inf")>]
[<InlineData("infinity")>]
[<InlineData("NaN")>]
[<InlineData("Infinity")>]
let ``nan, inf and infinity are names, not floats`` (name: string) =
    match parseModule $"def {name} = 3\ndef x = {name}\ndef scale = {{ {name} -> {name} * 2 }}" with
    | Result.Ok [ Kind (Def (ValueDef (_, defName, _, Kind (LiteralExpr (IntLit 3L)))))
                  Kind (Def (ValueDef (_, "x", _, Kind (IdentifierExpr used))))
                  Kind (Def (ValueDef (_, "scale", _, Kind (Lambda ([ (param, None) ],
                                                                    Kind (BinaryOp ("*", _, Kind (IdentifierExpr body),
                                                                                    Kind (LiteralExpr (IntLit 2L)))), _))))) ] ->
        Assert.Equal(name, defName)
        Assert.Equal(name, used)
        Assert.Equal(name, param)
        Assert.Equal(name, body)
    | other -> failwith $"Unexpected: %A{other}"

[<Fact>]
let ``float literals with a fraction or an exponent still parse`` () =
    match parseModule "def a = 1.5\ndef b = 2.5e3" with
    | Result.Ok [ Kind (Def (ValueDef (_, "a", _, Kind (LiteralExpr (FloatLit a)))))
                  Kind (Def (ValueDef (_, "b", _, Kind (LiteralExpr (FloatLit b))))) ] ->
        Assert.Equal(1.5, a)
        Assert.Equal(2500.0, b)
    | other -> failwith $"Unexpected: %A{other}"

[<Fact>]
let ``Digits may be grouped with underscores, which are dropped`` () =
    let source = """def a = 1_000_000
def b = 3.141_592
def c = -1_000
def d = 1_000.5e1_0
def e = 0x1F"""
    match parseModule source with
    | Result.Ok [ Kind (Def (ValueDef (_, "a", _, Kind (LiteralExpr (IntLit a)))))
                  Kind (Def (ValueDef (_, "b", _, Kind (LiteralExpr (FloatLit b)))))
                  Kind (Def (ValueDef (_, "c", _, Kind (LiteralExpr (IntLit c)))))
                  Kind (Def (ValueDef (_, "d", _, Kind (LiteralExpr (FloatLit d)))))
                  Kind (Def (ValueDef (_, "e", _, Kind (LiteralExpr (IntLit e))))) ] ->
        Assert.Equal(1_000_000L, a)
        Assert.Equal(3.141592, b)
        Assert.Equal(-1000L, c)
        Assert.Equal(1000.5e10, d)
        Assert.Equal(31L, e)
    | other -> failwith $"Unexpected: %A{other}"

[<Fact>]
let ``An int literal holds 64 bits`` () =
    let source =
        """def big = 9_223_372_036_854_775_807
def small = -9_223_372_036_854_775_808
def hex = 0x7FFFFFFFFFFF"""
    match parseModule (source.ReplaceLineEndings "\n") with
    | Result.Ok [ Kind (Def (ValueDef (_, "big", _, Kind (LiteralExpr (IntLit big)))))
                  Kind (Def (ValueDef (_, "small", _, Kind (LiteralExpr (IntLit small)))))
                  Kind (Def (ValueDef (_, "hex", _, Kind (LiteralExpr (IntLit hex))))) ] ->
        Assert.Equal(System.Int64.MaxValue, big)
        Assert.Equal(System.Int64.MinValue, small)
        Assert.Equal(0x7FFF_FFFF_FFFFL, hex)
    | other -> failwith $"Unexpected: %A{other}"

[<Theory>]
[<InlineData("1__0")>]
[<InlineData("1_")>]
[<InlineData("1_.5")>]
[<InlineData("1._5")>]
[<InlineData("1.5_")>]
let ``A _ in a number that is not between two digits is an error`` (number: string) =
    match parseModule $"def x = {number}" with
    | Result.Error err -> Assert.Contains("A `_` in a number goes between two digits", err)
    | other -> failwith $"Unexpected: %A{other}"

[<Fact>]
let ``A name that starts with _ after a number is still a name`` () =
    let source = """def _1 = 2
def x = [1, _1]"""
    match parseModule source with
    | Result.Ok [ _; Kind (Def (ValueDef (_, "x", _, Kind (ListExpr [ Kind (LiteralExpr (IntLit 1L)); Kind (IdentifierExpr "_1") ])))) ] -> ()
    | other -> failwith $"Unexpected: %A{other}"

// Literal Tests
[<Fact>]
let ``Parse string literal`` () =
    let result = parseModule "def message = \"Hello world\""
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (LiteralExpr (StringLit value))))) ->
            name |> should equal "message"
            value |> should equal "Hello world"
        | _ -> failwith "Expected Def with StringLit"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse integer literal`` () =
    let result = parseModule "def count = 42"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (LiteralExpr (IntLit value))))) ->
            name |> should equal "count"
            value |> should equal 42L
        | _ -> failwith "Expected Def with IntLit"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse float literal`` () =
    let result = parseModule "def pi = 3.14159"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (LiteralExpr (FloatLit value))))) ->
            name |> should equal "pi"
            value |> should equal 3.14159
        | _ -> failwith "Expected Def with FloatLit"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse boolean true`` () =
    let result = parseModule "def flag = true"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (LiteralExpr (BoolLit value))))) ->
            name |> should equal "flag"
            value |> should equal true
        | _ -> failwith "Expected Def with BoolLit"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"

[<Fact>]
let ``Parse boolean false`` () =
    let result = parseModule "def flag = false"
    
    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (LiteralExpr (BoolLit value))))) ->
            name |> should equal "flag"
            value |> should equal false
        | _ -> failwith "Expected Def with BoolLit"
    | Result.Error err -> failwith $"Parse should succeed, got: {err}"
