module ParserTests.Features.Literals

open Xunit
open FsUnit.Xunit
open Parser.Program
open ParserTestHelpers

[<Fact>]
let ``list spread desugars to concatenation`` () =
    match parseModule "def x = [1, ...xs, 2, 3, ...ys]" with
    | Result.Ok [ Kind (Def (ValueDef (_, "x", _, Kind (BinaryOp ("++", Kind (ListExpr [ Kind (LiteralExpr (IntLit 1)) ]),
                                                                 Kind (BinaryOp ("++", Kind (IdentifierExpr "xs"),
                                                                    Kind (BinaryOp ("++", Kind (ListExpr [ Kind (LiteralExpr (IntLit 2)); Kind (LiteralExpr (IntLit 3)) ]),
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
