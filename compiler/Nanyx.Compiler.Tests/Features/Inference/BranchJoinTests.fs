module Nanyx.Compiler.Tests.Features.BranchJoin

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The type of `r` in `source`, which must compile clean.
let private typeOfR (source: string) =
    let typed = compileClean (source.ReplaceLineEndings "\n")
    Unifier.tyToString typed.Types.["r"]

[<Fact>]
let ``Branches that each end in a union with a rest of its own join into one union`` () =
    let source = """
def f = { g, h, n ->
  match n
  | 0 ->
    match n
    | 1 -> #a
    | _ -> g(n)
  | _ ->
    match n
    | 1 -> #b
    | _ -> h(n)
}

def r = f({ n -> #c }, { n -> #d }, 3)
"""
    Assert.Equal("#a | #b | #c | #d", typeOfR source)

[<Fact>]
let ``A call whose type an earlier branch already joined into a union joins as that union`` () =
    // The second `g(n)` is of the type the first join made a union, `#a | r`: it is joined with `#b` as a union, not
    // made equal to `#b`, which would give `g` the type `int -> #b`.
    let source = """
def f = { g, n ->
  match n
  | 0 ->
    match n
    | 1 -> #a
    | _ -> g(n)
  | _ ->
    match n
    | 1 -> #b
    | _ -> g(n)
}

def r = f({ n -> #c }, 3)
"""
    Assert.Equal("#a | #b | #c", typeOfR source)
