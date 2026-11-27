module Nanyx.Compiler.Tests.Features.Inference.ModuleSolve

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A module's constraints are solved a def at a time: each def's on top of what the defs before it solved to, where the
// two share no variable (#1266). That must find what solving the whole module afresh finds: the same failure, reported
// at the same place, and a union's tags in the same order.

/// `count` defs, from `f{from}` on, that each pass a lambda to `list.map`, whose contexts get rests made up for them.
let private mapDefs (from: int) (count: int) =
    [ for i in from .. from + count - 1 -> $"def f{i} = list.map([1, 2, 3], {{ x -> x + {i} }}) \\list.length" ]

let private moduleOf (lines: string list) = String.concat "\n" ("module m" :: "" :: lines) + "\n"

[<Fact>]
let ``A failure among many defs is reported where solving the module afresh reports it`` () =
    let source = moduleOf (mapDefs 1 30 @ [ "def bad: string = list.map([1, 2, 3], { x -> x + 1 }) \\list.length" ] @ mapDefs 31 30)
    Assert.Equal(
        (DiagnosticCodes.TypeMismatch.Code, "'int' vs 'string'", "list.map([1, 2, 3], { x -> x + 1 }) \\list.length"),
        onlyDiagnostic source)

[<Fact>]
let ``A union built across defs keeps its tags in the order solving the module afresh gives`` () =
    let source =
        moduleOf (
            mapDefs 1 20
            @ [ "def colour = { n: int -> if n > 0 then #red else if n < 0 then #green else #blue }"
                "def shade = { n: int -> if n > 10 then #dark else colour(n) }" ]
            @ mapDefs 21 20
            @ [ "def tint = { n: int -> if n > 5 then shade(n) else #light }" ])
    let result = Compiler.compile source
    Assert.Empty(result.Diagnostics)
    Assert.Equal("int -> #red | #green | #blue", typeIn result.Typed.Value "colour")
    Assert.Equal("int -> #dark | #red | #green | #blue", typeIn result.Typed.Value "shade")
    Assert.Equal("int -> #dark | #red | #green | #blue | #light", typeIn result.Typed.Value "tint")

[<Fact>]
let ``Defs that share a variable, as mutually recursive ones do, are solved together`` () =
    let source =
        moduleOf (
            mapDefs 1 10
            @ [ "def isEven = { n -> if n == 0 then true else isOdd(n - 1) }"
                "def isOdd = { n -> if n == 0 then false else isEven(n - 1) }" ]
            @ mapDefs 11 10)
    let result = Compiler.compile source
    Assert.Empty(result.Diagnostics)
    Assert.Equal("int -> bool", typeIn result.Typed.Value "isEven")
    Assert.Equal("int -> bool", typeIn result.Typed.Value "isOdd")

/// `count` defs, from `g{from}` on, that each pipe a list through calls with lambdas, the last with an argument before
/// its lambda, which the type checker looks the types so far up for many times.
let private pipeDefs (from: int) (count: int) =
    [ for i in from .. from + count - 1 ->
          $"def g{i}: list(int) -> int = {{ xs ->\n  xs\n    \\list.map {{ x -> x + {i} }}\n    \\list.filter {{ x -> x > 1 }}\n    \\list.fold(0, {{ a, b -> a + b }})\n}}" ]

[<Fact>]
let ``A failure early in a module leaves the defs after it solved on top of what agrees, not afresh`` () =
    // Each type looked up after a failure solved the whole module again, twice, so checking took time in proportion to
    // the square of the module's size, and minutes for a package's (#2449). Then each def after it was still solved from
    // the start once, as the list a def ended with had the failure in it, and so was no checkpoint; now the constraints
    // of it that agree are (#2848). What the failure adds to the lists solved from the start is the same however many
    // defs follow it.
    let failing count = moduleOf ([ "def first: int = \"one\"" ] @ pipeDefs 1 count)
    let passing count = moduleOf ([ "def first: int = 1" ] @ pipeDefs 1 count)
    Assert.Equal((DiagnosticCodes.TypeMismatch.Code, "'string' vs 'int'", "\"one\""), onlyDiagnostic (failing 20))
    let fullSolves (source: string) = snd (Solving.countingFullSolves (fun () -> Compiler.compile source))
    let addedBefore count = fullSolves (failing count) - fullSolves (passing count)
    Assert.Equal(addedBefore 20, addedBefore 80)

/// A copy of the prelude's `proc` (`stdlib/nanyx/prelude-results.nyx`), as `prc`: checking it sets a constraint of the
/// call in its `for` member aside for a while, though the def agrees once it is checked.
let private procLike =
    [ "context Prc = ("
      "  try: [c] ((#some(a) | r), ([c] a -> #some(b) | r)) -> #some(b) | r"
      "  return: a -> #some(a) | r"
      "  zero: #some() | r"
      "  for: [d] (list(x), ([d] x -> #__nextItem | #some(y) | s), ([d] () -> #some(y) | s)) -> #some(y) | s"
      ")"
      ""
      "def prc: [c] ([Prc, c] () -> #some(b) | r) -> #some(b) | r = { block ->"
      "  use Prc("
      "    try = {"
      "      | #some(value), continue -> continue(value)"
      "      | failure, _ -> failure"
      "    }"
      "    return = { value -> #some(value) }"
      "    zero = #some()"
      "    for = { items, body, rest -> prcFor(items, body, rest) }"
      "  ) in block()"
      "}"
      ""
      "def prcFor"
      "  : [c] list(a), ([c] a -> #__nextItem | #some(b) | r), ([c] () -> #some(b) | r) -> #some(b) | r"
      "  = {"
      "    | [], _, rest -> rest()"
      "    | [item, ...others], body, rest ->"
      "      match body(item)"
      "        | #__nextItem -> prcFor(others, body, rest)"
      "        | ended -> ended"
      "  }" ]

[<Fact>]
let ``A constraint set aside while a def is checked leaves the defs after it solved on top of it`` () =
    // The lists after it were solved on the copy of the constraints that agreed, which has no checkpoint, so they were
    // solved from the start each time `solvedSoFar` had forgotten that list: the more defs after it, the more (#2709).
    // Now what it adds is the same however many defs follow it.
    let fullSolves (lines: string list) =
        let result, count = Solving.countingFullSolves (fun () -> Compiler.compile (moduleOf lines))
        Assert.Empty(result.Diagnostics)
        count
    let addedBefore count = fullSolves (procLike @ pipeDefs 1 count) - fullSolves (pipeDefs 1 count)
    Assert.Equal(addedBefore 10, addedBefore 40)

[<Fact>]
let ``A call passed a division whose storages don't agree adds no full solve for the defs after it that another error doesn't`` () : unit =
    // The call finds no instance for the type the division leaves open, and looks for the division's failure among the
    // constraints solved so far, not by solving them afresh (#2852).
    let fullSolves (first: string) count =
        let result, solves = Solving.countingFullSolves (fun () -> Compiler.compile (moduleOf ([ first ] @ pipeDefs 1 count)))
        Assert.Equal<string list>([ DiagnosticCodes.TypeMismatch.Code ], result.Diagnostics |> List.map _.Code)
        solves
    let addedBefore count =
        fullSolves "def i: Time(int) = 1ms\ndef first: [Console] () -> () = { () -> println(1.5ns / i) }" count
        - fullSolves "def first: int = \"one\"" count
    Assert.Equal(addedBefore 20, addedBefore 80)
