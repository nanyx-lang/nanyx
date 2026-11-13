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
let ``use C(T) = expr does not parse, and the error says to write the call form where the context is named`` () =
    for line, head in [ "  use Show(int) = (show = { n -> \"n\" })", "Show(int)"; "  use Console = (println = log)", "Console" ] do
        let source = $"def main = {{\n{line}\n  1\n}}"
        match parseModuleFileWithNames "t.nyx" source with
        | Result.Error err ->
            // FParsec wraps a long message onto several lines.
            let message = err.Message.Replace("\r\n", " ").Replace("\n", " ")
            Assert.Contains($"`use {head} = ...` is not a way to load an instance: write `use {head}(...)`,", message)
            Assert.Contains($"or define the instance with `def name: {head} = (...)` and load it with `use name`.", message)
            Assert.Equal(source.IndexOf head, err.Span.Start)
        | Result.Ok _ -> failwith $"Expected '{line.Trim()}' not to parse"

[<Fact>]
let ``A context's or a type's parameter is a single lowercase letter, and a longer name is an error where it is written`` () =
    match parseModule "context Store(a, b) = (put: (a, b) -> a)\ntype Box(t) = (value: t)" with
    | Result.Ok [ Kind (Def (TypeDef ("Store", _, [ "a", None; "b", None ], _))); Kind (Def (TypeDef ("Box", _, [ "t", None ], _))) ] -> ()
    | other -> failwith $"Unexpected parse: %A{other}"
    let longer = "longer lowercase names are reserved for associated types"
    let cases =
        [ "context Store(item) = (put: item -> item)", "item", Some longer
          "context Pair(a, bb) = (first: a, second: bb)", "bb", Some longer
          "type Box(item) = (value: item)", "item", Some longer
          "type Box(T) = (value: T)", "T", None
          "def main = {\n  type Box(item) = (value: item)\n  1\n}", "item", Some longer ]
    for source, name, reason in cases do
        match parseModuleFileWithNames "t.nyx" source with
        | Result.Error err ->
            // FParsec wraps a long message onto several lines.
            let message = err.Message.Replace("\r\n", " ").Replace("\n", " ")
            Assert.Contains($"A type parameter is a single lowercase letter, such as 'a', so '{name}' cannot be one", message)
            match reason with
            | Some reason -> Assert.Contains(reason, message)
            | None -> Assert.DoesNotContain(longer, message)
            Assert.Equal(source.IndexOf $"{name})", err.Span.Start)
        | Result.Ok _ -> failwith $"Expected '{source}' not to parse"

[<Fact>]
let ``An operator is required by the member it reads, among a def's contexts`` () =
    let contextsOf source =
        match parseModule source with
        | Result.Ok [ Kind (Def (ValueDef (_, _, Some { Kind = TypeWithContext (contexts, _) }, _))) ]
        | Result.Ok [ Kind (Def (ValueDef (_, _, Some { Kind = TypeContext contexts }, _))) ] -> contexts |> List.map _.Kind
        | other -> failwith $"Unexpected parse: %A{other}"
    let a = TypeExpr.synthetic (TypeVar "a")
    let binary result = TypeExpr.synthetic (TypeFunc (TypeExpr.synthetic (TypeTuple [ a; a ]), result))
    let required name memberType = TypeRecord [ TypeField (name, false, Some memberType, None) ]
    Assert.Equal<TypeExprKind list>([ required "+" (binary a) ], contextsOf "def add: [`+`: a, a -> a] = { + }")
    Assert.Equal<TypeExprKind list>([ required "+" (binary a) ], contextsOf "def add: [`+`: (a, a) -> a] a, a -> a = { + }")
    Assert.Equal<TypeExprKind list>(
        [ required "+" (binary a); TypeApply ("Eq", [ a ]); required "==" (binary (TypeExpr.synthetic (TypeName "bool"))); TypeName "Console" ],
        contextsOf "def f: [(`+`: a, a -> a) & Eq(a) & (`==`: a, a -> bool) & Console] a -> a = { x -> x }")
    // A member's type runs to the end of the set, so a `&` after it is an intersection in its type.
    let b = TypeExpr.synthetic (TypeVar "b")
    Assert.Equal<TypeExprKind list>(
        [ required "-" (TypeExpr.synthetic (TypeFunc (a, TypeExpr.synthetic (TypeIntersection [ a; b ])))) ],
        contextsOf "def f: [`-`: a -> a & b] a -> a = { x -> x }")
    // The bracketed member spans its brackets, which the official linter's fixes rely on.
    match parseModule "def f: [(`+`: a, a -> a) & Eq(a)] a -> a = { x -> x }" with
    | Result.Ok [ Kind (Def (ValueDef (_, _, Some { Kind = TypeWithContext (requirement :: _, _) }, _))) ] ->
        Assert.Equal(8, requirement.Span.Start)
        Assert.Equal(24, requirement.Span.End)
    | other -> failwith $"Unexpected parse: %A{other}"

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
        | [ DefStatement (_, "a", None, _); ExprStatement (Kind (BinaryOp ("+", _, _, _))) ] -> ()
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

/// The context and the member names of each instance `use C(T)(members)` writes out in `main`'s statements.
let private instancesUsedIn (source: string) =
    let instance (value: Expression) =
        match value.Kind with
        | UseInstanceSyntax.Instance (context, Kind (RecordExpr fields)) ->
            Some (context.Kind, fields |> List.map (function NamedField (name, _) -> name | PositionalField _ -> "_"))
        | _ -> None
    match parseModule (source.ReplaceLineEndings "\n") with
    | Result.Ok [ Kind (Def (ValueDef (_, "main", None, Kind (Lambda ([], Kind (Block statements), _))))) ] ->
        statements
        |> List.choose (fun statement ->
            match statement.Kind with
            | UseStatement value -> instance value
            | ExprStatement (Kind (UseIn (value, _))) -> instance value
            | _ -> None)
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``use C(T)(members) writes out an instance of a parameterised context`` () =
    let source = """
def main = {
  use Add(Point)(
    zero = origin
    `+` = plus
  )
  1
}"""
    Assert.Equal<(TypeExprKind * string list) list>(
        [ TypeApply ("Add", [ TypeExpr.synthetic (TypeName "Point") ]), [ "zero"; "+" ] ],
        instancesUsedIn source)

[<Fact>]
let ``use C(T)(members) in body writes out an instance for the body`` () =
    let source = """
def main = {
  use ToString(Point)(toString = show) in describe(p)
}"""
    Assert.Equal<(TypeExprKind * string list) list>(
        [ TypeApply ("ToString", [ TypeExpr.synthetic (TypeName "Point") ]), [ "toString" ] ],
        instancesUsedIn source)

[<Fact>]
let ``use of a call is not an instance written out`` () =
    Assert.Empty(instancesUsedIn "def main = {\n  use Console(println = log)\n  use makeAdd(int)(zero)\n  1\n}")
