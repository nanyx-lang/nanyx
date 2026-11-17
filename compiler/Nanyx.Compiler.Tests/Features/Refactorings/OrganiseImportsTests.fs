module Nanyx.Compiler.Tests.Features.Refactorings.OrganiseImports

open Xunit
open NyxCompiler
open NyxCompiler.Refactorings
open Nanyx.Formatter
open Nanyx.Compiler.Tests.TestSupport

// Organise imports: remove the unused ones, merge and sort the rest.

let private offersFor (source: string) =
    let source = normalized source
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    source, OrganiseImports.at Formatter.Options.defaults "" source result Compiler.compile |> List.map Offer.decided

/// The text after organising imports, checked to compile and be formatted.
let private organised (source: string) =
    match offersFor source with
    | source, [ Available refactoring ] ->
        Assert.Equal(RefactoringKind.OrganiseImports, refactoring.Kind)
        let after = applyEdits source refactoring.Edits
        let result = Compiler.compile after
        Assert.True(result.Diagnostics.IsEmpty, sprintf "%s\n%A" after result.Diagnostics)
        match FormatCommand.formatText Formatter.Options.defaults "" after with
        | FormatCommand.Formatting.Formatted formatted -> Assert.Equal(formatted, after)
        | other -> failwithf "%A" other
        after
    | _, offers -> failwithf "expected one offer, got %A" offers

[<Fact>]
let ``An import nothing uses is removed`` () =
    Assert.Equal(
        normalized
            """import nanyx/math as m

def x = m.abs(3)
""",
        organised
            """import (
  nanyx/math as m
  nanyx/text as t
)

def x = m.abs(3)
""")

[<Fact>]
let ``A name of a selection nothing uses is removed`` () =
    Assert.Equal(
        normalized
            """import nanyx/math as (abs)

def x = abs(3)
""",
        organised
            """import nanyx/math as (abs, max)

def x = abs(3)
""")

[<Fact>]
let ``Imports are merged into one and sorted`` () =
    Assert.Equal(
        normalized
            """import (
  nanyx/math as m
  nanyx/text
)

def x = m.abs(3)

def y = "abc" \take(1)
""",
        organised
            """import nanyx/text

def x = m.abs(3)

import nanyx/math as m

def y = "abc" \take(1)
""")

[<Fact>]
let ``The only import nothing uses is removed with its line`` () =
    Assert.Equal(
        normalized
            """def x = 3
""",
        organised
            """import nanyx/math as m

def x = 3
""")

[<Fact>]
let ``Nothing is offered when the imports are organised`` () =
    Assert.Empty(snd (offersFor "import nanyx/math as m\n\ndef x = m.abs(3)\n"))

[<Fact>]
let ``Imports with comments among them are refused`` () =
    match snd (offersFor "import (\n  -- for abs\n  nanyx/math as m\n  nanyx/text as t\n)\n\ndef x = m.abs(3)\n") with
    | [ Refused(_, RefactoringKind.OrganiseImports, reason) ] -> Assert.Equal("The imports have comments among them, which organising would lose", reason)
    | offers -> failwithf "expected a refusal, got %A" offers
