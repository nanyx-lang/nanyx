module Nanyx.Compiler.Tests.Features.UnderscoreContextNames

open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

// Contexts, their members and the types they are instances at can have `_` in their names (#873).

[<Fact>]
let ``A parameterised context whose name, members and type argument have _ in them type checks`` () =
    let source = """context To_Text(a) = (to_text: a -> string, text_tag: a -> string)
type My_Point = (x: int, y: int)

def describe: [To_Text(a)] a -> string = { value -> To_Text(a).to_text(value) ++ "/" ++ text_tag(value) }

def My_Point.to_text: My_Point -> string = { p -> "p" }
def My_Point.text_tag: My_Point -> string = { p -> "t" }

def synthesized = describe(My_Point(x = 1, y = 2))
def show_point: To_Text(My_Point) = (to_text = { p -> "q" }, text_tag = { p -> "u" })
def loaded = {
  use show_point
  describe(My_Point(x = 1, y = 2))
}
"""
    let typed = compileClean (source.ReplaceLineEndings "\n")
    match ownContextInstances typed |> Map.toList |> List.sortBy fst |> List.map snd |> List.filter ((<>) [ Some InstanceBuiltIn ]) with
    | [ [ Some (InstanceSynthesized members) ]
        [ Some (InstanceInScope (Kind (TypeApply ("To_Text", [ Kind (TypeName "My_Point") ])))) ] ] ->
        Assert.Equal<(string * string) list>([ "text_tag", "My_Point.text_tag"; "to_text", "My_Point.to_text" ], List.sort members)
    | other -> failwith $"Unexpected instances: %A{other}"

[<Fact>]
let ``A function can require two contexts whose names and members differ only in where a _ falls`` () =
    let source = """context A(x) = (a_c: x -> string)
context A_b(x) = (c: x -> string)

def both: [A_b(a) & A(b)] (a, b) -> string = { v, w -> A_b(a).c(v) ++ " " ++ A(b).a_c(w) }

def a_int: A(int) = (a_c = { n -> "A" })
def ab_int: A_b(int) = (c = { n -> "A_b" })
def main = {
  use a_int
  use ab_int
  both(1, 2)
}
"""
    compileClean (source.ReplaceLineEndings "\n") |> ignore
