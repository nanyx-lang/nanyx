module ParserTests.Features.RecordPatterns

open Xunit
open Parser.Program
open ParserTestHelpers

/// The patterns of each arm of `source`'s one def, a lambda that matches.
let private armsOf (source: string) =
    match parseModule source with
    | Result.Ok [ Kind (Def (ValueDef (_, _, _, Kind (Lambda (_, Kind (Match (_, arms)), _))))) ] -> arms |> List.map fst
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``a record pattern by member name may end in ... to leave the other fields out`` () =
    match armsOf "def f = { | (a = 1, ...) -> 1 | (a = 1, b = x , ... ) -> 2 | (a = 1, b = _) -> 3 }" with
    | [ [ Kind (RecordMemberPattern ([ "a", Kind (LiteralPattern (IntLit64 1L)) ], true)) ]
        [ Kind (RecordMemberPattern ([ "a", _; "b", Kind (IdentifierPattern "x") ], true)) ]
        [ Kind (RecordMemberPattern ([ "a", _; "b", Kind WildcardPattern ], false)) ] ] -> ()
    | other -> failwith $"Unexpected arms: %A{other}"

[<Fact>]
let ``... in a record pattern comes after its members`` () =
    match parseModule "def f = { | (..., a = 1) -> 1 }" with
    | Result.Error _ -> ()
    | Result.Ok parsed -> failwith $"Expected a parse error: %A{parsed}"
    match parseModule "def f = { | (...) -> 1 }" with
    | Result.Error _ -> ()
    | Result.Ok parsed -> failwith $"Expected a parse error: %A{parsed}"
