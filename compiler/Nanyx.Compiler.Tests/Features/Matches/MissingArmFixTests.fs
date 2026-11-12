module Nanyx.Compiler.Tests.Features.MissingArmFixes

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

let private normalised (source: string) = source.ReplaceLineEndings "\n"

/// The one diagnostic `source` compiles to, which must be a non-exhaustive match: the title of each fix, and the
/// source it gives.
let private fixesFor (source: string) =
    let source = normalised source
    match (Compiler.compile source).Diagnostics with
    | [ diagnostic ] ->
        Assert.Equal(DiagnosticCodes.NonExhaustiveMatch.Code, diagnostic.Code)
        diagnostic.Fixes |> List.map (fun fix -> fix.Title, applyFix source fix)
    | diagnostics -> failwith $"Expected one diagnostic, got %A{diagnostics}"

/// The codes of the diagnostics `source` compiles to.
let private codesOf (source: string) =
    (Compiler.compile (normalised source)).Diagnostics |> List.map _.Code |> List.distinct

[<Fact>]
let ``The missing tags are added as arms, indented as the last arm, with ??? for a body`` () =
    let source =
        """
def describe: #red | #green | #blue -> string = { colour ->
  match colour
    | #red -> "red"
}
"""
    let fixedSource =
        """
def describe: #red | #green | #blue -> string = { colour ->
  match colour
    | #red -> "red"
    | #blue -> ???
    | #green -> ???
}
"""
    Assert.Equal<(string * string) list>([ "Add the missing arms", normalised fixedSource ], fixesFor source)
    // `???` is all that is left to write.
    Assert.Equal<string list>([ DiagnosticCodes.NotImplemented.Code ], codesOf fixedSource)

[<Fact>]
let ``A tag with a payload is added with a catch-all for it`` () =
    let source =
        """
def value: #some(int) | #none = #none
def result = match value
| #none -> 0
"""
    let fixedSource =
        """
def value: #some(int) | #none = #none
def result = match value
| #none -> 0
| #some(_) -> ???
"""
    Assert.Equal<(string * string) list>([ "Add the missing arm", normalised fixedSource ], fixesFor source)
    Assert.Equal<string list>([ DiagnosticCodes.NotImplemented.Code ], codesOf fixedSource)

[<Fact>]
let ``Ints, strings and floats get a catch-all arm`` () =
    let source =
        """
def name: int -> string = { n ->
  match n
    | 0 -> "zero"
    | 1 -> "one"
}
"""
    let fixedSource =
        """
def name: int -> string = { n ->
  match n
    | 0 -> "zero"
    | 1 -> "one"
    | _ -> ???
}
"""
    Assert.Equal<(string * string) list>([ "Add the missing arm", normalised fixedSource ], fixesFor source)

[<Fact>]
let ``A missing case inside a tag's payload is added with the tag`` () =
    let source =
        """
def f: #some(bool) | #none -> int = { o ->
  match o
    | #some(true) -> 1
    | #none -> 0
}
"""
    let fixedSource =
        """
def f: #some(bool) | #none -> int = { o ->
  match o
    | #some(true) -> 1
    | #none -> 0
    | #some(false) -> ???
}
"""
    Assert.Equal<(string * string) list>([ "Add the missing arm", normalised fixedSource ], fixesFor source)
    Assert.Equal<string list>([ DiagnosticCodes.NotImplemented.Code ], codesOf fixedSource)

[<Fact>]
let ``A list match gets the list cases it is missing`` () =
    let source =
        """
def first: list(int) -> int = { items ->
  match items
    | [ x, ... ] -> x
}
"""
    let fixedSource =
        """
def first: list(int) -> int = { items ->
  match items
    | [ x, ... ] -> x
    | [] -> ???
}
"""
    Assert.Equal<(string * string) list>([ "Add the missing arm", normalised fixedSource ], fixesFor source)
    Assert.Equal<string list>([ DiagnosticCodes.NotImplemented.Code ], codesOf fixedSource)

[<Fact>]
let ``A match on several values gets an arm for each combination it is missing`` () =
    let source =
        """
def both: bool, bool -> int = { a, b ->
  match a, b
    | true, true -> 1
    | false, _ -> 0
}
"""
    let fixedSource =
        """
def both: bool, bool -> int = { a, b ->
  match a, b
    | true, true -> 1
    | false, _ -> 0
    | true, false -> ???
}
"""
    Assert.Equal<(string * string) list>([ "Add the missing arm", normalised fixedSource ], fixesFor source)
    Assert.Equal<string list>([ DiagnosticCodes.NotImplemented.Code ], codesOf fixedSource)

[<Fact>]
let ``Arms written on the match's line are added on it`` () =
    let source = "def f: #a | #b -> int = { x -> match x | #a -> 1 }\n"
    Assert.Equal<(string * string) list>(
        [ "Add the missing arm", "def f: #a | #b -> int = { x -> match x | #a -> 1 | #b -> ??? }\n" ],
        fixesFor source)

[<Fact>]
let ``A lambda's pattern parameter, which has no arms to add to, gets no fix`` () =
    Assert.Empty(fixesFor "def f: #a | #b -> int = { #a -> 1 }\n")