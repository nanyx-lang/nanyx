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
            | Kind (ExprStatement (Kind (LiteralExpr (IntLit64 1L)))) -> ()
            | _ -> failwith "Expected first statement to be literal 1"
            match statements.[1] with
            | Kind (ExprStatement (Kind (LiteralExpr (IntLit64 2L)))) -> ()
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

[<Fact>]
let ``An empty line between statements does not end a def block`` () =
    let source = "def x =\n  def a = 1\n\n  a\n"
    defBlockTexts "x" source |> should equal ("def a = 1\n\n  a", [ "def a = 1"; "a" ])
    parseSource source |> should haveLength 1

[<Fact>]
let ``A line of only spaces between statements does not end a def block`` () =
    let source = "def x =\n  def a = 1\n    \n  a\n"
    defBlockTexts "x" source |> should equal ("def a = 1\n    \n  a", [ "def a = 1"; "a" ])

[<Fact>]
let ``A comment line at the indent between statements does not end a def block`` () =
    let source = "def x =\n  def a = 1\n  -- note\n  a\n"
    defBlockTexts "x" source |> should equal ("def a = 1\n  -- note\n  a", [ "def a = 1"; "a" ])
    parseSource source |> should haveLength 1

[<Fact>]
let ``Several blank and comment lines between statements do not end a def block`` () =
    let source = "def x =\n  def a = 1\n\n-- one\n    -- two\n\n  a\n"
    defBlockTexts "x" source |> should equal ("def a = 1\n\n-- one\n    -- two\n\n  a", [ "def a = 1"; "a" ])

[<Fact>]
let ``Code at a smaller indent after blank and comment lines still ends a def block`` () =
    let source = "def x =\n  def a = 1\n  a\n\n  -- note\n\ndef y = 2\n"
    defBlockTexts "x" source |> should equal ("def a = 1\n  a", [ "def a = 1"; "a" ])
    parseSource source |> List.map (fun item -> textOf source item.Span) |> should equal [ "def x =\n  def a = 1\n  a"; "def y = 2" ]

[<Fact>]
let ``A comment line skipped between statements is recorded once`` () =
    let source = "def x =\n  def a = 1\n  -- note\n  a\n  -- after\ndef y = 2\n"
    match parseModuleWithTrivia "t.nyx" source with
    | Result.Ok parsed ->
        parsed.Trivia
        |> List.choose (function
            | LineComment(text, _, _) -> Some text
            | _ -> None)
        |> should equal [ "-- note"; "-- after" ]
    | Result.Error err -> failwith $"Parse failed: {err.Message}"

[<Fact>]
let ``An empty line in a match arm's block body does not end the arm`` () =
    let source = "def f = { x ->\n  match x\n    | 1 ->\n      def a = 1\n\n      a\n    | _ -> 0\n}\n"
    let parsed = parseSource source
    parsed |> should haveLength 1
    textOf source parsed.[0].Span |> should equal (source.TrimEnd '\n')

[<Fact>]
let ``An empty line between items does not end a module block`` () =
    let source = "module app\nmodule m =\n  def a = 1\n\n  def b = 2\ndef c = 3\n"
    itemsOf source
    |> should equal [ "module app", "module app"
                      "module app.m", "module m =\n  def a = 1\n\n  def b = 2"
                      "def app.m.a", "def a = 1"
                      "def app.m.b", "def b = 2"
                      "def c", "def c = 3" ]

[<Fact>]
let ``A line of only the indent between items does not end a module block`` () =
    let source = "module app\nmodule m =\n  def a = 1\n  \n  def b = 2\n"
    itemsOf source |> List.map fst |> should equal [ "module app"; "module app.m"; "def app.m.a"; "def app.m.b" ]

[<Fact>]
let ``A comment line between items does not end a module block`` () =
    let source = "module app\nmodule m =\n  def a = 1\n  -- note\n  def b = 2\n"
    itemsOf source |> List.map fst |> should equal [ "module app"; "module app.m"; "def app.m.a"; "def app.m.b" ]

[<Fact>]
let ``Code at a smaller indent after blank and comment lines still ends a module block`` () =
    let source = "module app\nmodule m =\n  def a = 1\n\n  -- note\n\ndef b = 2\n"
    itemsOf source
    |> should equal [ "module app", "module app"
                      "module app.m", "module m =\n  def a = 1"
                      "def app.m.a", "def a = 1"
                      "def b", "def b = 2" ]
