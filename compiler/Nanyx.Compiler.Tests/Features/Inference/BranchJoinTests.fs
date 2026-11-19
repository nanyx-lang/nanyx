module Nanyx.Compiler.Tests.Features.BranchJoin

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

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
    Assert.Equal("#a | #b | #c | #d", typeOf source "r")

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
    Assert.Equal("#a | #b | #c", typeOf source "r")

[<Fact>]
let ``Same-named tags with tuple payloads join the tags in the tuples' parts`` () =
    let source = """
def r = if true then #a(#x, 1) else #a(#y, 2)
"""
    Assert.Equal("#a(#x | #y, int)", typeOf source "r")

[<Fact>]
let ``Same-named tags with record payloads join the tags in the records' fields`` () =
    let source = """
def r = if true then #a(kind = #x, n = 1) else #a(kind = #y, n = 2)
"""
    Assert.Equal("#a(kind: #x | #y, n: int)", typeOf source "r")

[<Fact>]
let ``Branches and list elements giving a tag with differing tuple payloads check against a union that allows both`` () =
    let source = """
type T = #a(#x | #y, int) | #b

def xs: list(T) = [ #a(#x, 1), #a(#y, 2) ]
def r: T = if true then #a(#x, 1) else #a(#y, 2)
"""
    compileClean (source.ReplaceLineEndings "\n") |> ignore

[<Fact>]
let ``A recursive alias's tags with differing tuple payloads join in a list`` () =
    let source = """
type Expr = #num(int) | #add(Expr, Expr)

def r: list(Expr) = [ #num(1), #add(#num(2), #num(3)), #add(#add(#num(4), #num(5)), #num(6)) ]
"""
    compileClean (source.ReplaceLineEndings "\n") |> ignore

[<Fact>]
let ``Same-named tags whose tuple payloads have parts of different types are still a mismatch`` () =
    let source = """
def r = if true then #a(#x, 1) else #a(#y, "one")
"""
    let code, _ = singleError (source.ReplaceLineEndings "\n")
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)

[<Fact>]
let ``A match of an arm's variable and a tag checks when a call with a context variable follows it`` () =
    // The arm `v` is settled as `J` by the constraints before the join. Constrained as its variable, it was made
    // `#null` when the module was solved afresh, which the call of `list.fold`, whose lambda's contexts are a
    // variable, made it be (#2070).
    let source = """
type J = #num(int) | #null | #obj(map(string, J))

def pick: map(string, J), list(string) -> int = { o, cases ->
  def payload =
    match map.get(o, "value")
      | #some(v) -> v
      | _ -> #null
  list.fold(cases, 0, { found, c -> found })
}
"""
    compileClean (source.ReplaceLineEndings "\n") |> ignore
