module Nanyx.Compiler.Tests.Features.NameSuggestions

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The one diagnostic `source` compiles to: its code and message, with the title of each fix and the source it gives.
let private suggested (source: string) =
    let source = source.ReplaceLineEndings "\n"
    match (Compiler.compile source).Diagnostics with
    | [ diagnostic ] -> diagnostic.Code, diagnostic.Message, diagnostic.Fixes |> List.map (fun fix -> fix.Title, applyFix source fix)
    | diagnostics -> failwith $"Expected one diagnostic, got %A{diagnostics}"

[<Fact>]
let ``A misspelt name offers the name in scope it is closest to`` () =
    let source =
        """
def total = 3
def n = totl + 1
"""
    let fixedSource =
        """
def total = 3
def n = total + 1
"""
    Assert.Equal(
        (DiagnosticCodes.UnknownIdentifier.Code,
         "Unknown identifier 'totl': did you mean 'total'?",
         [ "Write `total`", fixedSource.ReplaceLineEndings "\n" ]),
        suggested source)
    compileClean fixedSource |> ignore

[<Fact>]
let ``A misspelt function offers the function, with two letters swapped counted as one edit`` () =
    let source =
        """
def double: int -> int = { x -> x * 2 }
def n = duoble(3)
"""
    let fixedSource =
        """
def double: int -> int = { x -> x * 2 }
def n = double(3)
"""
    Assert.Equal(
        (DiagnosticCodes.UnknownFunction.Code,
         "Unknown function 'duoble': did you mean 'double'?",
         [ "Write `double`", fixedSource.ReplaceLineEndings "\n" ]),
        suggested source)
    compileClean fixedSource |> ignore

[<Fact>]
let ``A parameter is a name in scope`` () =
    let source =
        """
def f: int -> int = { count -> cuont + 1 }
"""
    let fixedSource =
        """
def f: int -> int = { count -> count + 1 }
"""
    Assert.Equal(
        (DiagnosticCodes.UnknownIdentifier.Code,
         "Unknown identifier 'cuont': did you mean 'count'?",
         [ "Write `count`", fixedSource.ReplaceLineEndings "\n" ]),
        suggested source)
    compileClean fixedSource |> ignore

[<Fact>]
let ``Names equally close are all offered, in order`` () =
    let source =
        """
def alpha1 = 1
def alpha2 = 2
def n = alpha3
"""
    let code, message, fixes = suggested source
    Assert.Equal(DiagnosticCodes.UnknownIdentifier.Code, code)
    Assert.Equal("Unknown identifier 'alpha3': did you mean 'alpha1' or 'alpha2'?", message)
    Assert.Equal<string list>([ "Write `alpha1`"; "Write `alpha2`" ], fixes |> List.map fst)

[<Fact>]
let ``A name far from every name in scope offers nothing`` () =
    let code, message, fixes = suggested "def n = frobnicate(1)\n"
    Assert.Equal(DiagnosticCodes.UnknownFunction.Code, code)
    Assert.Equal("Unknown function 'frobnicate'", message)
    Assert.Empty fixes

[<Fact>]
let ``The threshold grows with the name's length`` () =
    Assert.Equal<string list>([ "abc" ], NameSuggestions.closest [ "abc" ] "abd")
    Assert.Empty(NameSuggestions.closest [ "abc" ] "axy")
    Assert.Equal<string list>([ "position" ], NameSuggestions.closest [ "position" ] "posiiton")

[<Fact>]
let ``A dotted name is offered names with the same qualifier`` () =
    Assert.Equal<string list>([ "string.trim" ], NameSuggestions.closest [ "string.trim"; "list.trim"; "trimm" ] "string.trimm")

[<Fact>]
let ``A name the compiler reserves is never offered`` () =
    Assert.Empty(NameSuggestions.closest [ "__nanyx_exit"; "__for" ] "nanyx_exit")
    Assert.Empty(NameSuggestions.closest [ "__nanyx_exit"; "__for" ] "_for")
