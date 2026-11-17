module Nanyx.Compiler.Tests.Features.Refactorings.ShorthandLambda

open Xunit
open NyxCompiler
open NyxCompiler.Refactorings
open Nanyx.Formatter
open Nanyx.Compiler.Tests.TestSupport

// Convert a lambda to its shorthand and back.

/// The offers at `$$` in `marked`, and the source without the mark.
let private offersAt (marked: string) =
    let marked = normalized marked
    let offset = marked.IndexOf "$$"
    let source = marked.Remove(offset, 2)
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    source, ShorthandLambda.at Formatter.Options.defaults "" source result offset Compiler.compile

/// The text after the one offer at `$$`, checked to compile and to be formatted, and the name its rename starts on.
let private convertedWithRename (marked: string) =
    match offersAt marked with
    | source, [ Available refactoring ] ->
        let after = applyEdits source refactoring.Edits
        let result = Compiler.compile after
        Assert.True(result.Diagnostics.IsEmpty, sprintf "%s\n%A" after result.Diagnostics)
        match FormatCommand.formatText Formatter.Options.defaults "" after with
        | FormatCommand.Formatting.Formatted formatted -> Assert.Equal(formatted, after)
        | other -> failwithf "%A" other
        let renamed =
            refactoring.Rename
            |> Option.map (fun at -> after.Substring(at) |> Seq.takeWhile System.Char.IsLetterOrDigit |> Seq.toArray |> System.String)
        after, renamed
    | _, offers -> failwithf "expected one offer, got %A" offers

let private converted (marked: string) = fst (convertedWithRename marked)

[<Fact>]
let ``A lambda whose parameter is the left operand becomes a shorthand`` () =
    Assert.Equal("def doubled = [1, 2] \\map { * 2 }\n", converted "def doubled = [1, 2] \\map { x -> x$$ * 2 }\n")

[<Fact>]
let ``A lambda of two parameters, the two operands, becomes the operator alone`` () =
    Assert.Equal("def total = [1, 2] \\fold(0, { + })\n", converted "def total = [1, 2] \\fold(0, { a, b$$ -> a + b })\n")

[<Fact>]
let ``A comparison becomes a shorthand`` () =
    Assert.Equal("def full = [\"a\", \"\"] \\filter { != \"\" }\n", converted "def full = [\"a\", \"\"] \\filter { $$s -> s != \"\" }\n")

[<Fact>]
let ``The right operand keeps its brackets`` () =
    Assert.Equal("def scaled = [1, 2] \\map { * (1 + 2) }\n", converted "def scaled = [1, 2] \\map { x -> x * $$(1 + 2) }\n")

[<Fact>]
let ``A shorthand gets a parameter, which starts a rename`` () =
    let after, renamed = convertedWithRename "def doubled = [1, 2] \\map {$$ * 2 }\n"
    Assert.Equal("def doubled = [1, 2] \\map { x -> x * 2 }\n", after)
    Assert.Equal(Some "x", renamed)

[<Fact>]
let ``An operator alone gets two parameters`` () =
    Assert.Equal("def total = [1, 2] \\fold(0, { x, y -> x + y })\n", converted "def total = [1, 2] \\fold(0, { $$+ })\n")

[<Fact>]
let ``A member access shorthand gets a parameter`` () =
    Assert.Equal(
        "def names = [(name = \"a\")] \\map { x -> x.name }\n",
        converted "def names = [(name = \"a\")] \\map { $$.name }\n")

[<Fact>]
let ``A minus in a shorthand is the infix one`` () =
    Assert.Equal("def less = [1, 2] \\map { x -> x - 1 }\n", converted "def less = [1, 2] \\map { $$- 1 }\n")

[<Fact>]
let ``A negation is not converted`` () =
    Assert.Empty(snd (offersAt "def negated = [1, 2] \\map { n -> -$$n }\n"))

[<Fact>]
let ``The parameter's name is made unique`` () =
    Assert.Equal(
        "def x = 3\n\ndef scaled = [1, 2] \\map { x2 -> x2 * x }\n",
        converted "def x = 3\n\ndef scaled = [1, 2] \\map { $$* x }\n")

[<Fact>]
let ``A lambda whose parameter is used again is not converted`` () =
    Assert.Empty(snd (offersAt "def squares = [1, 2] \\map { x -> x$$ * x }\n"))

[<Fact>]
let ``A parameter that writes its type is refused`` () =
    match offersAt "def doubled = [1, 2] \\map { (n: int) -> n$$ * 2 }\n" with
    | _, [ Refused(_, RefactoringKind.Rewrite, reason) ] -> Assert.Equal("A parameter writes its type, which a shorthand lambda can't write", reason)
    | _, offers -> failwithf "expected a refusal, got %A" offers
