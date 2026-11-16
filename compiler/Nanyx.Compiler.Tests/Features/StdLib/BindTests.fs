module Nanyx.Compiler.Tests.Features.StdLib.Bind

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// #183: two steps that fail with different tags.
let private steps =
    normalized """def parseDigit: string -> #some(int) | #invalidInt = {
  | "1" -> #some(1)
  | _ -> #invalidInt
}
def find: int -> #some(string) | #notFound = {
  | 1 -> #some("1")
  | _ -> #notFound
}
"""

let private tagsOf (ty: Ty) =
    match Unifier.unalias ty with
    | TyUnion items -> items |> List.map Unifier.tyToString |> List.sort
    | other -> [ Unifier.tyToString other ]

[<Fact>]
let ``bindSome keeps the other tags of both steps, piped or called`` () =
    let typed =
        compileClean (
            steps +
            withFinalNewline """def piped = find(1) \bindSome(parseDigit)
def called = bindSome(find(1), parseDigit)
def withLambda = bindSome(find(1), { s -> parseDigit(s) })""")
    let expected = [ "#invalidInt"; "#notFound"; "#some(int)" ]
    Assert.Equal<string list>(expected, tagsOf typed.Types.["piped"])
    Assert.Equal<string list>(expected, tagsOf typed.Types.["called"])
    Assert.Equal<string list>(expected, tagsOf typed.Types.["withLambda"])

[<Fact>]
let ``The result of bindSome is matched on each step's tags with no catch-all`` () =
    let arms =
        normalized """    | #some(n) -> "some"
    | #notFound -> "not found"
    | #invalidInt -> "invalid"
}
"""
    compileClean (steps + "def piped: int -> string = { n ->\n  match find(n) \\bindSome(parseDigit)\n" + arms) |> ignore
    compileClean (steps + "def called: int -> string = { n ->\n  match bindSome(find(n), parseDigit)\n" + arms) |> ignore
    compileClean (steps + "def viaLocal: int -> string = { n ->\n  def b = bindSome(find(n), parseDigit)\n  match b\n" + arms) |> ignore

[<Fact>]
let ``A match on the result of bindSome that leaves out a step's tag says which`` () =
    let source =
        steps +
        normalized """def partial: int -> string = { n ->
  def b = bindSome(find(n), parseDigit)
  match b
    | #some(n) -> "some"
    | #notFound -> "not found"
}
"""
    let diagnostic = Assert.Single((Compiler.compile source).Diagnostics)
    Assert.Equal(DiagnosticCodes.NonExhaustiveMatch.Code, diagnostic.Code)
    Assert.Equal("Non-exhaustive match, missing tags: invalidInt", diagnostic.Message)

[<Fact>]
let ``Result.bind of two steps with one error type keeps it`` () =
    let typed =
        compileClean (
            withFinalNewline """def read: string -> Result(int, string) = { s -> int.fromString(s) \someToResult("not a number") }
def half: int -> Result(int, string) = { n -> if n % 2 == 0 then #some(n / 2) else #error("odd") }
def piped = read("42") \Result.bind(half)
def called = Result.bind(read("42"), half)""")
    Assert.Equal("Result(int, string)", Unifier.tyToString typed.Types.["piped"])
    Assert.Equal("Result(int, string)", Unifier.tyToString typed.Types.["called"])

// #679: a variable given two closed tag unions takes their join.
let private failingSteps =
    normalized """def lookup: int -> Result(string, #notFound) = {
  | 1 -> #some("1")
  | _ -> #error(#notFound)
}
def digit: string -> Result(int, #invalidInt) = {
  | "1" -> #some(1)
  | _ -> #error(#invalidInt)
}
"""

[<Fact>]
let ``Result.bind of steps that fail with different tags fails with both, piped or called`` () =
    let typed =
        compileClean (
            failingSteps
            + withFinalNewline """def piped = lookup(1) \Result.bind(digit)
def called = Result.bind(lookup(1), digit)
def tried = proc {
  def s = try lookup(1)
  def n = try digit(s)
  return n
}""")
    let errorsOf (ty: Ty) =
        match Unifier.unalias ty with
        | TyUnion items -> items |> List.tryPick (function TyTag("error", Some payload) -> Some (tagsOf payload) | _ -> None)
        | _ -> None
    for name in [ "piped"; "called"; "tried" ] do
        Assert.Equal(Some [ "#invalidInt"; "#notFound" ], errorsOf typed.Types.[name])

[<Fact>]
let ``A match on Result.bind of different errors handles each, and leaving one out says which`` () =
    let source (arms: string) =
        failingSteps
        + normalized $"""def shown: int -> string = {{ n ->
  match lookup(n) \Result.bind(digit)
    | #some(d) -> "some {{d}}"
    | #error(#notFound) -> "not found"
{arms}}}
"""
    compileClean (source "    | #error(#invalidInt) -> \"invalid\"\n") |> ignore
    let diagnostic = Assert.Single((Compiler.compile (source "")).Diagnostics)
    Assert.Equal(DiagnosticCodes.NonExhaustiveMatch.Code, diagnostic.Code)

[<Fact>]
let ``Result.bind of steps whose errors are not tags still needs them to be the same type`` () =
    let source =
        normalized """def read: string -> Result(int, string) = { s -> int.fromString(s) \someToResult("not a number") }
def half: int -> Result(int, int) = { n -> if n % 2 == 0 then #some(n / 2) else #error(n) }
def piped = read("42") \Result.bind(half)
"""
    let code, _ = singleError source
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
