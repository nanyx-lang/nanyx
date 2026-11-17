module Nanyx.Compiler.Tests.Features.Diagnostics.LookAlikes

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A character that looks like one Nanyx reads but isn't, such as a minus sign for `-`, pasted from a document: named,
// with a fix that writes what it looks like, in place of the generic syntax error.

let private minus = "−"

/// Each diagnostic compiling `source` gives: its code, its message and the text it is reported at.
let private diagnosticsOf (source: string) =
    let source = normalized source
    (Compiler.compile source).Diagnostics |> List.map (fun diagnostic -> diagnostic.Code, diagnostic.Message, textAt source (spanOf diagnostic))

/// `source` with the fixes of each of its diagnostics made.
let private allFixed (source: string) =
    let source = normalized source
    (Compiler.compile source).Diagnostics |> List.collect (fun diagnostic -> diagnostic.Fixes |> List.collect _.Edits) |> applyEdits source

let private code = DiagnosticCodes.LookAlikeCharacter.Code

[<Fact>]
let ``A minus sign is named where it is written, with a fix that writes a hyphen-minus`` () =
    let source = $"def expected = {minus}7.0\n"
    Assert.Equal<(string * string * string) list>(
        [ code, "'−' (U+2212 Minus Sign) looks like `-` (Hyphen-Minus), but it isn't one: write `-`", minus ],
        diagnosticsOf source)
    let fix = (Compiler.compile source).Diagnostics.Head.Fixes |> Assert.Single
    Assert.Equal("Replace '−' with '-'", fix.Title)
    Assert.Equal("def expected = -7.0\n", applyFix source fix)

[<Fact>]
let ``Curly quotes around a string are one diagnostic, whose fix writes both`` () =
    let source = "def greeting = “hello”\n"
    match diagnosticsOf source with
    | [ found, message, at ] ->
        Assert.Equal((code, "“"), (found, at))
        Assert.StartsWith("'“' (U+201C Left Double Quotation Mark) (and its pair, '”') looks like `\"`", message)
    | other -> failwithf "expected one diagnostic, got %A" other
    Assert.Equal("def greeting = \"hello\"\n", allFixed source)
    Assert.Empty((Compiler.compile (allFixed source)).Diagnostics)

[<Fact>]
let ``A no-break space is named, and its fix writes a space`` () =
    let source = "def total = 1 + 2\n"
    Assert.Equal<(string * string * string) list>(
        [ code, "U+00A0 (No-Break Space) looks like a space, but it isn't one: write a space", " " ],
        diagnosticsOf source)
    Assert.Empty((Compiler.compile (allFixed source)).Diagnostics)

[<Fact>]
let ``A fullwidth bracket is named, and each one in turn`` () =
    let source = "def three = max（1, 3）\n"
    Assert.Equal<(string * string) list>(
        [ code, "（"; code, "）" ],
        diagnosticsOf source |> List.map (fun (found, _, at) -> found, at))
    Assert.Equal("def three = max(1, 3)\n", allFixed source)

[<Fact>]
let ``A look-alike doesn't hide a syntax error further down`` () =
    let source =
        normalized
            $"""def expected = {minus}7.0

def broken = (1 +
"""
    match diagnosticsOf source with
    | [ (first, _, at); (second, _, _) ] -> Assert.Equal((code, minus, DiagnosticCodes.SyntaxError.Code), (first, at, second))
    | other -> failwithf "expected the look-alike and the syntax error, got %A" other

[<Fact>]
let ``The same characters in a string and a comment are left alone`` () =
    Assert.Empty((Compiler.compile $"def text = \"{minus}7 “quoted”  \" -- {minus}7.0  \n").Diagnostics)
