/// A value that is a function built point-free from functions that require contexts requires them in its type, as a
/// lambda that calls them does (#1288).
module Nanyx.Compiler.Tests.Features.PointFreeContexts

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

let private shout =
    """def shout = { s -> println(s ++ "!") }

def asked: ([c] string -> ()) -> [c] string -> () = { f -> { s: string -> s ++ "?" } >> f }
"""

[<Fact>]
let ``A composition of a function that requires Console requires it`` () =
    let source = shout + """
def greet = { name -> "hello " ++ name } >> shout
"""
    Assert.Equal(Some "def greet: [Console] string -> ()", hoverType source "greet =")

[<Fact>]
let ``A function returned from a call requires what the function passed to it requires`` () =
    let source = shout + """
def askShout = asked(shout)
"""
    Assert.Equal(Some "def askShout: [Console] string -> ()", hoverType source "askShout =")

[<Fact>]
let ``A local value that composes a function that requires Console requires it`` () =
    let source = shout + """
def main = {
  def greet = { name -> "hello " ++ name } >> shout
  greet("ada")
}
"""
    Assert.Equal(Some "greet: [Console] string -> ()", hoverType source "greet =")
    Assert.Equal(Some "def main: [Console] () -> ()", hoverType source "main =")

[<Fact>]
let ``A named module's value can compose a function that requires Console, as building it requires nothing`` () =
    let source = "module greetings\n\n" + shout + """
export def greet = { name -> "hello " ++ name } >> shout
"""
    Assert.Equal(Some "def greet: [Console] string -> ()", hoverType source "greet =")

/// `count` layers of functions, `f1` to `f{count}`, each calling the one below it twice, on top of an `f0` that prints:
/// `f0` is reached from the top along 2^count paths.
let private layers (count: int) =
    [ "def f0 = { s: string ->"; "  println(s)"; "  s"; "}" ]
    @ [ for i in 1 .. count -> $"def f{i} = {{ x -> f{i - 1}(f{i - 1}(x)) }}" ]
    |> String.concat "\n"

[<Fact>]
let ``A function named as a value requires what a function many calls below it does, however many paths reach it`` () =
    // Each function's contexts are found once, not once per path (#1458).
    let source = layers 40 + "\n\ndef g = { xs -> xs \\list.map(f40) }\n"
    Assert.Equal(Some "def g: [Console] list(string) -> list(string)", hoverType source "g =")

[<Fact>]
let ``A function named as a value requires what the functions in a cycle below it do`` () =
    let source = """def ping = { n: int -> if n > 0 then pong(n - 1) else println("done") }

def pong = { n: int -> ping(n) }

def run = { ns -> ns \list.map(pong) }
"""
    Assert.Equal(Some "def run: [Console] list(int) -> list(())", hoverType source "run =")

[<Fact>]
let ``Calling a composition that requires Console requires it where it is called`` () =
    let source = shout + """
def greet = { name -> "hello " ++ name } >> shout

def quiet: [] () -> () = { greet("ada") }
"""
    Assert.Equal((DiagnosticCodes.UndeclaredContext.Code, "greet"), singleError source)