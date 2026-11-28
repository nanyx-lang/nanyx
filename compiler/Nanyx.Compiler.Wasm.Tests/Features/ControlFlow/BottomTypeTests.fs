module TranspilerWasmTests.Features.ControlFlow.BottomType

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - a branch of type undefined that is not taken leaves the other branch's value`` () =
    let source =
        normalized """def stop: int -> undefined = { x -> stop(x) }

def double = { n -> if n > 0 then n * 2 else stop(n) }

def name = { n -> match n | 1 -> "one" | _ -> stop(n) }

@entry
def main = {
  println("{double(21)} {name(1)}")
}
"""
    runSource source |> assertRan "42 one\n"

[<Fact>]
let ``Run - crash writes its message to stderr and exits with 1, running nothing after it`` () =
    let source =
        normalized """def half: int -> int = { n -> if n % 2 == 0 then n // 2 else crash("odd: {n}") }

@entry
def main = {
  println("{half(4)}")
  println("{half(3)}")
  println("never")
}
"""
    let outcome = runSource source
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("2\n", outcome.StandardOutput)
    Assert.Equal("crash: odd: 3\n", withoutStack outcome.StandardError)

/// A `crash` where a value of another type is wanted, its own an `i32`: in a list of `int`s or of `float`s, first or
/// after another element, as an argument, a record's field, and an `if` branch whose other branch is a `float`.
let crashesWhereAValueIsWanted: obj[] seq =
    [ "def xs = [1, crash(\"no\")]\nprintln(xs)\n"
      "println([crash(\"no\"), 1])\n"
      "println([1.5, crash(\"no\")])\n"
      "def half: float -> float = { x -> x / 2.0 }\nprintln(half(crash(\"no\")))\n"
      "def r = (a = 1.5, b = crash(\"no\"))\nprintln(r.a)\n"
      "def g: bool -> float = { b -> if b then 1.5 else crash(\"no\") }\nprintln(g(false))\n" ]
    |> Seq.collect (fun source -> [ [| box source; box true |]; [| box source; box false |] ])

[<Theory>]
[<MemberData(nameof crashesWhereAValueIsWanted)>]
let ``Run - a crash where a value of another type is wanted crashes with its message`` (source: string) (inlineCalls: bool) =
    let outcome = runSourceWithOptions true inlineCalls "" source
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("", outcome.StandardOutput)
    Assert.Equal("crash: no\n", withoutStack outcome.StandardError)
    Assert.Contains("  crash (prelude.nyx:", outcome.StandardError)

[<Fact>]
let ``Run - a list of crashes where a list of ints is expected crashes`` () =
    let source =
        normalized """def xs: list(int) = [crash("no")]

println("start")
println(xs)
"""
    let outcome = runSource source
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("start\n", outcome.StandardOutput)
    Assert.Equal("crash: no\n", withoutStack outcome.StandardError)

[<Theory>]
[<InlineData("println(crash(\"no\") + 1)")>]
[<InlineData("println(crash(\"no\").name)")>]
[<InlineData("def x = crash(\"no\")\n  println(x(1))")>]
[<InlineData("println(crash(\"no\") \\g)")>]
let ``Run - a use of a crash crashes with its message`` (statement: string) =
    let source =
        normalized $"""type P = (name: string)

def g: P -> int = {{ p -> 1 }}

@entry
def main = {{
  println("start")
  {statement}
}}
"""
    let outcome = runSource source
    Assert.Equal(1, outcome.ExitCode)
    Assert.Equal("start\n", outcome.StandardOutput)
    Assert.Equal("crash: no\n", withoutStack outcome.StandardError)

[<Fact>]
let ``Run - the head of a list of undefined is #emptyList, with or without an arm for #some (#3129)`` () =
    // `list.head` is compiled at an `a` of `undefined`, whose `#some(a)` no value has.
    let source =
        normalized """def items = []

match items \head
  | #emptyList -> println("empty")

match items \head
  | #some(x) -> println("some")
  | #emptyList -> println("empty again")

println(list.length(items))
"""
    runSource source |> assertRan "empty\nempty again\n0\n"
