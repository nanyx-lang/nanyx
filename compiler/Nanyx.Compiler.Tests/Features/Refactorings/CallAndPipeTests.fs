module Nanyx.Compiler.Tests.Features.Refactorings.CallAndPipe

open Xunit
open NyxCompiler
open NyxCompiler.Refactorings
open Nanyx.Formatter
open Nanyx.Compiler.Tests.TestSupport

// Convert between a call and a pipe.

/// The offers at `^` in `marked`, and the source without the mark.
let private offersAt (marked: string) =
    let marked = normalized marked
    let offset = marked.IndexOf "^"
    let source = marked.Replace("^", "")
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    source, CallAndPipe.at Formatter.Options.defaults "" source result offset Compiler.compile |> List.map Offer.decided

/// The text after the offer at the mark, checked to compile and be formatted.
let private rewritten (marked: string) =
    match offersAt marked with
    | source, [ Available refactoring ] ->
        refactoredText source refactoring
    | _, offers -> failwithf "expected one offer, got %A" offers

[<Fact>]
let ``A call becomes a pipe of its first argument`` () =
    Assert.Equal(
        normalized
            """def add = { (a: int), (b: int) -> a + b }

def total = 1 \add(2)
""",
        rewritten
            """def add = { (a: int), (b: int) -> a + b }

def total = ^add(1, 2)
""")

[<Fact>]
let ``A call of one argument becomes a pipe without brackets`` () =
    Assert.Contains(
        "def total = 3 \\double\n",
        rewritten
            """def double = { (a: int) -> a * 2 }

def total = ^double(3)
""")

[<Fact>]
let ``A chain of calls converts as a whole`` () =
    Assert.Contains(
        "def total = xs \\map { x -> x * 2 } \\filter { x -> x > 2 }",
        rewritten
            """def xs = [1, 2, 3]

def total = list.filter(^list.map(xs, { x -> x * 2 }), { x -> x > 2 })
""")

[<Fact>]
let ``A pipe becomes a call`` () =
    Assert.Contains(
        "def total = add(1, 2)\n",
        rewritten
            """def add = { (a: int), (b: int) -> a + b }

def total = 1 \^add(2)
""")

[<Fact>]
let ``A chain of pipes converts as a whole`` () =
    Assert.Contains(
        "def total = list.filter(list.map(xs, { x -> x * 2 }), { x -> x > 2 })",
        rewritten
            """def xs = [1, 2, 3]

def total = xs \^map { x -> x * 2 } \filter { x -> x > 2 }
""")

[<Fact>]
let ``An attached definition is piped by its own name`` () =
    Assert.Contains(
        "def tidy = { (s: string) -> s \\trim }",
        rewritten
            """def tidy = { (s: string) -> ^string.trim(s) }
""")

[<Fact>]
let ``A pipe of an attached definition becomes a call by its whole name`` () =
    let after =
        rewritten
            """def tidy = { (s: string) -> s \^trim }
"""
    Assert.Contains("string.trim(s)", after)

[<Fact>]
let ``Where the own name would find another definition the whole name is kept`` () =
    Assert.Contains(
        "def shout = { (s: string) -> s \\text.trim }",
        rewritten
            """module text =
  export def trim = { (s: string) -> s }

def trim = { (n: int) -> n }

def shout = { (s: string) -> ^text.trim(s) }
""")

[<Fact>]
let ``A value that is an operator is bracketed in the pipe`` () =
    Assert.Contains(
        "(1 + 2) \\double",
        rewritten
            """def double = { (a: int) -> a * 2 }

def total = ^double(1 + 2)
""")

[<Fact>]
let ``Nothing is offered off a call or pipe`` () =
    Assert.Empty(snd (offersAt "def ^total = 1 + 2\n"))

