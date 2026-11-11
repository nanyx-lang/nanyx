module Nanyx.Compiler.Tests.Features.AppendContext

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``A def that does not write its type and joins values of a type variable requires Append of it`` () =
    let typed = compileClean "def join = { x, y -> x ++ y }\n"
    match typed.InferredContextTypes |> Map.values |> List.ofSeq with
    | [ [ TyApply(context, [ TyVar _ ]) ] ] -> Assert.Equal("Append", context)
    | other -> failwith $"Expected one inferred Append, got %A{other}"

[<Fact>]
let ``A def that requires Append is called with strings and lists`` () =
    let source =
        """
def concat: [Append(a)] a, a -> a = { x, y -> x ++ y }
def join = { x, y -> x ++ y }
def s = concat("ab", "cd")
def xs = concat([1], [2, 3])
def t = join("x", "y")
def ys = join([1.5], [2.5])
"""
    let typed = compileClean source
    Assert.Equal<string list>(
        [ "string"; "list(int)"; "string"; "list(float)" ],
        [ "s"; "xs"; "t"; "ys" ] |> List.map (fun name -> Unifier.tyToString typed.Types.[name]))

[<Fact>]
let ``++ on strings and lists requires nothing`` () =
    let source =
        """
def greet = { name -> "Hello, " ++ name }
def more = { xs -> xs ++ [1] }
"""
    let typed = compileClean source
    Assert.True(typed.InferredContextTypes.IsEmpty)
    Assert.Equal("string -> string", Unifier.tyToString typed.Types.["greet"])
    Assert.Equal("list(int) -> list(int)", Unifier.tyToString typed.Types.["more"])

[<Fact>]
let ``A def whose type does not require Append cannot join values of its type variable`` () =
    let message, at = missingContext "def concat: a, a -> a = { x, y -> x ++ y }\n"
    Assert.Equal("'concat' joins values of type 'a' with ++, so its type must require Append(a): add [Append(a)] to the type of 'concat'", message)
    Assert.Equal("x ++ y", at)

[<Fact>]
let ``A call that requires Append of a type with no ++ is an error at the call`` () =
    let source =
        """
def concat: [Append(a)] a, a -> a = { x, y -> x ++ y }
def n = concat(1, 2)
"""
    let message, at = missingContext (source.ReplaceLineEndings "\n")
    Assert.Equal("concat", at)
    Assert.Contains("'Append(int)'", message)

[<Fact>]
let ``++ on a type known to have no ++ is a type mismatch`` () =
    match (Compiler.compile "def n = 1 ++ 2\n").Diagnostics with
    | [ diagnostic ] ->
        Assert.Equal(DiagnosticCodes.TypeMismatch.Code, diagnostic.Code)
        Assert.Equal("'int' cannot be joined with ++: only strings, lists and types with an Append can", diagnostic.Message)
    | other -> failwith $"Expected one diagnostic, got %A{other}"

[<Fact>]
let ``A type that attaches ++ has an Append`` () =
    let source =
        """
type $Path = (parts: list(string))
def $Path.`++`: $Path, $Path -> $Path = { p, q -> $Path(parts = p.parts ++ q.parts) }
def join = { x, y -> x ++ y }
def p = join($Path(parts = ["a"]), $Path(parts = ["b"]))
"""
    let typed = compileClean source
    Assert.Equal("$Path", Unifier.tyToString typed.Types.["p"])
