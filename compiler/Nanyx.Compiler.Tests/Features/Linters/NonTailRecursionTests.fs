module Nanyx.Compiler.Tests.Features.Linters.NonTailRecursion

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

// The official linter's `nonTailRecursion` rule.

/// What `default/nonTailRecursion` reports for `source`: each message with the text it is reported at.
let private nonTailIn (source: string) =
    (compileWithOfficial source).Diagnostics
    |> List.filter (fun diagnostic -> diagnostic.Code = "default/nonTailRecursion")
    |> List.map (fun diagnostic ->
        Assert.Equal(Severity.Info, diagnostic.Severity)
        diagnostic.Message, textAt source diagnostic.Span.Value)

let private nonTailMessage name why =
    $"`{name}` calls itself here, but not in tail position: {why}. Each call keeps a stack frame until it returns, so a deep enough input runs out of stack."

[<Fact>]
let ``A function that calls itself other than in tail position is reported at the call, saying why`` () =
    let source =
        "def factorial: int -> int = { n -> if n <= 1 then 1 else n * factorial(n - 1) }\n" +
        "def length: list(int) -> int = {\n  | [] -> 0\n  | [_, ...tail] -> 1 + length(tail)\n}\n" +
        "def copy: list(int) -> list(int) = {\n  | [] -> []\n  | [x, ...rest] -> [x, ...copy(rest)]\n}\n" +
        "def show: int -> string = { n -> if n == 0 then \"\" else \"{show(n - 1)}.\" }\n" +
        "def steps: int -> int = { n ->\n  def before = if n == 0 then 0 else steps(n - 1)\n  before + 1\n}\n"
    Assert.Equal<(string * string) list>(
        [ nonTailMessage "factorial" "its result is an operand of `*`", "factorial(n - 1)"
          nonTailMessage "length" "its result is an operand of `+`", "length(tail)"
          nonTailMessage "copy" "its result goes into a list", "copy(rest)"
          nonTailMessage "show" "its result is interpolated into a string", "show(n - 1)"
          nonTailMessage "steps" "its result is bound to `before`", "steps(n - 1)" ],
        nonTailIn source)

[<Fact>]
let ``A local rec function that calls itself other than in tail position is reported`` () =
    let source =
        "def f: int -> int = { n ->\n" +
        "  rec down = { i -> if i == 0 then 0 else 1 + down(i - 1) }\n" +
        "  down(n)\n" +
        "}\n"
    Assert.Equal<(string * string) list>([ nonTailMessage "down" "its result is an operand of `+`", "down(i - 1)" ], nonTailIn source)

[<Fact>]
let ``Recursion in tail position is not reported`` () =
    // In the branches of an `if`, the arms of a `match`, the end of a block, and as a pipe.
    let source =
        "def countdown: (int, int) -> int = { n, acc -> if n == 0 then acc else countdown(n - 1, acc + 1) }\n" +
        "def sum: (list(int), int) -> int = {\n  | [], acc -> acc\n  | [x, ...rest], acc -> sum(rest, acc + x)\n}\n" +
        "def steps: (int, int) -> int = { n, acc ->\n  def next = n - 1\n  match n\n    | 0 -> acc\n    | _ -> next \\steps(acc + 1)\n}\n" +
        "def count: int -> int = { n ->\n  rec loop = { i, acc -> if i == 0 then acc else loop(i - 1, acc + 1) }\n  loop(n, 0)\n}\n"
    Assert.Empty(nonTailIn source)

[<Fact>]
let ``Calls to other functions, and calls from a lambda within the function, are not reported`` () =
    // `twice` calls `inc` other than in tail position, and `again` is called from a lambda it builds, not by itself.
    let source =
        "def inc: int -> int = { n -> n + 1 }\n" +
        "def twice: int -> int = { n -> inc(inc(n)) }\n" +
        "def again: list(int) -> list(int) = { xs -> xs \\ list.map({ x -> list.length(again([x])) }) }\n"
    Assert.Empty(nonTailIn source)
