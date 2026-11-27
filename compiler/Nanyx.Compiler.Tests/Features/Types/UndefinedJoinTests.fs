/// `undefined` in a type's arguments (#3129): a `list(undefined)` joined with a `list(int)`, by `++` or as a branch, is
/// a `list(int)`, as `undefined` joined with `int` is `int`, but a nominal type's arguments are its own, so an
/// `MArray(undefined)` is no `MArray(int)`.
module Nanyx.Compiler.Tests.Features.Types.UndefinedJoin

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

let private empty = "def e: list(undefined) = []\ndef xs: list(int) = [1]\n"

/// The types of `names` in `source`, which must compile with no diagnostics.
let private typesIn (source: string) (names: string list) =
    let result = Compiler.compile (withFinalNewline source)
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    typesOf (withFinalNewline source) names

[<Fact>]
let ``A list of undefined joined with ++ is the other operand's list, on either side`` () =
    Assert.Equal<string list>(
        [ "list(int)"; "list(string)"; "list(int)"; "list(int)" ],
        typesIn (empty + "def a = [1, 2] ++ e\ndef b = e ++ [\"x\"]\ndef c = xs ++ e\ndef d = e ++ xs") [ "a"; "b"; "c"; "d" ])

[<Fact>]
let ``A list of values that never come joined with ++ is the other operand's list`` () =
    Assert.Equal<string list>([ "list(int)" ], typesIn "def f = [1, 2] ++ [crash(\"no\")]" [ "f" ])

[<Fact>]
let ``A branch that is a list of undefined takes the other branches' type, whichever comes first`` () =
    Assert.Equal<string list>(
        [ "list(int)"; "list(int)"; "list(int)" ],
        typesIn (empty + "def h = if true then xs else e\ndef g = if true then e else xs\ndef m = match 1\n  | 1 -> e\n  | _ -> xs")
            [ "h"; "g"; "m" ])

[<Fact>]
let ``A list of undefined and another list given for one type variable make it the other list`` () =
    Assert.Equal<string list>(
        [ "list(int)"; "list(int)" ],
        typesIn (empty + "def pick: a, a -> a = { p, q -> p }\ndef j = pick(e, xs)\ndef k = pick(xs, e)") [ "j"; "k" ])

[<Fact>]
let ``The list the issue joins is a list of int, so reading it as strings is a mismatch`` () =
    let source =
        normalized """module unsound

def e: list(undefined) = []

def grown = [1, 2] ++ e

@entry
def main = {
  match list.head(grown)
    | #some(x) -> println(string.length(x))
    | #emptyList -> println("empty")
}
"""
    let result = Compiler.compile source
    Assert.Equal<string list>([ DiagnosticCodes.TypeMismatch.Code ], result.Diagnostics |> List.map _.Code)
    Assert.Equal("'int' vs 'string'", result.Diagnostics.Head.Message)

[<Fact>]
let ``A nominal type of undefined is not given where one of int is expected`` () =
    let source =
        normalized """import nanyx/marray

def size: [Memory] MArray(int) -> int = { m -> MArray.length(m) }

def t = { () ->
  memory {
    def m: MArray(undefined) = MArray.fromList([])
    size(m)
  }
}
"""
    let code, message, _ = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.Equal("'MArray(undefined)' vs 'MArray(int)'", message)

[<Fact>]
let ``A function that takes undefined is not given where one that takes int is expected`` () =
    let code, _, _ = onlyDiagnostic "def n: (undefined -> int) = { x -> 1 }\ndef o: (int -> int) = n\n"
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
