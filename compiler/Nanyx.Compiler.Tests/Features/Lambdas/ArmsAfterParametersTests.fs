module Nanyx.Compiler.Tests.Features.Lambdas.ArmsAfterParameters

open System
open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A lambda that names its parameters and then gives arms, `{ m, f -> | ... }`, matches the arms against the
// parameters in order, as `{ | ... }` does (#985).

let private typeOf (source: string) (name: string) = Unifier.tyToString (compileClean source).Types.[name]

[<Fact>]
let ``Arms after parameters check to the same type as arms alone`` () =
    let source =
        """def `>>=` = { m, f ->
  | #some(v), f -> f(v)
  | other, _ -> other
}
def bind = {
  | #some(v), f -> f(v)
  | other, _ -> other
}
"""
            .ReplaceLineEndings "\n"
    // The type variables are numbered apart, so they are compared by where they first appear.
    let renamed (ty: string) =
        let names = Text.RegularExpressions.Regex.Matches(ty, @"\bt\d+\b") |> Seq.map _.Value |> Seq.distinct |> Seq.toList
        names |> List.indexed |> List.fold (fun (s: string) (i, n) -> Text.RegularExpressions.Regex.Replace(s, $@"\b{n}\b", $"v{i}")) ty
    Assert.Equal(renamed (typeOf source "bind"), renamed (typeOf source ">>="))

[<Fact>]
let ``The parameters are in scope in each arm`` () =
    let source =
        """def keep = { m, f ->
  | #some(v), _ -> f(v)
  | #none, _ -> m
}
def pick = { a, b, c -> | 0, _, _ -> b | _, _, _ -> c + a }
"""
            .ReplaceLineEndings "\n"
    Assert.Equal("int, int, int -> int", typeOf source "pick")

[<Fact>]
let ``An arm with the wrong number of patterns is reported at the arm`` () =
    let code, at = singleError "def f = { a, b -> | x -> x }\n"
    Assert.Equal(DiagnosticCodes.WrongPatternCount.Code, code)
    Assert.Equal("x", at)
