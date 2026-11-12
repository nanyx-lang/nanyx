module ParserTests.Features.RecordSpread

open Xunit
open Parser.Program
open ParserTestHelpers

[<Fact>]
let ``a record with a spread is a call to __spread with its parts in the order written`` () =
    match parseModule "def config = (...defaults, protocol = \"https\", ...env)" with
    | Result.Ok [ Kind (Def (ValueDef (_, "config", _, Kind (SpreadSyntax.Call [ SpreadRecord (Kind (IdentifierExpr "defaults")); SpreadField ("protocol", Kind (LiteralExpr (StringLit "https"))); SpreadRecord (Kind (IdentifierExpr "env")) ])))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``a record with a spread may be written one part per line`` () =
    let source =
        """
def config = (
  ...defaults
  protocol = "https"
  ...env
)
"""
    match parseModule (source.ReplaceLineEndings "\n") with
    | Result.Ok [ Kind (Def (ValueDef (_, "config", _, Kind (SpreadSyntax.Call [ SpreadRecord _; SpreadField ("protocol", _); SpreadRecord _ ])))) ] -> ()
    | Result.Ok other -> failwith $"Unexpected parse: %A{other}"
    | Result.Error err -> failwith $"Parse failed: {err}"

[<Fact>]
let ``a record with a spread names its other fields`` () =
    match parseModule "def config = (...defaults, 1)" with
    | Result.Error err -> Assert.Contains("a record with a spread names each of its other fields", err)
    | Result.Ok other -> failwith $"Expected a parse error: %A{other}"
