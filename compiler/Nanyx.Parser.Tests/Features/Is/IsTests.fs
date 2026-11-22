module ParserTests.Features.Is

open Xunit
open Parser.Program
open ParserTestHelpers

/// The value of the one def `source` declares, `def x = ...`.
let private valueOf (source: string) =
    match parseModule source with
    | Result.Ok [ Kind (Def (ValueDef (_, _, _, value))) ] -> value
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``x is pattern is a test anywhere an expression goes, a lambda's body here`` () =
    match (valueOf "def f = { x -> x is #quux }").Kind with
    | Lambda(_, Kind (IsSyntax.Test(Kind (IdentifierExpr "x"), Kind (TagPattern("quux", None)), None)), _) -> ()
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``what follows a test in an and chain is the test's arm, where its names are in scope`` () =
    match (valueOf "def f = { o -> o is #some(v) and v > 3 and v < 9 }").Kind with
    | Lambda(_, Kind (IsSyntax.Test(_, Kind (TagPattern("some", _)), Some (Kind (BinaryOp("and", _, Kind (BinaryOp(">", _, _, _)), Kind (BinaryOp("<", _, _, _))))))), _) -> ()
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``is binds tighter than and and or`` () =
    match (valueOf "def x = a and b is #some(v) and v or c").Kind with
    | BinaryOp("or", _, Kind (BinaryOp("and", _, Kind (IdentifierExpr "a"), Kind (IsSyntax.Test(Kind (IdentifierExpr "b"), _, Some (Kind (IdentifierExpr "v")))))), Kind (IdentifierExpr "c")) -> ()
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``is binds looser than equality, comparisons and pipes`` () =
    match (valueOf "def x = a == b is #true").Kind with
    | IsSyntax.Test(Kind (BinaryOp("==", _, _, _)), _, None) -> ()
    | other -> failwith $"Unexpected parse: %A{other}"
    match (valueOf "def x = xs \\list.head is #some(h)").Kind with
    | IsSyntax.Test(Kind (Pipe(_, "list.head", _, _)), _, None) -> ()
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``a match written in the source is not a test`` () =
    match (valueOf "def x = match a\n  | #b -> true\n  | _ -> false\n").Kind with
    | IsSyntax.Test _ -> failwith "A written match was read as a test"
    | Match _ -> ()
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``an if whose condition is a chain is an if, which binds the chain's names in its then branch`` () =
    match (valueOf "def x = if a is #some(v) and v > 1 then v else 0").Kind with
    | IsSyntax.BindingIf(Match([ _ ], [ [ Kind (TagPattern(tag, Some (Kind (IdentifierPattern "v")))) ], Kind (IdentifierExpr "v"); [ Kind WildcardPattern ], _ ])) when
        tag = IsSyntax.heldTag -> ()
    | other -> failwith $"Unexpected parse: %A{other}"
    // A chain that binds nothing is an if of a bool.
    match (valueOf "def x = if a is #some(_) and b then 1 else 0").Kind with
    | IsSyntax.BindingIf _ -> failwith "A chain that binds nothing was made a match"
    | IfExpr(Kind (IsSyntax.Test(_, _, Some _)), _, _) -> ()
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``not binds tighter than is, and says how to negate a test`` () =
    let message, at = parseErrorOf "def f = { x -> not x is #a }\n"
    Assert.Equal("`not` binds tighter than `is`: write `not (x is p)` to negate the test", message)
    Assert.StartsWith("not x is", at)
    match (valueOf "def f = { x -> not (x is #a) }").Kind with
    | Lambda(_, Kind (FunctionCall("not", _, [ Kind (IsSyntax.Test _) ])), _) -> ()
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``only a looser operator may follow a test's pattern, and is does not chain`` () =
    let message, at = parseErrorOf "def f = { x -> x is v == 1 }\n"
    Assert.Equal("`==` binds tighter than `is`, so it can't follow a pattern: put the `is` test in parentheses", message)
    Assert.StartsWith("==", at)
    let message, _ = parseErrorOf "def f = { x -> x is a is b }\n"
    Assert.Equal("`is` and `is` cannot be written side by side: put one of them in parentheses", message)
