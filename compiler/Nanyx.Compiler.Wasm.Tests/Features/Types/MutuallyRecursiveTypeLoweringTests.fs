module TranspilerWasmTests.Features.Types.MutuallyRecursiveTypeLowering

open Xunit
open Transpiler.Wasm
open TranspilerWasmTestHelpers

/// A group of two types that name each other, one of them a union of `cases` cases that each hold the other, and four
/// functions that match on it.
let private groupOfCases (cases: int) =
    let union = [ for i in 1..cases -> $"#case{i}(Pair)" ] |> String.concat " | "
    let walks =
        [ for f in 1..4 ->
            $"""
def walk{f}: Expr -> int = {{ e ->
  match e.kind
    | #leaf -> {f}
    | #case1(pair) -> walk{f}(pair.left) + walk{f}(pair.right)
    | _ -> 0
}}
""" ]
    ($"""
type Expr = (id: int, kind: #leaf | {union})

type Pair = (left: Expr, right: Expr)

def tree: Expr = (id = 1, kind = #case1((left = (id = 2, kind = #leaf), right = (id = 3, kind = #leaf))))
"""
     + String.concat "" walks
     + String.concat "\n" [ for f in 1..4 -> $"println(walk{f}(tree))" ]
     + "\n")
        .ReplaceLineEndings "\n"

[<Fact>]
let ``Lowering code over a group of types that name each other walks the group no more often for a larger group`` () =
    // The group's types were compared structurally, walking all of them, at each reference to one of them met in
    // lowering a type of the group, so the more references the group held, the more walks: a build of `nanyx.lint`, which
    // matches on the 30 types of `nanyx.analysis`'s syntax, took minutes (#3260). Now they are compared by reference,
    // and each copy of the group that type checking made is walked once.
    let walks cases = snd (CoreIrLowerTypes.countingGroupWalks (fun () -> transpileWat (groupOfCases cases)))
    Assert.Equal(walks 10, walks 40)
