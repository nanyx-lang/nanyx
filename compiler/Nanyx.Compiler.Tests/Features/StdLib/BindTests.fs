module Nanyx.Compiler.Tests.Features.StdLib.Bind

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// #183: two steps that fail with different tags.
let private steps =
    "def parseDigit: string -> #some(int) | #invalidInt = {\n" +
    "  | \"1\" -> #some(1)\n" +
    "  | _ -> #invalidInt\n" +
    "}\n" +
    "def find: int -> #some(string) | #notFound = {\n" +
    "  | 1 -> #some(\"1\")\n" +
    "  | _ -> #notFound\n" +
    "}\n"

let private tagsOf (ty: Ty) =
    match Unifier.unalias ty with
    | TyUnion items -> items |> List.map Unifier.tyToString |> List.sort
    | other -> [ Unifier.tyToString other ]

[<Fact>]
let ``bindOption keeps the other tags of both steps, piped or called`` () =
    let typed =
        compileClean (
            steps +
            "def piped = find(1) \\bindOption(parseDigit)\n" +
            "def called = bindOption(find(1), parseDigit)\n" +
            "def withLambda = bindOption(find(1), { s -> parseDigit(s) })\n")
    let expected = [ "#invalidInt"; "#notFound"; "#some(int)" ]
    Assert.Equal<string list>(expected, tagsOf typed.Types.["piped"])
    Assert.Equal<string list>(expected, tagsOf typed.Types.["called"])
    Assert.Equal<string list>(expected, tagsOf typed.Types.["withLambda"])

[<Fact>]
let ``The result of bindOption is matched on each step's tags with no catch-all`` () =
    let arms =
        "    | #some(n) -> \"some\"\n" +
        "    | #notFound -> \"not found\"\n" +
        "    | #invalidInt -> \"invalid\"\n" +
        "}\n"
    compileClean (steps + "def piped: int -> string = { n ->\n  match find(n) \\bindOption(parseDigit)\n" + arms) |> ignore
    compileClean (steps + "def called: int -> string = { n ->\n  match bindOption(find(n), parseDigit)\n" + arms) |> ignore
    compileClean (steps + "def viaLocal: int -> string = { n ->\n  def b = bindOption(find(n), parseDigit)\n  match b\n" + arms) |> ignore

[<Fact>]
let ``A match on the result of bindOption that leaves out a step's tag says which`` () =
    let source =
        steps +
        "def partial: int -> string = { n ->\n" +
        "  def b = bindOption(find(n), parseDigit)\n" +
        "  match b\n" +
        "    | #some(n) -> \"some\"\n" +
        "    | #notFound -> \"not found\"\n" +
        "}\n"
    let diagnostic = Assert.Single((Compiler.compile source).Diagnostics)
    Assert.Equal(DiagnosticCodes.NonExhaustiveMatch.Code, diagnostic.Code)
    Assert.Equal("Non-exhaustive match, missing tags: invalidInt", diagnostic.Message)

[<Fact>]
let ``Result.bind of two steps with one error type keeps it`` () =
    let typed =
        compileClean (
            "def read: string -> Result(int, string) = { s -> Int.fromString(s) \\toResultOption(\"not a number\") }\n" +
            "def half: int -> Result(int, string) = { n -> if n % 2 == 0 then #some(n / 2) else #error(\"odd\") }\n" +
            "def piped = read(\"42\") \\Result.bind(half)\n" +
            "def called = Result.bind(read(\"42\"), half)\n")
    Assert.Equal("Result(int, string)", Unifier.tyToString typed.Types.["piped"])
    Assert.Equal("Result(int, string)", Unifier.tyToString typed.Types.["called"])
