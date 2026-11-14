module Nanyx.Compiler.Tests.Features.GenericContextMembers

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A context member's own type variables, those the context's parameters do not give it, are instantiated afresh at
// each call, as a generic top-level function's are. The context's own parameters stay fixed by the instance (#1304).

let private proc = """context Proc = (
  bind: [c] ((#some(a) | r), ([c] a -> #some(b) | r)) -> #some(b) | r
  ret: a -> #some(a) | r
)

def name: int -> #some(string) | #missing = { i -> #some("item {i}") }
def size: string -> #some(int) | #missing = { s -> if s == "" then #missing else #some(1) }
"""

[<Fact>]
let ``A context member is called at two types in one function`` () =
    let source = proc + """
def both: [Proc] () -> (x: #some(int) | #missing, y: #some(string) | #missing) = { (x = ret(1), y = ret("s")) }
"""
    let typed = compileClean (source.ReplaceLineEndings "\n")
    Assert.Equal("() -> (x: #some(int) | #missing, y: #some(string) | #missing)", Unifier.tyToString typed.Types.["both"])

[<Fact>]
let ``A context member is called at a different type in a nested lambda`` () =
    let source = proc + """
def lookup: [Proc] int -> #some(int) | #missing = { i ->
  bind(name(i), { s -> bind(size(s), { n -> ret(n) }) })
}
"""
    let typed = compileClean (source.ReplaceLineEndings "\n")
    Assert.Equal("int -> #some(int) | #missing", Unifier.tyToString typed.Types.["lookup"])

[<Fact>]
let ``A member of an instance a use loads is called at two types`` () =
    let source = proc + """
def both = {
  use Proc(
    bind = {
      | #some(value), continue -> continue(value)
      | failure, _ -> failure
    }
    ret = { value -> #some(value) }
  )
  (x = ret(1), y = ret("s"))
}
"""
    compileClean (source.ReplaceLineEndings "\n") |> ignore

[<Fact>]
let ``A member's own type variable is instantiated at each call while the context's parameter stays fixed`` () =
    let source = """context Pair(a) = (pair: b -> (first: a, second: b))

def both: [Pair(int)] () -> (first: int, second: string) = {
  def p = pair(true)
  def q = pair("x")
  (first = p.first + q.first, second = q.second)
}
"""
    compileClean (source.ReplaceLineEndings "\n") |> ignore

[<Fact>]
let ``A context's parameter is not instantiated afresh at a call of its member`` () =
    let source = """context Pair(a) = (pair: b -> (first: a, second: b))

def bad: [Pair(int)] () -> string = { pair("x").first }
"""
    let source = source.ReplaceLineEndings "\n"
    match (Compiler.compile source).Diagnostics with
    | [ diagnostic ] ->
        Assert.Equal(DiagnosticCodes.TypeMismatch.Code, diagnostic.Code)
        Assert.Equal("Type mismatch: int vs string.", diagnostic.Message)
    | other -> failwith $"Expected one diagnostic: %A{other}"
