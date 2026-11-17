module ParserTests.Features.Loops.ForLoopArms

open Xunit
open Parser.Program
open ParserTestHelpers

/// The arms of the match that is `f`'s body.
let private outerArms (source: string) =
    match parseModule (source.ReplaceLineEndings "\n") with
    | Result.Ok [ Kind (Def (ValueDef (_, "f", _, Kind (Lambda ([], Kind (Block [ Kind (ExprStatement (Kind (Match (_, arms)))) ]), _))))) ] -> arms
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

/// How many arms the `for` loop that is `body` has.
let private forArmCount (body: Expression) =
    match body.Kind with
    | ForSyntax.Call (_, _, _, Kind (Match (_, arms))) -> List.length arms
    | other -> failwith $"Expected a for loop with arms: %A{other}"

// A `for` with arms written on a match arm's line took the outer match's next arm as one of its own (#1835).
[<Fact>]
let ``A for loop's arms on a match arm's line end at the next arm of the match`` () =
    let source = """
def f = {
  match lines
    | #some(lines) -> for lines | line -> println(line)
    | #error(_) -> println("error")
}
"""
    match outerArms source with
    | [ [ Kind (TagPattern ("some", _)) ], loop; [ Kind (TagPattern ("error", _)) ], _ ] ->
        Assert.Equal(1, forArmCount loop)
    | other -> failwith $"Unexpected arms: %A{other}"

[<Fact>]
let ``A for loop's arms on a match arm's line go on at lines indented further than it`` () =
    let source = """
def f = {
  match lines
    | #some(lines) -> for lines | "" -> println("blank")
                                | line -> println(line)
    | #error(_) -> println("error")
}
"""
    match outerArms source with
    | [ _, loop; _ ] -> Assert.Equal(2, forArmCount loop)
    | other -> failwith $"Unexpected arms: %A{other}"

[<Fact>]
let ``A match nested on a match arm's line ends at the next arm of the outer match`` () =
    let source = """
def f = {
  match x
    | #some(y) -> match y | 0 -> "zero" | _ -> "other"
    | #none -> "none"
}
"""
    match outerArms source with
    | [ _, Kind (Match (_, inner)); [ Kind (TagPattern ("none", _)) ], _ ] -> Assert.Equal(2, List.length inner)
    | other -> failwith $"Unexpected arms: %A{other}"
