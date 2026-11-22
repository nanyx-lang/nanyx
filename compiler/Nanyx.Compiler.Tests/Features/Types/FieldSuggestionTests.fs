/// A field a record doesn't have, read, updated or written in a record value, offers the record's field closest to it
/// (#2078).
module Nanyx.Compiler.Tests.Features.FieldSuggestions

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
let ``A misspelt field read with a dot offers the record's field`` () =
    let source =
        """
def p = (name = "x", age = 3)
def q = p.nmae
"""
    let fixedSource =
        """
def p = (name = "x", age = 3)
def q = p.name
"""
    Assert.Equal(
        (DiagnosticCodes.UnknownMember.Code,
         "The record 'p' has no member 'nmae': did you mean 'name'?",
         [ "Write `name`", fixedSource.ReplaceLineEndings "\n" ]),
        suggested source)
    compileClean fixedSource |> ignore

[<Fact>]
let ``A field no field of the record is close to offers nothing`` () =
    let source =
        """
def p = (name = "x", age = 3)
def q = p.zzz
"""
    Assert.Equal((DiagnosticCodes.UnknownMember.Code, "The record 'p' has no member 'zzz'", []), suggested source)

[<Fact>]
let ``A misspelt field updated with with offers the record's field, at the field's name`` () =
    let source =
        """
def p = (name = "x", age = 3)
def q = p with (nmae = "y")
"""
    let fixedSource =
        """
def p = (name = "x", age = 3)
def q = p with (name = "y")
"""
    Assert.Equal(
        (DiagnosticCodes.UnknownMember.Code,
         "The record has no member 'nmae' to update: did you mean 'name'?",
         [ "Write `name`", fixedSource.ReplaceLineEndings "\n" ]),
        suggested source)
    compileClean fixedSource |> ignore

[<Fact>]
let ``A misspelt field in a record value given for a record type offers the type's field`` () =
    let source =
        """
type P = (first: string, last: string)
def r: P = (first = "a", lsat = "b")
"""
    let fixedSource =
        """
type P = (first: string, last: string)
def r: P = (first = "a", last = "b")
"""
    Assert.Equal(
        (DiagnosticCodes.TypeMismatch.Code,
         "'lsat' is not a field of P: did you mean 'last'?",
         [ "Write `last`", fixedSource.ReplaceLineEndings "\n" ]),
        suggested source)
    compileClean fixedSource |> ignore

[<Fact>]
let ``A misspelt field in a record value passed to a function offers the parameter's field`` () =
    let source =
        """
type P = (first: string, last: string)
def f: P -> P = { x -> x }
def s = f((first = "a", lsat = "b"))
"""
    let fixedSource =
        """
type P = (first: string, last: string)
def f: P -> P = { x -> x }
def s = f((first = "a", last = "b"))
"""
    Assert.Equal(
        (DiagnosticCodes.TypeMismatch.Code,
         "'lsat' is not a field of P: did you mean 'last'?",
         [ "Write `last`", fixedSource.ReplaceLineEndings "\n" ]),
        suggested source)
    compileClean fixedSource |> ignore

[<Fact>]
let ``Records that differ in fields with names far apart keep the record field mismatch`` () =
    let source =
        """
type P = (first: string, last: string)
def r: P = (first = "a", zzz = "b")
"""
    let code, message, fixes = suggested source
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.StartsWith("Record field mismatch:", message)
    Assert.Empty fixes
