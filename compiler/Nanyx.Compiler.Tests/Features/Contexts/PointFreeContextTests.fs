/// A value that is a function built point-free from functions that require contexts requires them in its type, as a
/// lambda that calls them does (#1288).
module Nanyx.Compiler.Tests.Features.PointFreeContexts

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

let private shout =
    """import nanyx/console

def shout = { s -> println(s ++ "!") }

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

[<Fact>]
let ``Calling a composition that requires Console requires it where it is called`` () =
    let source = shout + """
def greet = { name -> "hello " ++ name } >> shout

def quiet: [] () -> () = { greet("ada") }
"""
    Assert.Equal((DiagnosticCodes.UndeclaredContext.Code, "greet"), singleError source)