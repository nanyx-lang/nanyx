module ParserTests.Features.Tuples

open Xunit
open FsUnit.Xunit
open Parser.Program
open ParserTestHelpers

[<Fact>]
let ``def (x, y) desugars into a whole-value binding and one def per element`` () =
    match parseModule "def (x, _, y) = point" with
    | Result.Ok [ Kind (Def (ValueDef (_, temp, None, Kind (IdentifierExpr "point"))))
                  Kind (Def (ValueDef (_, "x", None, Kind (MemberAccess (Kind (IdentifierExpr temp1), "0", None)))))
                  Kind (Def (ValueDef (_, "y", None, Kind (MemberAccess (Kind (IdentifierExpr temp2), "2", None))))) ] ->
        temp1 |> should equal temp
        temp2 |> should equal temp
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``def x, y is def (x, y) without the brackets`` () =
    match parseModule "def x, _, y = point" with
    | Result.Ok [ Kind (Def (ValueDef (_, temp, None, Kind (IdentifierExpr "point"))))
                  Kind (Def (ValueDef (_, "x", None, Kind (MemberAccess (Kind (IdentifierExpr temp1), "0", None)))))
                  Kind (Def (ValueDef (_, "y", None, Kind (MemberAccess (Kind (IdentifierExpr temp2), "2", None))))) ] ->
        temp1 |> should equal temp
        temp2 |> should equal temp
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"
    // An element may itself be a bracketed tuple.
    match parseModule "def main = {\n  def a, (b, c) = v\n  a\n}" with
    | Result.Ok [ Kind (Def (ValueDef (_, "main", None, Kind (Lambda ([], Kind (Block [ Kind (DefStatement (_, _, None, Kind (IdentifierExpr "v")))
                                                                                        Kind (DefStatement (_, "a", None, Kind (MemberAccess (_, "0", None))))
                                                                                        Kind (DefStatement (_, "b", None, Kind (MemberAccess (Kind (MemberAccess (_, "1", None)), "0", None))))
                                                                                        Kind (DefStatement (_, "c", None, Kind (MemberAccess (Kind (MemberAccess (_, "1", None)), "1", None))))
                                                                                        Kind (ExprStatement (Kind (IdentifierExpr "a"))) ]), _))))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"
    // A pattern that may not match binds through a match, as with the brackets.
    match parseModule "def #some(x), y = pair" with
    | Result.Ok [ Kind (Def (ValueDef (_, _, None, Kind (Match (_, [ [ Kind (TuplePattern [ Kind (TagPattern ("some", _)); Kind (IdentifierPattern "y") ]) ], _ ])))))
                  Kind (Def (ValueDef (_, "x", None, _)))
                  Kind (Def (ValueDef (_, "y", None, _))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``def with a nested tuple pattern destructures through member access chains`` () =
    match parseModule "def main = {\n  def (a, (b, c)) = v\n  a\n}" with
    | Result.Ok [ Kind (Def (ValueDef (_, "main", None, Kind (Lambda ([], Kind (Block [ Kind (DefStatement (_, _, None, Kind (IdentifierExpr "v")))
                                                                                        Kind (DefStatement (_, "a", None, Kind (MemberAccess (_, "0", None))))
                                                                                        Kind (DefStatement (_, "b", None, Kind (MemberAccess (Kind (MemberAccess (_, "1", None)), "0", None))))
                                                                                        Kind (DefStatement (_, "c", None, Kind (MemberAccess (Kind (MemberAccess (_, "1", None)), "1", None))))
                                                                                        Kind (ExprStatement (Kind (IdentifierExpr "a"))) ]), _))))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``def with a pattern that may not match binds through a one-arm match`` () =
    // `def $Money(pence) = m` is `def __tuple = match m | $Money(pence) -> pence` and `def pence = __tuple`;
    // whether the pattern is total is the type checker's to say.
    match parseModule "def $Money(pence) = m" with
    | Result.Ok [ Kind (Def (ValueDef (_, temp, None, Kind (Match ([ Kind (IdentifierExpr "m") ],
                                                              [ [ Kind (RecordPattern ("$Money", [ Kind (IdentifierPattern "pence") ])) ], Kind (IdentifierExpr "pence") ])))))
                  Kind (Def (ValueDef (_, "pence", None, Kind (IdentifierExpr temp1)))) ] ->
        temp1 |> should equal temp
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"
    // Several names come back as a tuple, one def per element.
    match parseModule "def [first, ...rest] = xs" with
    | Result.Ok [ Kind (Def (ValueDef (_, temp, None, Kind (Match ([ Kind (IdentifierExpr "xs") ],
                                                              [ [ Kind (ListPattern ([ Kind (IdentifierPattern "first") ], Some (Kind (IdentifierPattern "rest")))) ],
                                                                Kind (TupleExpr [ Kind (IdentifierExpr "first"); Kind (IdentifierExpr "rest") ]) ])))))
                  Kind (Def (ValueDef (_, "first", None, Kind (MemberAccess (Kind (IdentifierExpr temp1), "0", None)))))
                  Kind (Def (ValueDef (_, "rest", None, Kind (MemberAccess (Kind (IdentifierExpr temp2), "1", None))))) ] ->
        temp1 |> should equal temp
        temp2 |> should equal temp
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"
    // A literal in a tuple pattern takes the same route, and a pattern binding no name is only the match.
    match parseModule "def (1, y) = point" with
    | Result.Ok [ Kind (Def (ValueDef (_, _, None, Kind (Match (_, [ [ Kind (TuplePattern [ Kind (LiteralPattern (IntLit 1)); Kind (IdentifierPattern "y") ]) ], Kind (IdentifierExpr "y") ])))))
                  Kind (Def (ValueDef (_, "y", None, Kind (IdentifierExpr _)))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"
    match parseModule "def #none = value" with
    | Result.Ok [ Kind (Def (ValueDef (_, _, None, Kind (Match (_, [ [ Kind (TagPattern ("none", None)) ], Kind UnitExpr ]))))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``def with a bare name is an ordinary def, not a destructuring one`` () =
    match parseModule "def x = 1" with
    | Result.Ok [ Kind (Def (ValueDef (_, "x", None, Kind (LiteralExpr (IntLit 1))))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"
    match parseModule "def $Money.new: int -> $Money = { pence -> $Money(pence) }" with
    | Result.Ok [ Kind (Def (ValueDef (_, "$Money.new", Some _, Kind (Lambda _)))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``Parse tuple feature files`` () =
    parseFeatureNyxFiles "Tuples"
    |> Array.iter (fun (filePath, result) ->
        result |> isOk |> should equal true
        match result with
        | Result.Error err -> failwith $"Parse failed for {filePath}: {err}"
        | _ -> ()
    )
