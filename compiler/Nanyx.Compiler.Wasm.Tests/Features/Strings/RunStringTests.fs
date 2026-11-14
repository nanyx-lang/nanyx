module TranspilerWasmTests.Features.Strings.Run

open System
open System.Diagnostics
open System.IO
open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - an interpolated value of a type only the type checker knows`` () =
    // The backend works a hole's type out from what it sees, and asks the type checker when that is not enough
    // (#193): a generic call's result, a for loop's element, a value bound by a match at the top of a script.
    let source =
        normalized """import nanyx/console

def xs = [1, 2, 3]
def total = list.fold(xs, 0, { acc, x -> acc + x })
println("{total} [{list.fold(xs, 1, { acc, x -> acc * x })}]")
for ["a", "b"] | s ->
  println("[{s}]")
def found = xs \find({ x -> x > 1 })
match found
  | #some(n) -> println("found {n}")
  | #notFound -> println("none")
"""
    runSource source |> assertRan "6 [6]\n[a]\n[b]\nfound 2\n"

[<Fact>]
let ``Run - an interpolated string inside a hole leaves the holes around it alone`` () =
    // Each hole is evaluated before the string is written; a string inside a later hole used to overwrite the
    // earlier holes' values (#326), printing them empty or trapping.
    let source =
        normalized """import nanyx/console
import nanyx/text

def pad: string, int -> string = { text, width -> string.padStart(text, width = width, fill = " ") }

def money: int -> string = { pence ->
  def pounds = pence / 100
  def rest = pence % 100
  "£{pounds}.{string.padStart("{rest}", width = 2, fill = "0")}"
}

def show: [Console] string, int -> () = { name, qty ->
  println("[{pad(name, 6)}][{pad("{qty}", 4)}]")
  println("{pad("{name}!", 7)}|{pad("{qty}", 3)}|{"<{"{qty}"}>"}")
  println("{money(289)} and {money(1205)}")
}

@entry
def main = { show("tea", 2) }
"""
    runSource source |> assertRan "[   tea][   2]\n   tea!|  2|<2>\n£2.89 and £12.05\n"

[<Fact>]
let ``Run - escapes in double-quoted strings`` () =
    // A line break, a quote, a backslash, a brace and a code point can be written in a double-quoted string (#247);
    // a single-quoted string is raw.
    let source = """import nanyx/console

def n = 5
println("a\nb")
println("say \"hi\"")
println("\{n} = {n}|\\|\u{e9}")
println('raw\n{n}')
"""
    let expected = """a
b
say "hi"
{n} = 5|\|é
raw\n{n}
"""
    runSource source |> assertRan (expected.Replace("\r\n", "\n"))

[<Fact>]
let ``Run - string literals larger than the space below the heap`` () =
    // The literals used to run from 256 into a heap and table of free-list heads fixed at 4096, so that `alloc`
    // overwrote them, or read a literal's bytes as the head of a free list (#375, #571). The last literal crosses the
    // first page of memory too.
    let literal (letter: char) (length: int) = String.replicate (length / 10) $"{letter}123456789"
    let literals = [ literal 'a' 1500; literal 'b' 1500; literal 'c' 1500; literal 'd' 70000 ]
    let names = [ "a"; "b"; "c"; "d" ]
    let source =
        [ "import nanyx/console"
          ""
          yield! List.map2 (fun name value -> $"def {name} = \"{value}\"") names literals
          ""
          "@entry"
          "def main = {"
          "  for [1, 2, 3] | n ->"
          "    def made = \"made {n}\" ++ \"!\""
          "    println(made)"
          yield! names |> List.map (fun name -> $"  println({name})")
          """  println(if a ++ b == a ++ b then "equal" else "different")"""
          "}" ]
        |> String.concat "\n"
    let expected =
        [ "made 1!"; "made 2!"; "made 3!"; yield! literals; "equal" ] |> List.map (fun line -> line + "\n") |> String.concat ""
    runSource source |> assertRan expected

[<Fact>]
let ``Run - string concatenation and equality`` () =
    let source =
        normalized """import nanyx/console

def name = "Nanyx"
println("Hello, " ++ name ++ "!")
println(if name == "Nanyx" then "same" else "different")
println(if name != "Nanyx" then "different" else "same")
println(if "a" ++ "b" == "ab" then "concat works" else "concat broken")
"""
    runSource source |> assertRan "Hello, Nanyx!\nsame\nsame\nconcat works\n"

