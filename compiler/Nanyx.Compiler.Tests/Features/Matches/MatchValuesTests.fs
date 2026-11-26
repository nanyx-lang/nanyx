module Nanyx.Compiler.Tests.Features.MatchValues

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// `match a, b` is a match on the tuple `(a, b)` (#2823): an arm has a pattern for each value, or one for all of them.

[<Fact>]
let ``A lone _ arm in a match on several values matches whatever they all are`` () =
    let source = normalized """def s: string = match 1, 2
  | 1, 2 -> "matched"
  | _ -> "not matched"
"""
    compileClean source |> ignore

[<Fact>]
let ``A lone name in a match on several values is their tuple`` () =
    let source = normalized """def whole = match 1, "a"
  | 0, _ -> (0, "zero")
  | t -> t
"""
    let typed = compileClean source
    Assert.Equal("(int, string)", Unifier.tyToString typed.Types.["whole"])

[<Fact>]
let ``A lone name for all the values is a catch-all`` () =
    let source = normalized """def describe: int, int -> string = { x, y ->
  match x, y
    | 0, 0 -> "origin"
    | t -> "elsewhere"
}
"""
    compileClean source |> ignore

[<Fact>]
let ``A match on several values whose arms miss a case is still not exhaustive`` () =
    let source =
        normalized """def describe: bool, bool -> string = { x, y ->
  match x, y
    | true, true -> "both"
    | (false, _) & t -> "not the first"
}
"""
    let diagnostic = Assert.Single((Compiler.compile source).Diagnostics)
    Assert.Contains(diagnostic.Code, [ DiagnosticCodes.NonExhaustiveMatch.Code; DiagnosticCodes.MissingCatchAllArm.Code ])
