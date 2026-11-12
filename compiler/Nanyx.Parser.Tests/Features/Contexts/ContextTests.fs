module ParserTests.Features.Contexts

open Xunit
open FsUnit.Xunit
open Parser.Program
open ParserTestHelpers

[<Fact>]
let ``Parse context feature files`` () =
    parseFeatureNyxFiles "Contexts"
    |> Array.iter (fun (filePath, result) ->
        result |> isOk |> should equal true
        match result with
        | Result.Error err -> failwith $"Parse failed for {filePath}: {err}"
        | _ -> ()
    )

[<Fact>]
let ``a name that starts with use or in is not the use keyword`` () =
    let statementsOf source =
        match parseModule source with
        | Result.Ok [ Kind (Def (ValueDef (_, "main", None, Kind (Lambda ([], Kind (Block statements), _))))) ] ->
            statements |> List.map (fun (statement: Statement) -> statement.Kind)
        | other -> failwith $"Unexpected parse: %A{other}"
    match statementsOf "def main = {\n  def useful = 1\n  useful\n}" with
    | [ DefStatement (_, "useful", None, _); ExprStatement (Kind (IdentifierExpr "useful")) ] -> ()
    | other -> failwith $"Unexpected statements: %A{other}"
    match statementsOf "def main = {\n  use ctx\n  index\n}" with
    | [ UseStatement (Kind (IdentifierExpr "ctx")); ExprStatement (Kind (IdentifierExpr "index")) ] -> ()
    | other -> failwith $"Unexpected statements: %A{other}"

[<Fact>]
let ``use C(T) = expr does not parse, and the error says to write use <expr> where the context is named`` () =
    for line, head in [ "  use Show(int) = (show = { n -> \"n\" })", "Show(int)"; "  use Console = (println = log)", "Console" ] do
        let source = $"def main = {{\n{line}\n  1\n}}"
        match parseModuleFileWithNames "t.nyx" source with
        | Result.Error err ->
            // FParsec wraps a long message onto several lines.
            let message = err.Message.Replace("\r\n", " ").Replace("\n", " ")
            Assert.Contains($"`use {head} = ...` is not a way to load an instance: write `use <expr>`.", message)
            Assert.Contains($"Define the instance with `def name: {head} = (...)`, then load it with `use name`.", message)
            Assert.Equal(source.IndexOf head, err.Span.Start)
        | Result.Ok _ -> failwith $"Expected '{line.Trim()}' not to parse"

[<Fact>]
let ``An operator is required by the member it reads, among a def's contexts`` () =
    let contextsOf source =
        match parseModule source with
        | Result.Ok [ Kind (Def (ValueDef (_, _, Some { Kind = TypeWithContext (contexts, _) }, _))) ]
        | Result.Ok [ Kind (Def (ValueDef (_, _, Some { Kind = TypeContext contexts }, _))) ] -> contexts |> List.map _.Kind
        | other -> failwith $"Unexpected parse: %A{other}"
    let a = TypeExpr.synthetic (TypeVar "a")
    let binary result = TypeExpr.synthetic (TypeFunc (TypeExpr.synthetic (TypeTuple [ a; a ]), result))
    let required name memberType = TypeRecord [ TypeField (name, false, false, Some memberType, None) ]
    Assert.Equal<TypeExprKind list>([ required "+" (binary a) ], contextsOf "def add: [`+`: a, a -> a] = { + }")
    Assert.Equal<TypeExprKind list>([ required "+" (binary a) ], contextsOf "def add: [`+`: (a, a) -> a] a, a -> a = { + }")
    Assert.Equal<TypeExprKind list>(
        [ required "+" (binary a); TypeApply ("Eq", [ a ]); required "==" (binary (TypeExpr.synthetic (TypeName "bool"))); TypeName "Console" ],
        contextsOf "def f: [`+`: a, a -> a & Eq(a) & `==`: a, a -> bool & Console] a -> a = { x -> x }")
    // In brackets, `&` in a member's type is an intersection.
    let b = TypeExpr.synthetic (TypeVar "b")
    Assert.Equal<TypeExprKind list>(
        [ required "f" (TypeExpr.synthetic (TypeFunc (TypeExpr.synthetic (TypeIntersection [ a; b ]), a))); TypeName "Console" ],
        contextsOf "def f: [`f`: (a & b) -> a & Console] a -> a = { x -> x }")

