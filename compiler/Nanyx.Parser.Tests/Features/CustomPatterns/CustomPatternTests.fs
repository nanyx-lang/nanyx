module ParserTests.Features.CustomPatterns

open Xunit
open Parser.Program
open ParserTestHelpers

/// The one item of `source`, which parses.
let private itemOf (source: string) =
    match parseModule (source.ReplaceLineEndings "\n") with
    | Result.Ok [ item ] -> item
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

/// The patterns of each arm of `source`'s one def, a lambda that matches.
let private armsOf (source: string) =
    match itemOf source with
    | Kind (Def (ValueDef (_, _, _, Kind (Lambda (_, Kind (Match (_, arms)), _))))) -> arms |> List.map fst
    | other -> failwith $"Unexpected parse: %A{other}"

[<Fact>]
let ``a total pattern is a def of its function, named for its tags`` () =
    let source = """
pattern int -> #Even | #Odd = { n ->
  if n % 2 == 0 then #Even else #Odd
}
"""
    match itemOf source with
    | Kind (Def (PatternSyntax.Declaration (signature, Kind (Lambda _)) & ValueDef (false, name, _, _))) ->
        Assert.Equal(Some ({ Tags = [ "Even"; "Odd" ]; WithPayload = []; TakesArguments = false; IsPartial = false; ReturnsBool = false }: PatternSyntax.Shape), PatternSyntax.shapeOf name)
        Assert.Equal(TypeName "int", signature.Input.Kind)
        Assert.Equal<Identifier list>([ "Even"; "Odd" ], signature.Results |> List.map fst)
    | other -> failwith $"Unexpected item: %A{other}"

[<Fact>]
let ``a partial pattern has _ among its tags, and one with one tag and no payload returns a bool`` () =
    match itemOf "export pattern int -> #Zero | _ = { n -> n == 0 }" with
    | Kind (Def (PatternSyntax.Declaration (signature, _) & ValueDef (true, name, _, _))) ->
        Assert.True signature.IsPartial
        Assert.Equal(Some ({ Tags = [ "Zero" ]; WithPayload = []; TakesArguments = false; IsPartial = true; ReturnsBool = true }: PatternSyntax.Shape), PatternSyntax.shapeOf name)
    | other -> failwith $"Unexpected item: %A{other}"

[<Fact>]
let ``_ is no match in a pattern's body`` () =
    let source = """
pattern string -> #ParseInt(int) | _ = { s ->
  match int.fromString(s)
    | #some(i) -> #ParseInt(i)
    | #none -> _
}
"""
    match itemOf source with
    | Kind (Def (PatternSyntax.Declaration (signature, Kind (Lambda (_, Kind (Block [ Kind (ExprStatement (Kind (Match (_, [ _; _, lastArm ])))) ]), _))) & ValueDef (_, name, _, _))) ->
        Assert.Equal(IdentifierExpr PatternSyntax.noMatch, lastArm.Kind)
        Assert.Equal(Some (TypeName "int"), signature.Results |> List.head |> snd |> Option.map _.Kind)
        Assert.False (PatternSyntax.shapeOf name).Value.ReturnsBool
        Assert.Equal<Identifier list>([ "ParseInt" ], (PatternSyntax.shapeOf name).Value.WithPayload)
    | other -> failwith $"Unexpected item: %A{other}"

[<Fact>]
let ``a pattern with arguments takes them before its input`` () =
    match itemOf "pattern string -> string -> #SplitOn(string, string) | _ = { separator -> { s -> _ } }" with
    | Kind (Def (PatternSyntax.Declaration (signature, _) & ValueDef (_, name, _, _))) ->
        Assert.Equal(Some (TypeName "string"), signature.Arguments |> Option.map _.Kind)
        Assert.Equal(TypeName "string", signature.Input.Kind)
        Assert.True (PatternSyntax.shapeOf name).Value.TakesArguments
    | other -> failwith $"Unexpected item: %A{other}"

[<Fact>]
let ``a pattern's def name gives back its shape whatever its tags are made of`` () =
    // A tag that ends in `p` is read whole, and the mark after a tag that carries a payload is no tag's.
    let shape = ({ Tags = [ "A1"; "Bp"; "C_2"; "D" ]; WithPayload = [ "Bp"; "D" ]; TakesArguments = true; IsPartial = true; ReturnsBool = false }: PatternSyntax.Shape)
    Assert.Equal(Some shape, PatternSyntax.shapeOf (PatternSyntax.defName shape))
    Assert.Equal(Some shape, PatternSyntax.shapeOf ("m." + PatternSyntax.defName shape))
    Assert.Equal(None, PatternSyntax.shapeOf "pattern")

[<Fact>]
let ``a pattern's type ends in its tags`` () =
    let reported, _ = parseErrorOf "pattern int -> string = { n -> \"x\" }"
    Assert.Contains("A pattern's type ends in the tags it returns", reported)

[<Fact>]
let ``a pattern's tags are capitalised`` () =
    let reported, _ = parseErrorOf "pattern int -> #even | #odd = { n -> #even }"
    Assert.Contains("A pattern's tags are capitalised", reported)
    Assert.Contains("`#Even`", reported)

[<Fact>]
let ``pattern is reserved`` () =
    Assert.Contains("pattern", Keywords.reserved)
    Assert.Equal(Lexer.Keyword, (Lexer.lex "t.nyx" "pattern").Tokens[0].Kind)

[<Fact>]
let ``a pattern tag's one group is its payload when it is a pattern, and its arguments when it is only an expression`` () =
    match armsOf "def f = { | #Hsl(h, s, l) -> 1 | #DivisibleBy(3) -> 2 | #DivisibleBy(n + 1) -> 3 | #Even -> 4 }" with
    | [ [ Kind (TagPattern ("Hsl", Some (Kind (TuplePattern([ _; _; _ ], false))))) ]
        [ Kind (TagPattern ("DivisibleBy", Some (Kind (LiteralPattern (IntLit64 3L))))) ]
        [ Kind (ArgumentTagPattern ("DivisibleBy", Kind (BinaryOp ("+", _, _, _)), None)) ]
        [ Kind (TagPattern ("Even", None)) ] ] -> ()
    | other -> failwith $"Unexpected arms: %A{other}"

[<Fact>]
let ``a pattern tag with two groups takes its arguments, then its payload`` () =
    match armsOf "def f = { | #SplitOn(\",\")(first, second) -> 1 | #Between(1, 2)(n) -> 2 | #DivisibleBy(3) & #DivisibleBy(5) -> 3 }" with
    | [ [ Kind (ArgumentTagPattern ("SplitOn", Kind (LiteralExpr (StringLit ",")), Some (Kind (TuplePattern([ Kind (IdentifierPattern "first"); Kind (IdentifierPattern "second") ], false))))) ]
        [ Kind (ArgumentTagPattern ("Between", Kind (TupleExpr [ _; _ ]), Some (Kind (IdentifierPattern "n")))) ]
        [ Kind (AndPattern [ Kind (TagPattern ("DivisibleBy", Some _)); Kind (TagPattern ("DivisibleBy", Some _)) ]) ] ] -> ()
    | other -> failwith $"Unexpected arms: %A{other}"

[<Fact>]
let ``a lower-case tag never takes arguments`` () =
    // `#split(",")` is read as a tag with a payload, so its second group is where an arm's `->` belongs.
    let reported, text = parseErrorOf "def f = { | #split(\",\")(a, b) -> 1 }"
    Assert.Equal("Expected `,` or `->`, found `(`", reported)
    Assert.Equal("(a, b) -> 1 }", text)
