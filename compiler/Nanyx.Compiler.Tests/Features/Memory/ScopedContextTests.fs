module Nanyx.Compiler.Tests.Features.Memory.ScopedContexts

open Xunit
open Parser.Program
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A scoped context such as `$Memory` is of the scope of the call that supplies it, which a function type carries as
// the context's argument and a reader never sees (#1057).

/// The scope of the `$Memory` that `ty`'s function type requires, if it requires one.
let private scopeOf (ty: Ty) =
    match Unifier.unalias ty with
    | TyFunc(_, _, contexts) -> contexts |> List.tryPick (function FunctionContexts.ScopedContext("$Memory", scope) -> Some scope | _ -> None)
    | _ -> None

/// Each lambda of `source` whose type requires `$Memory`, in the order they are written: its type as a reader sees it,
/// and its scope.
let private lambdaScopes (source: string) =
    let typed = compileClean source
    typed.Module
    |> List.collect SyntaxTree.ofItem
    |> List.collect SyntaxTree.expressionsWithin
    |> List.choose (fun expr ->
        match expr.Kind, typed.ExprTypes |> Map.tryFind expr.Id with
        | Lambda _, Some ty -> scopeOf ty |> Option.map (fun scope -> Unifier.tyToString ty, scope)
        | _ -> None)

[<Fact>]
let ``The lambdas over the cells of one block require $Memory of one scope, which is not written`` () =
    let lambdas = lambdaScopes """
def tally = { words: list(string) ->
  memory {
    mut count = 0
    def note = { word: string -> count := count + 1 }
    def current = { count }
    for words | word -> note(word)
    current()
  }
}
"""
    Assert.Contains("[$Memory] string -> ()", lambdas |> List.map fst)
    Assert.Contains("[$Memory] () -> int", lambdas |> List.map fst)
    Assert.Equal(1, lambdas |> List.map snd |> List.distinct |> List.length)

[<Fact>]
let ``Each memory block has a scope of its own`` () =
    let lambdas = lambdaScopes """
def two = {
  def a = memory {
    mut x = 0
    def f = { x }
    f()
  }
  def b = memory {
    mut y = 1
    def g = { y }
    g()
  }
  a + b
}
"""
    Assert.Equal(2, lambdas |> List.map snd |> List.distinct |> List.length)

[<Fact>]
let ``The $Memory a signature writes twice is of one scope`` () =
    let typed = compileClean """
def both: ([$Memory] () -> int) -> ([$Memory] () -> int) -> int = { f -> { g -> 0 } }
"""
    let ty = typed.Types.["both"]
    Assert.Equal("([$Memory] () -> int) -> ([$Memory] () -> int) -> int", Unifier.tyToString ty)
    match Unifier.unalias ty with
    | TyFunc(first, TyFunc(second, _, _), _) ->
        let scopes = [ scopeOf first; scopeOf second ]
        Assert.True(scopes |> List.forall Option.isSome, $"%A{scopes}")
        Assert.Equal(1, scopes |> List.distinct |> List.length)
    | other -> Assert.Fail $"Expected a curried function, got %A{other}"