[<Fact>]
let ``Run - interpolating variables bound by match patterns`` () =
    // Regression: the backend did not know the type of a pattern-bound name, so `"{n}"` inside a
    // match arm failed with "Unable to determine interpolated value type" (#35).
    let source =
        normalized """import nanyx/console

def describe: #some(int) | #none -> string = { v ->
  match v
    | #some(n) -> "some {n}"
    | #none -> "none"
}
def greet: #named(string) | #anon -> string = { v ->
  match v
    | #named(name) -> "hello {name}"
    | #anon -> "hello stranger"
}
def first: list(int) -> string = { xs ->
  match xs
    | [] -> "empty"
    | [x, ..._rest] -> "first {x}"
}
println(describe(#some(3)) ++ ", " ++ describe(#none))
println(greet(#named("Ada")) ++ ", " ++ greet(#anon))
println(first([7, 8]) ++ ", " ++ first([]))
"""
    runSource source |> assertRan "some 3, none\nhello Ada, hello stranger\nfirst 7, empty\n"

[<Fact>]
let ``Run - interpolation at the top level of a script`` () =
    // Regression: the script start function used to omit the interpolation scratch locals (#48).
    let source =
        normalized """import nanyx/console

def x = 1 + 1
println("script {x}")
println("{x * 10} again")
"""
    runSource source |> assertRan "script 2\n20 again\n"

[<Fact>]
let ``Run - string literals inside interpolation holes`` () =
    // Regression: literals nested in `{...}` were not collected into the data section (#49).
    let source =
        normalized """import nanyx/console
import nanyx/text

def main = {
  println("len {string.length("abc")} and {string.uppercase("x")}")
}

main()
"""
    runSource source |> assertRan "len 3 and X\n"

[<Fact>]
let ``Run - interpolation holes that allocate or have effects run once`` () =
    // Regression: holes used to be evaluated twice, and an allocating hole in the second pass
    // overwrote the output buffer, so `"a {s ++ "!"} c"` printed just `b!`.
    let source =
        normalized """import nanyx/console
import nanyx/text

def s = "b"
def shout: [Console] string -> string = { v ->
  println("shouting " ++ v)
  string.uppercase(v)
}
println("a {s ++ "!"} c {string.uppercase(s)} d")
println("[{shout("x")}]")
"""
    runSource source |> assertRan "a b! c B d\nshouting x\n[X]\n"

[<Fact>]
let ``Run - strings and lists through the core IR`` () =
    let source =
        normalized """import nanyx/console

def greet: string -> string = { name -> "Hello, " ++ name }

def heads: list(int) -> int = { xs ->
  match xs
    | [] -> 0
    | [a] -> a
    | [a, b, ...rest] -> a + b
}

def answer = { s ->
  match s
    | "yes" -> "agreed"
    | _ -> "declined"
}

def show = { n -> "{n * 1}" }

def main = {
  println(greet("Ada"))
  println(show(heads([10, 20] ++ [30])))
  println(show(heads([7])))
  println(answer("yes"))
  println(answer("no"))
  println(show("four".length))
  if "a" != "b" then println("different") else println("same")
}

main()
"""
    runSource source |> assertRan "Hello, Ada\n30\n7\nagreed\ndeclined\n4\ndifferent\n"

[<Fact>]
let ``Run - interpolated strings and dbg through the core IR`` () =
    let source =
        normalized """import nanyx/console

def pick = { n -> if n > 0 then #yes else #no }

def describe: int -> string = { n -> "n is {n}, positive {n > 0}, pick {pick(n)}" }

def nested: string -> string = { s -> "outer {"inner {s}"} end" }

def debugs: int -> int = { n ->
  dbg(n)
  dbg(#some(n))
  dbg(pick(n))
  n
}

def main = {
  println(describe(3))
  println(describe(-1))
  println(nested("x"))
  debugs(2)
}

main()
"""
    runSource source
    |> assertRan "n is 3, positive true, pick #yes\nn is -1, positive false, pick #no\nouter inner x end\ndbg: 2\ndbg: #some(2)\ndbg: #yes\n"
