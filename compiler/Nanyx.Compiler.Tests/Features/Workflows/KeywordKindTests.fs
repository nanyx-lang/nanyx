module Nanyx.Compiler.Tests.Features.WorkflowKeywordKinds

open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

/// The value of the def `name` in `source`, desugared.
let private desugaredDef (name: string) (source: string) : Expression =
    match parseModule (normalized source) with
    | Result.Error err -> failwith $"Parse error: {err}"
    | Result.Ok ast ->
        match WorkflowDesugaring.desugar ast |> List.tryPick (function Kind (Def (ValueDef(_, n, _, body))) when n = name -> Some body | _ -> None) with
        | Some body -> body
        | None -> failwith $"No def '{name}'"

[<Fact>]
let ``A #delay keyword is a call of its member with a lambda of the whole expression after it, a pipe included`` () =
    let source =
        """context Scope = (
  @customKeywordType(#delay)
  start: (() -> a) -> a
)

def run = {
  def a = start! fetch(1) \orCrash("failed")
  a
}
"""
    match (desugaredDef "run" source).Kind with
    | Lambda([], Kind (Block [ Kind (DefStatement(_, "a", _, call)); _ ]), _) ->
        match call.Kind with
        | FunctionCall("start", _, [ Kind (Lambda([], Kind (Pipe(Kind (FunctionCall("fetch", _, _)), "orCrash", _, _)), false)) ]) -> ()
        | other -> Assert.Fail $"Unexpected call: %A{other}"
    | other -> Assert.Fail $"Unexpected body: %A{other}"

[<Fact>]
let ``A #delay keyword in a builder's block leaves the rest of the block where it is`` () =
    let source =
        """context Scope = (
  @customKeywordType(#delay)
  start: (() -> a) -> a
)

def run = proc {
  def a = start! fetch(1)
  def b = try fetch(2)
  return b
}
"""
    match (desugaredDef "run" source).Kind with
    | FunctionCall("proc", _, [ Kind (Lambda([], Kind (Block statements), _)) ]) ->
        match statements with
        | [ Kind (DefStatement(_, "a", _, Kind (FunctionCall("start", _, [ Kind (Lambda([], _, _)) ])))); Kind (ExprStatement (Kind (FunctionCall("try", _, [ _; _ ])))) ] -> ()
        | other -> Assert.Fail $"Unexpected statements: %A{other}"
    | other -> Assert.Fail $"Unexpected body: %A{other}"

[<Fact>]
let ``A #bindF keyword passes a lambda of the expression after it and the rest of the block`` () =
    let source =
        """context Cleanup = (
  @customKeywordType(#bindF)
  defer: ((() -> ()), (() -> int)) -> int
)

def run = cleaning {
  defer! println("cleaned up")
  return 1
}
"""
    match (desugaredDef "run" source).Kind with
    | FunctionCall("cleaning", _, [ Kind (Lambda([], Kind (Block [ Kind (ExprStatement chain) ]), _)) ]) ->
        match chain.Kind with
        | FunctionCall("defer", _, [ Kind (Lambda([], Kind (FunctionCall("println", _, _)), false)); Kind (Lambda([ ("_", None) ], Kind (FunctionCall("return", _, _)), _)) ]) -> ()
        | other -> Assert.Fail $"Unexpected chain: %A{other}"
    | other -> Assert.Fail $"Unexpected body: %A{other}"

[<Fact>]
let ``A #delay keyword in a lambda in a builder's block is not reported as one the block cannot follow`` () =
    let source =
        """context Scope = (
  @customKeywordType(#delay)
  start: [c] ([c] () -> int) -> int
)

def halve: int -> #some(int) | #odd = { n -> if n % 2 == 0 then #some(n / 2) else #odd }

def f: list(int) -> #some(list(int)) | #odd = { xs -> proc {
  use Scope(start = { thunk -> thunk() })
  def h = try halve(4)
  return xs \list.map { x -> start! x + h }
} }
"""
    let result = Compiler.compile (normalized source)
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
