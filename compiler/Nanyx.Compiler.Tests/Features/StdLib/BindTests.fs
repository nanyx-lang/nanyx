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
    Assert.Equal("Missing tags: invalidInt", diagnostic.Message)

// A bind written by hand over steps that fail with a `#failed` tag carrying why: the payloads of the two steps'
// failures meet in one type variable.
let private andThen =
    normalized """def andThen: [c] (#some(a) | #failed(e)), ([c] a -> #some(b) | #failed(e)) -> #some(b) | #failed(e) = {
  | #some(value), f -> f(value)
  | #failed(why), _ -> #failed(why)
}
"""

[<Fact>]
let ``A bind of two steps with one failure payload type keeps it`` () =
    let typed =
        compileClean (
            andThen
            + withFinalNewline """def read: string -> #some(int) | #failed(string) = { s -> int.fromString(s) except #invalidInt -> #failed("not a number") }
def half: int -> #some(int) | #failed(string) = { n -> if n % 2 == 0 then #some(n // 2) else #failed("odd") }
def piped = read("42") \andThen(half)
def called = andThen(read("42"), half)""")
    Assert.Equal("#some(int) | #failed(string)", Unifier.tyToString typed.Types.["piped"])
    Assert.Equal("#some(int) | #failed(string)", Unifier.tyToString typed.Types.["called"])

// #679: a variable given two closed tag unions takes their join.
let private failingSteps =
    andThen
    + normalized """def lookup: int -> #some(string) | #failed(#notFound) = {
  | 1 -> #some("1")
  | _ -> #failed(#notFound)
}
def digit: string -> #some(int) | #failed(#invalidInt) = {
  | "1" -> #some(1)
  | _ -> #failed(#invalidInt)
}
"""

[<Fact>]
let ``A bind of steps whose failures carry different tags fails with both, piped or called`` () =
    let typed =
        compileClean (
            failingSteps
            + withFinalNewline """def piped = lookup(1) \andThen(digit)
def called = andThen(lookup(1), digit)
def tried = proc {
  def s = try lookup(1)
  def n = try digit(s)
  return n
}""")
    let failuresOf (ty: Ty) =
        match Unifier.unalias ty with
        | TyUnion items -> items |> List.tryPick (function TyTag("failed", Some payload) -> Some (tagsOf payload) | _ -> None)
        | _ -> None
    for name in [ "piped"; "called"; "tried" ] do
        Assert.Equal(Some [ "#invalidInt"; "#notFound" ], failuresOf typed.Types.[name])

[<Fact>]
let ``A match on a bind of different failures handles each, and leaving one out says which`` () =
    let source (arms: string) =
        failingSteps
        + normalized $"""def shown: int -> string = {{ n ->
  match lookup(n) \andThen(digit)
    | #some(d) -> "some {{d}}"
    | #failed(#notFound) -> "not found"
{arms}}}
"""
    compileClean (source "    | #failed(#invalidInt) -> \"invalid\"\n") |> ignore
    let diagnostic = Assert.Single((Compiler.compile (source "")).Diagnostics)
    Assert.Equal(DiagnosticCodes.NonExhaustiveMatch.Code, diagnostic.Code)

[<Fact>]
let ``A bind of steps whose failure payloads are not tags still needs them to be the same type`` () =
    let source =
        andThen
        + normalized """def read: string -> #some(int) | #failed(string) = { s -> int.fromString(s) except #invalidInt -> #failed("not a number") }
def half: int -> #some(int) | #failed(int) = { n -> if n % 2 == 0 then #some(n // 2) else #failed(n) }
def piped = read("42") \andThen(half)
"""
    let code, _ = singleError source
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
