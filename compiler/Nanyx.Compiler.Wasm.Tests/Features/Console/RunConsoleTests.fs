module TranspilerWasmTests.Features.Console.Run

open System
open System.Diagnostics
open System.IO
open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - script hello world prints the greeting`` () =
    runE2E "twentythree" |> assertRan "Hello, Nanyx!\n"

[<Fact>]
let ``Run - project hello world runs the entry point`` () =
    runE2E "twentyfour" |> assertRan "Hello, World!\n"

[<Fact>]
let ``Run - dbg prints integers, strings and tags in order`` () =
    let source =
        normalized """import nanyx/console

def main = {
  dbg(42)
  dbg(-7)
  dbg("text")
  dbg(#ok)
  dbg(#some(5))
  println("done")
}

main()
"""
    runSource source |> assertRan (withFinalNewline """dbg: 42
dbg: -7
dbg: "text"
dbg: #ok
dbg: #some(5)
done""")

[<Fact>]
let ``Run - dbg prints a tag-typed value by its tag and payload`` () =
    let source =
        normalized """import nanyx/console

def pick = { n ->
  if n == 0 then #some("first")
  else if n == 1 then #none
  else #err
}

def main = {
  def a = dbg(pick(0))
  dbg(pick(1))
  dbg(pick(2))
  dbg(dbg(a))
  println("done")
}

main()
"""
    runSource source
    |> assertRan (withFinalNewline """dbg: #some("first")
dbg: #none
dbg: #err
dbg: #some("first")
dbg: #some("first")
done""")

[<Fact>]
let ``Run - console print and println write to stdout`` () =
    let source =
        normalized """import nanyx/console

print("a")
print("b")
println("")
println("c")
"""
    runSource source |> assertRan "ab\nc\n"

[<Fact>]
let ``Run - println writes an int, a string, a record and a list through their ToString`` () =
    let source = """type Point = (x: int, y: int)

def Point.toString = { p -> "<{p.x}, {p.y}>" }

@entry
def main = {
  println(42)
  println("text")
  println((name = "Ada", age = 36))
  println([1, 2, 3])
  println(Point(x = 4, y = 5))
  println(#some((1, "two")))
  print(-7)
  print(" ")
  println(true)
}
"""
    runSource source |> assertRan "42\ntext\n(age = 36, name = Ada)\n[1, 2, 3]\n<4, 5>\n#some(1, two)\n-7 true\n"

[<Fact>]
let ``Run - a function generic in what it prints passes its ToString to println`` () =
    let source = """def labelled: [Console & ToString(a)] string, a -> () = { label, value ->
  print(label ++ ": ")
  println(value)
}

@entry
def main = {
  labelled("count", 3)
  labelled("names", ["a", "b"])
  eprintln(1.5)
}
"""
    let outcome = runSource source
    Assert.True((outcome.ExitCode = 0), $"Expected exit code 0, got {outcome.ExitCode}. stderr:\n{outcome.StandardError}")
    Assert.Equal("count: 3\nnames: [a, b]\n", outcome.StandardOutput)
    Assert.Equal("1.5\n", outcome.StandardError)

[<Fact>]
let ``Run - println imported under an alias writes through the runtime's Console`` () =
    let source = """module main

import nanyx/console as c

@entry
def main = {
  c.println("hi")
  c.println([1, 2])
}
"""
    runSource source |> assertRan "hi\n[1, 2]\n"

[<Fact>]
let ``Run - a def that declares the Console of an aliased import writes through the runtime's Console`` () =
    let source = """module main

import nanyx/console as c

def g: [c.Console] () -> () = { c.println("5") }

def h: [c.Console & ToString(a)] a -> () = { value ->
  g()
  c.write("value: ")
  c.println(value)
}

@entry
def main = {
  g()
  h([1, 2])
}
"""
    runSource source |> assertRan "5\n5\nvalue: [1, 2]\n"

[<Fact>]
let ``Run - eprint and eprintln write to stderr`` () =
    let source =
        normalized """def warn: [Console] string -> () = { message -> eprintln("warning: " ++ message) }

@entry
def main = {
  print("out ")
  eprint("err ")
  flush()
  warn("careful")
  println("done")
}
"""
    let outcome = runSource source
    Assert.True((outcome.ExitCode = 0), $"Expected exit code 0, got {outcome.ExitCode}. stderr:\n{outcome.StandardError}")
    Assert.Equal("out done\n", outcome.StandardOutput)
    Assert.Equal("err warning: careful\n", outcome.StandardError)

[<Fact>]
let ``Run - readLine and readToEnd read stdin`` () =
    let source =
        normalized """@entry
def main = {
  match readLine()
    | #some(line) -> println("first [" ++ line ++ "]")
    | #error(_) -> println("no first")
  match readLine()
    | #some(line) -> println("second [" ++ line ++ "]")
    | #error(_) -> println("no second")
  match readToEnd()
    | #some(rest) -> println("rest [" ++ rest ++ "]")
    | #error(_) -> println("failed")
  match readLine()
    | #some(line) -> println("more [" ++ line ++ "]")
    | #error(_) -> println("end of input")
}
"""
    runSourceWithInput "hello\r\nworld\nthe\nrest" source
    |> assertRan "first [hello]\nsecond [world]\nrest [the\nrest]\nend of input\n"
