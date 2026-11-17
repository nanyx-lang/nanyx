module TranspilerWasmTests.Features.Matching.RunNestedTagPatterns

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - arms that name one tag with different tags in its payload`` () =
    let source =
        normalized """type ParseError = #helpRequested | #missingArgument(string)

def describe: #some(int) | #error(ParseError) -> string = { parsed ->
  match parsed
    | #some(n) -> "ok {n}"
    | #error(#helpRequested) -> "help"
    | #error(#missingArgument(name)) -> "missing {name}"
}

@entry
def main = {
  println(describe(#some(3)))
  println(describe(#error(#helpRequested)))
  println(describe(#error(#missingArgument("--input"))))
}
"""
    runSource source |> assertRan "ok 3\nhelp\nmissing --input\n"

[<Fact>]
let ``Run - a nested pattern binds the recursive union inside an outer tag`` () =
    let source =
        normalized """type J = #s(string) | #n(float) | #o(list(J))

def make: () -> #some(J) | #error(int) = { -> #some(#o([#s("x"), #n(1.0)])) }

match make()
  | #some(#o([a, b])) -> match (a, b)
    | (#s(x), #n(y)) -> println("{x} {y}")
    | _ -> println("no")
  | _ -> println("no")
"""
    runSource source |> assertRan "x 1.0\n"

[<Fact>]
let ``Run - a nested pattern binds a list's element type inside an outer tag`` () =
    let source =
        normalized """def describe = { v: #some(#o(list(#s(string) | #n(float)))) | #error(int) ->
  match v
    | #some(#o([a, b])) -> match (a, b)
      | (#s(x), #n(y)) -> "{x} {y}"
      | _ -> "no"
    | _ -> "no"
}

println(describe(#some(#o([#s("x"), #n(1.0)]))))
"""
    runSource source |> assertRan "x 1.0\n"
