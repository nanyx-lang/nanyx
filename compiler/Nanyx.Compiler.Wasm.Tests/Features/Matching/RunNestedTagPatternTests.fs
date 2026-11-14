module TranspilerWasmTests.Features.Matching.RunNestedTagPatterns

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Run - arms that name one tag with different tags in its payload`` () =
    let source =
        normalized """import nanyx/console

type ParseError = #helpRequested | #missingArgument(string)

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
