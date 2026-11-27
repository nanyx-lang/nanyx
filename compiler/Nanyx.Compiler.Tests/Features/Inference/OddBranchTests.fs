/// A `match`'s arm, an `if`'s branch or a list literal's item whose type doesn't agree with those before it is reported
/// at it, saying what it gives and what the others give (#2688).
module Nanyx.Compiler.Tests.Features.Inference.OddBranches

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The one diagnostic of `source`, which must be a type mismatch: its message and the text it is reported at.
let private mismatch (source: string) =
    let code, message, at = onlyDiagnostic (withFinalNewline source)
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    message, at

[<Fact>]
let ``A match's arm of another type is reported at its body`` () =
    let source = "def describe = { n: int ->\n  match n\n    | 0 -> \"zero\"\n    | 1 -> \"one\"\n    | 2 -> 2\n    | _ -> \"many\"\n}"
    Assert.Equal(("This arm gives 'int', but the arms before it give 'string'", "2"), mismatch source)

[<Fact>]
let ``A match's arm of another type is reported at it where the result's type is written`` () =
    let source = "def label: int -> string = { n ->\n  match n\n    | 0 -> \"zero\"\n    | _ -> 2\n}"
    Assert.Equal(("This arm gives 'int', but the arms before it give 'string'", "2"), mismatch source)

[<Fact>]
let ``An if's else branch of another type is reported at it`` () =
    Assert.Equal(
        ("The `else` branch gives 'int', but the `then` branch gives 'string'", "0"),
        mismatch "def pick = { b: bool -> if b then \"yes\" else 0 }")

[<Fact>]
let ``A list literal's item of another type is reported at it`` () =
    Assert.Equal(("This item is 'int', but the items before it are 'string'", "3"), mismatch "def xs = [\"a\", \"b\", 3, \"d\"]")

[<Fact>]
let ``Arms of different tags are joined, not reported`` () =
    compileClean "def tags = { n: int ->\n  match n\n    | 0 -> #zero\n    | _ -> #many\n}" |> ignore

/// The related locations of the one diagnostic of `source`: the text each is at, and its label.
let private related (source: string) =
    let source = withFinalNewline source
    let diagnostic = Assert.Single((Compiler.compile source).Diagnostics)
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, diagnostic.Code)
    diagnostic.Related |> List.map (fun (span, label) -> textAt source span, label)

[<Fact>]
let ``Each arm before the odd one out is a related location, saying what it gives`` () =
    // The places that explain the error (#2717).
    let source = "def describe = { n: int ->\n  match n\n    | 0 -> \"zero\"\n    | 1 -> \"one\"\n    | 2 -> 2\n    | _ -> \"many\"\n}"
    Assert.Equal<(string * string) list>(
        [ "\"zero\"", "this arm gives 'string'"; "\"one\"", "this arm gives 'string'" ],
        related source)

[<Fact>]
let ``An if's then branch is the related location of its else branch, and a list's items before the odd one of it`` () =
    Assert.Equal<(string * string) list>(
        [ "\"yes\"", "the `then` branch gives 'string'" ],
        related "def pick = { b: bool -> if b then \"yes\" else 0 }")
    Assert.Equal<(string * string) list>(
        [ "\"a\"", "this item is 'string'"; "\"b\"", "this item is 'string'" ],
        related "def xs = [\"a\", \"b\", 3, \"d\"]")
