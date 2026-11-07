module ParserTests.Features.Blocks

open Xunit
open FsUnit.Xunit
open Parser.Program
open ParserTestHelpers

[<Fact>]
let ``Parse block with two literals`` () =
    let result = parseTestFile "test_block_two_literals.nyx"

    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 1
        match module'.[0] with
        | Kind (Def (ValueDef (_, name, _, Kind (Block statements)))) ->
            name |> should equal "test"
            statements |> should haveLength 2
            match statements.[0] with
            | Kind (ExprStatement (Kind (LiteralExpr (IntLit 1)))) -> ()
            | _ -> failwith "Expected first statement to be literal 1"
            match statements.[1] with
            | Kind (ExprStatement (Kind (LiteralExpr (IntLit 2)))) -> ()
            | _ -> failwith "Expected second statement to be literal 2"
        | _ -> failwith "Expected a block with two literal statements"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``Parse sample-block.nyx file`` () =
    let result = parseTestFile "sample-block.nyx"

    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> List.length |> should be (greaterThan 2)
        match module'.[0] with
        | Kind (ModuleDecl name) -> name |> should equal "BlockTest"
        | _ -> failwith "Expected ModuleDecl"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``Parse sibling blocks`` () =
    let result = parseTestFile "test_block_sibling_blocks.nyx"

    result |> isOk |> should equal true
    match result with
    | Result.Ok module' ->
        module' |> should haveLength 2
        match module'.[0], module'.[1] with
        | Kind (Def (ValueDef (_, firstName, _, Kind (Block firstStatements)))), Kind (Def (ValueDef (_, secondName, _, Kind (Block secondStatements)))) ->
            firstName |> should equal "first"
            secondName |> should equal "second"
            firstStatements |> should haveLength 2
            secondStatements |> should haveLength 2
        | _ -> failwith "Expected two sibling block definitions"
    | Result.Error err -> failwith $"Parse failed: {err}"

let private parseSource (source: string) =
    match parseModuleFile "t.nyx" source with
    | Result.Ok module' -> module'
    | Result.Error err -> failwith $"Parse failed: {err}"

let private textOf (source: string) (span: SourceSpan) = source.Substring(span.Start, span.End - span.Start)

/// The block a `def name =` in `module'` is, and the source text of it and of each of its statements.
let private defBlockTexts name (source: string) =
    parseSource source
    |> List.pick (function
        | Kind (Def (ValueDef (_, n, _, (Kind (Block statements) as block)))) when n = name ->
            Some (textOf source block.Span, statements |> List.map (fun statement -> textOf source statement.Span))
        | _ -> None)

[<Fact>]
let ``A comment after a statement in the middle of a def block does not end the block`` () =
    let source = "def total =\n  def x = 1\n  def taxRate = 0.2 -- the rate\n  x + 100\n"
    defBlockTexts "total" source
    |> should equal ("def x = 1\n  def taxRate = 0.2 -- the rate\n  x + 100", [ "def x = 1"; "def taxRate = 0.2"; "x + 100" ])

[<Fact>]
let ``A comment after the last statement of a def block ends it there`` () =
    let source = "def total =\n  def x = 1\n  x + 100 -- the total\ndef after = 2\n"
    defBlockTexts "total" source |> should equal ("def x = 1\n  x + 100", [ "def x = 1"; "x + 100" ])
    parseSource source |> List.map (fun item -> textOf source item.Span) |> List.last |> should equal "def after = 2"

/// The kind of each item `source` parses to, with the source text it spans.
let private itemsOf (source: string) =
    parseSource source
    |> List.map (fun item ->
        let name =
            match item.Kind with
            | ModuleDecl name -> $"module {name}"
            | Def (ValueDef (_, name, _, _)) -> $"def {name}"
            | other -> failwith $"Unexpected item %A{other}"
        name, textOf source item.Span)

[<Fact>]
let ``A comment after an item in the middle of a module block does not end the block`` () =
    let source = "module app\nmodule m =\n  def a = 1 -- note\n  def b = 2\ndef c = 3\n"
    itemsOf source
    |> should equal [ "module app", "module app"
                      "module app.m", "module m =\n  def a = 1 -- note\n  def b = 2"
                      "def app.m.a", "def a = 1"
                      "def app.m.b", "def b = 2"
                      "def c", "def c = 3" ]

[<Fact>]
let ``A comment after the last item of a module block ends it there`` () =
    let source = "module app\nmodule m =\n  def a = 1\n  def b = 2 -- note\ndef c = 3\n"
    itemsOf source
    |> should equal [ "module app", "module app"
                      "module app.m", "module m =\n  def a = 1\n  def b = 2"
                      "def app.m.a", "def a = 1"
                      "def app.m.b", "def b = 2"
                      "def c", "def c = 3" ]