[<Fact>]
let ``A def's contexts are joined with &, or with + as an earlier spec had it`` () =
    let contextsOf source =
        match parseModule source with
        | Result.Ok [ Kind (Def (ValueDef (_, _, Some { Kind = TypeWithContext (contexts, _) }, _))) ] -> contexts |> List.map _.Kind
        | other -> failwith $"Unexpected parse: %A{other}"
    let a = TypeExpr.synthetic (TypeVar "a")
    let expected = [ TypeApply ("Eq", [ a ]); TypeApply ("Hash", [ a ]); TypeName "Console" ]
    Assert.Equal<TypeExprKind list>(expected, contextsOf "def both: [Eq(a) & Hash(a) & Console] a, a -> bool = { x, y -> x == y }")
    Assert.Equal<TypeExprKind list>(expected, contextsOf "def both: [Eq(a) + Hash(a) + Console] a, a -> bool = { x, y -> x == y }")

/// The statements of `def main = { ... }` in `source`, its line endings made `\n`.
let private mainStatements (source: string) =
    match parseModuleFile "t.nyx" (source.ReplaceLineEndings "\n") with
    | Result.Ok [ Kind (Def (ValueDef (_, "main", None, Kind (Lambda ([], Kind (Block statements), _))))) ] ->
        statements |> List.map (fun (statement: Statement) -> statement.Kind)
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``use ... in takes a block indented on the lines after in, and the block ends where the indent does (#673)`` () =
    let source =
        """def main = {
  def total = use counter in
    def a = counted(3)
    a + 1
  total
}"""
    match mainStatements source with
    | [ DefStatement (_, "total", None, Kind (UseIn (Kind (IdentifierExpr "counter"), (Kind (Block body) as block))))
        ExprStatement (Kind (IdentifierExpr "total")) ] ->
        match body |> List.map (fun (statement: Statement) -> statement.Kind) with
        | [ DefStatement (_, "a", None, _); ExprStatement (Kind (BinaryOp ("+", _, _))) ] -> ()
        | other -> failwith $"Unexpected block: %A{other}"
        let text = (source.ReplaceLineEndings "\n").Substring(block.Span.Start, block.Span.End - block.Span.Start)
        Assert.Equal("""def a = counted(3)
    a + 1""".ReplaceLineEndings "\n", text)
    | other -> failwith $"Unexpected statements: %A{other}"

[<Fact>]
let ``use ... in followed by one expression on the next line takes that expression, not a block`` () =
    let source =
        """def main = {
  use counter in
    counted(3)
}"""
    match mainStatements source with
    | [ ExprStatement (Kind (UseIn (_, Kind (FunctionCall ("counted", _, _))))) ] -> ()
    | other -> failwith $"Unexpected statements: %A{other}"

[<Fact>]
let ``use ... in with braces after in takes a lambda, not a block`` () =
    let source =
        """def main = {
  use counter in { counted(3) }
}"""
    match mainStatements source with
    | [ ExprStatement (Kind (UseIn (_, Kind (Lambda ([], _, _))))) ] -> ()
    | other -> failwith $"Unexpected statements: %A{other}"

[<Fact>]
let ``A block after in must be indented more than the line use is on`` () =
    let source =
        """def main = {
  def total = use counter in
  def a = counted(3)
  a
}"""
    match parseModuleFile "t.nyx" (source.ReplaceLineEndings "\n") with
    | Result.Error _ -> ()
    | Result.Ok parsed -> failwith $"Expected a parse error, got %A{parsed}"