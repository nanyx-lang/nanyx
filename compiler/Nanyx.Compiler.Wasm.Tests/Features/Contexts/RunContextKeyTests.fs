/// The backend knows each context member by a key of its own, whatever `_`s the names of the context, its type
/// arguments and the member have (#873).
module TranspilerWasmTests.Features.Contexts.ContextKeys

open Xunit
open Transpiler.Wasm
open TranspilerWasmTestHelpers

[<Fact>]
let ``A key's text is the context, its type arguments and the member, joined by _`` () =
    let key = InstanceMember(TypeKey("ToString", [ TypeKey("list", [ TypeKey("int", []) ]) ]), "toString")
    Assert.Equal("ToString_list_int_toString", ContextMemberKey.text key)
    Assert.Equal("println", ContextMemberKey.text (NamedMember "println"))

[<Fact>]
let ``Keys whose names differ only in where a _ falls have different text`` () =
    let contextWithUnderscore = InstanceMember(TypeKey("A_b", [ TypeKey("a", []) ]), "c")
    let memberWithUnderscore = InstanceMember(TypeKey("A", [ TypeKey("b", []) ]), "a_c")
    let named = NamedMember "A_b_a_c"
    let texts = [ contextWithUnderscore; memberWithUnderscore; named ] |> List.map ContextMemberKey.text
    Assert.Equal<string list>([ "A-5F-b_a_c"; "A_b_a-5F-c"; "A-5F-b-5F-a-5F-c" ], texts)

[<Fact>]
let ``Run - a parameterised context whose name and members have _ in them`` () =
    let source = """import nanyx/console

context To_Text(a) = (to_text: a -> string, text_tag: a -> string)
type My_Point = (x: int, y: int)

def describe: [To_Text(a)] a -> string = { value -> To_Text(a).to_text(value) ++ "/" ++ To_Text(a).text_tag(value) }
def twice: [To_Text(a)] a -> string = { v -> describe(v) ++ " " ++ describe(v) }

def My_Point.to_text: My_Point -> string = { p -> "p{p.x}" }
def My_Point.text_tag: My_Point -> string = { p -> "t" }

println(twice(My_Point(x = 1, y = 2)))
def show_point: To_Text(My_Point) = (to_text = { p -> "q" }, text_tag = { p -> "u" })
use show_point in println(twice(My_Point(x = 1, y = 2)))
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "p1/t p1/t\nq/u q/u\n"

[<Fact>]
let ``Run - two contexts whose keys were once the same text are both passed`` () =
    // `c` of `A_b(a)` and `a_c` of `A(b)` were both `A_b_a_c`, so `both` took two parameters of the same name.
    let source = """import nanyx/console

context A(x) = (a_c: x -> string)
context A_b(x) = (c: x -> string)

def both: [A_b(a) & A(b)] (a, b) -> string = { v, w -> A_b(a).c(v) ++ " " ++ A(b).a_c(w) }

def a_int: A(int) = (a_c = { n -> "A" })
def ab_int: A_b(int) = (c = { n -> "A_b" })
def main = {
  use a_int
  use ab_int
  both(1, 2)
}
println(main())
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "A_b A\n"

[<Fact>]
let ``Run - contexts whose names share what comes before a _ are kept apart`` () =
    let source = """import nanyx/console

context A_B(x) = (d: x -> string)
context A_C(x) = (d: x -> string)

def one: [A_B(a)] a -> string = { v -> A_B(a).d(v) }
def two: [A_C(a) & A_B(a)] a -> string = { v -> A_C(a).d(v) ++ " " ++ one(v) }

def c_int: A_C(int) = (d = { n -> "C" })
def b_int: A_B(int) = (d = { n -> "B" })
def main = {
  use c_int
  use b_int
  two(1)
}
println(main())
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "C B\n"

[<Fact>]
let ``Run - a lambda captures a member with _ in its name, of a plain context and of a parameterised one`` () =
    let plain = """import nanyx/console

context Log_Line = (log_line: string -> string)

def shout: [Log_Line] string -> string = { s -> log_line(s) ++ "!" }

def run_twice: [Log_Line] () -> string = {
  def again = { shout("b") }
  shout("a") ++ " " ++ again()
}

use Log_Line(log_line = { s -> "<" ++ s ++ ">" }) in println(run_twice())
"""
    runSource (plain.ReplaceLineEndings "\n") |> assertRan "<a>! <b>!\n"
    let parameterised = """import nanyx/console

context To_Text(a) = (to_text: a -> string)
type My_Point = (x: int, y: int)

def describe: [To_Text(a)] a -> string = { value -> to_text(value) }

def main = {
  def show_point: To_Text(My_Point) = (to_text = { p -> "point {p.x}" })
  use show_point
  def later = { describe(My_Point(x = 3, y = 4)) }
  later()
}

println(main())
"""
    runSource (parameterised.ReplaceLineEndings "\n") |> assertRan "point 3\n"
