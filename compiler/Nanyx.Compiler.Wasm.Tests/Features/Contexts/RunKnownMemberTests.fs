/// A function that requires contexts is specialised on the members known where it is called: it calls a known member
/// directly, passes it on as known, and is not passed it (#744, see "Specialising on known members" in
/// `docs/src/content/work-in-progress/context-calls.md`).
module TranspilerWasmTests.Features.Contexts.KnownMembers

open System
open Xunit
open TranspilerWasmTestHelpers

/// The text of the function `name` in `wat`, from its `(func` to the line that closes it.
let private functionText (name: string) (wat: string) =
    let start = wat.IndexOf($"(func ${name} ")
    Assert.True(start >= 0, $"no function {name}")
    wat.Substring(start, wat.IndexOf("\n  )", start) - start)

[<Fact>]
let ``A function that passes a known member on passes it as known`` () =
    let source = """import nanyx/console

def greet: [Console] string -> () = { name -> println("hi " ++ name) }

def main = {
  greet("Ada")
}

main()
"""
    let wat = transpileWat source
    // The ambient `Console` is known where `main` calls `greet`, and where `greet` calls `println`: neither is passed
    // a member, and neither makes a closure.
    let greet = functionText "greet__known" wat
    Assert.StartsWith("""(func $greet__known (param $name i32) (result i32)""", greet)
    let println = Text.RegularExpressions.Regex.Match(greet, @"return_call \$(println__string__known\w*)").Groups.[1].Value
    Assert.StartsWith($"(func ${println} (param $value i32) (result i32)", functionText println wat)
    Assert.DoesNotContain("__fn_write", wat)
    runSource source |> assertRan "hi Ada\n"

[<Fact>]
let ``A definition called with two instances has a specialisation for each`` () =
    let source = """import nanyx/console

context Show(a) = (show: a -> string)

type Point = (x: int, y: int)

type Size = (width: int, height: int)

def Point.show: Point -> string = { p -> "point {p.x},{p.y}" }

def Size.show: Size -> string = { s -> "size {s.width}x{s.height}" }

def describe: [Show(a)] a -> string = { value -> "<" ++ show(value) ++ ">" }

def main = {
  println(describe(Point(x = 1, y = 2)))
  println(describe(Size(width = 3, height = 4)))
}

main()
"""
    let wat = transpileWat source
    Assert.Contains("""call $Point_show""", wat)
    Assert.Contains("""call $Size_show""", wat)
    Assert.Equal(2, countOccurrences "(func $describe__" wat)
    Assert.DoesNotContain("call_indirect", wat)
    runSource source |> assertRan "<point 1,2>\n<size 3x4>\n"

[<Fact>]
let ``A known member used as a value is made where it is used`` () =
    let source = """import nanyx/console

context Show(a) = (show: a -> string)

type Point = (x: int, y: int)

def Point.show: Point -> string = { p -> "({p.x}, {p.y})" }

def showAll: [Show(a)] list(a) -> list(string) = { values -> values \list.map(show) }

def main = {
  for showAll([Point(x = 1, y = 2), Point(x = 3, y = 4)]) | s -> println(s)
}

main()
"""
    let wat = transpileWat source
    // `showAll` is not passed the member, but makes the closure over it that `list.map` takes.
    Assert.Contains("""(func $showAll__rec_x_int_y_int__known (param $values i32) (result i32)""", wat)
    Assert.Contains("__fn_Point_show", wat)
    runSource source |> assertRan "(1, 2)\n(3, 4)\n"

[<Fact>]
let ``A use of a lambda that captures a local is still passed as a closure`` () =
    let source = """import nanyx/console

context Greet = (greet: string -> string)

def shout: [Greet] string -> string = { name -> greet(name) ++ "!" }

def main = {
  def suffix = "?"
  use (greet = { s -> s ++ suffix })
  println(shout("a"))
}

main()
"""
    let wat = transpileWat source
    Assert.Contains("""(func $shout (param $name i32) (param $__ctx_greet i32) (result i32)""", wat)
    Assert.Contains("call_indirect", wat)
    runSource source |> assertRan "a?!\n"
