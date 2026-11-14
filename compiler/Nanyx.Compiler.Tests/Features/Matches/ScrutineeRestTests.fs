module Nanyx.Compiler.Tests.Features.ScrutineeRests

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

let private find =
    normalized """def find: int -> #some(string) | #notFound = {
  | 1 -> #some("1")
  | _ -> #notFound
}
"""

[<Fact>]
let ``A match on a call that widened its argument's union needs no catch-all`` () =
    // #183: the call's `#some(b) | r` is `#some(string) | #notFound | r'` here, with `r'` the room widening left.
    // Nothing can add a tag to it, so the arms are checked against the tags written.
    compileClean (
        find +
        withFinalNewline """def describe: int -> string = { n ->
  match find(n) \mapSome({ s -> s ++ "!" })
    | #some(s) -> s
    | #notFound -> "not found"
}""")
    |> ignore

[<Fact>]
let ``A match on a call that widened its argument's union is checked against the tags it has`` () =
    let source =
        find +
        normalized """def describe: int -> string = { n ->
  match find(n) \mapSome({ s -> s ++ "!" })
    | #some(s) -> s
}
"""
    let diagnostic = Assert.Single((Compiler.compile source).Diagnostics)
    Assert.Equal(DiagnosticCodes.NonExhaustiveMatch.Code, diagnostic.Code)
    Assert.Equal("Non-exhaustive match, missing tags: notFound", diagnostic.Message)

[<Fact>]
let ``A match on a union whose rest an annotation names needs a catch-all`` () =
    let source =
        normalized """def value: (#some(int) | r) -> int = { x ->
  match x
    | #some(n) -> n
}
"""
    let diagnostic = Assert.Single((Compiler.compile source).Diagnostics)
    Assert.Equal(DiagnosticCodes.MissingCatchAllArm.Code, diagnostic.Code)

[<Fact>]
let ``An and-pattern of catch-alls is the catch-all a union with a rest needs`` () =
    // The rest check took only `_`, `else` and a name as a catch-all, unlike the exhaustiveness check (#450).
    let source =
        """
def value: (#some(int) | r) -> int = { x ->
  match x
    | #some(n) -> n
    | _ & _ -> 0
}
"""
    compileClean (source.ReplaceLineEndings "\n") |> ignore

[<Fact>]
let ``A match on a call whose rest is its caller's parameter's needs a catch-all`` () =
    // `keep(x)` has the rest of `x`, which whoever calls `describe` can give more tags.
    let source =
        normalized """def keep: (#some(a) | r) -> (#some(a) | r) = { x -> x }
def describe = { x ->
  match keep(x)
    | #some(n) -> "some"
    | #none -> "none"
}
"""
    let diagnostic = Assert.Single((Compiler.compile source).Diagnostics)
    Assert.Equal(DiagnosticCodes.MissingCatchAllArm.Code, diagnostic.Code)
