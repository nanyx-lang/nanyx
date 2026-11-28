module Nanyx.Compiler.Tests.Features.Linters.Allow

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The codes of the diagnostics of `source`, compiled with every linter on, each with the text it is reported at.
let private reported (source: string) =
    let source = source.ReplaceLineEndings "\n"
    let result = Compiler.compileWith (everyLinter ()) source
    result.Diagnostics
    |> List.map (fun diagnostic ->
        diagnostic.Code, diagnostic.Span |> Option.map (textAt source) |> Option.defaultValue "")

[<Fact>]
let ``An allow before a def suppresses the rule inside it`` () =
    let source =
        """@allow(default/unusedBindings)
export def total: () -> int = { ->
  def taxRate = 2
  100
}

export def other: () -> int = { ->
  def unused = 2
  100
}
"""
    Assert.Equal<(string * string) list>([ "default/unusedBindings", "unused" ], reported source)

[<Fact>]
let ``An allow before a local def suppresses the rule only inside that def`` () =
    let source =
        """export def total: () -> int = { ->
  @allow(default/unusedBindings)
  def inner = { ->
    def taxRate = 2
    100
  }
  def unused = 3
  inner()
}
"""
    Assert.Equal<(string * string) list>([ "default/unusedBindings", "unused" ], reported source)

[<Fact>]
let ``An allow can name several rules`` () =
    let source =
        """@allow(default/unusedBindings, default/unusedResults)
export def total: [Console] () -> int = { ->
  def taxRate = 2
  readLine()
  100
}
"""
    Assert.Empty(reported source)

[<Fact>]
let ``An allow can stand before another attribute`` () =
    let source =
        """module main

@allow(default/unusedBindings)
@entry
def main = { ->
  def unused = 2
  ()
}
"""
    let result = compileWithConsoleCoreUsing (everyLinter ()) (source.ReplaceLineEndings "\n")
    Assert.Empty(result.Diagnostics)

[<Fact>]
let ``An allow that suppresses nothing is reported, and its fix removes it`` () =
    let source =
        """export def total: () -> int = { ->
  @allow(default/unusedBindings)
  def inner = 100
  inner
}
"""
    let source = source.ReplaceLineEndings "\n"
    let result = Compiler.compileWith (everyLinter ()) source
    match result.Diagnostics with
    | [ diagnostic ] ->
        Assert.Equal("default/unusedAllows", diagnostic.Code)
        Assert.Equal(Severity.Warning, diagnostic.Severity)
        Assert.Equal("default/unusedBindings", textAt source diagnostic.Span.Value)
        Assert.Equal(
            "`default/unusedBindings` reports nothing in this definition, so `@allow(default/unusedBindings)` suppresses nothing: remove it",
            diagnostic.Message)
        let fixedSource = applyFix source diagnostic.Fixes.Head
        Assert.Equal("""export def total: () -> int = { ->
  def inner = 100
  inner
}
""".ReplaceLineEndings "\n", fixedSource)
    | other -> failwith $"Expected one diagnostic, got %A{other}"

[<Fact>]
let ``Only the rule that suppresses nothing is reported`` () =
    let source =
        """@allow(default/unusedBindings, default/unusedResults)
export def total: () -> int = { ->
  def taxRate = 2
  100
}
"""
    Assert.Equal<(string * string) list>([ "default/unusedAllows", "default/unusedResults" ], reported source)

[<Fact>]
let ``An allow of a rule there is not says so`` () =
    let source =
        """@allow(default/noSuchRule, other/unusedBindings)
export def total: () -> int = { -> 100 }
"""
    let result = Compiler.compileWith (everyLinter ()) (source.ReplaceLineEndings "\n")
    Assert.Equal<string list>(
        [ "The linter 'default' has no rule 'noSuchRule', so `@allow(default/noSuchRule)` suppresses nothing: remove it"
          "There is no linter 'other', so `@allow(other/unusedBindings)` suppresses nothing: remove it" ],
        result.Diagnostics |> List.map _.Message)

[<Fact>]
let ``An allow is not reported when the official linter does not run`` () =
    let source =
        """@allow(default/unusedBindings)
export def total: () -> int = { -> 100 }
"""
    Assert.Empty((Compiler.compileWith Linters.none (source.ReplaceLineEndings "\n")).Diagnostics)

[<Theory>]
[<InlineData("@allow\ndef total: () -> int = { -> 100 }\n")>]
[<InlineData("@allow(unusedBindings)\ndef total: () -> int = { -> 100 }\n")>]
[<InlineData("def total: () -> int = { ->\n  @allow(\"default/unusedBindings\")\n  def x = 1\n  x\n}\n")>]
let ``An allow must name the rules it allows`` (source: string) =
    let diagnostic = diagnosticOf source
    Assert.Equal(DiagnosticCodes.UnexpectedAttributeArgument.Code, diagnostic.Code)
    Assert.Equal(
        "It takes the linter rules it allows, separated by commas, as `@allow(default/unusedBindings)`",
        diagnostic.Message)

[<Fact>]
let ``An allow must be followed by a def`` () =
    let source =
        """def total: () -> int = { ->
  @allow(default/unusedBindings)
  100
}
"""
    let diagnostic = diagnosticOf (source.ReplaceLineEndings "\n")
    Assert.Equal(DiagnosticCodes.MisplacedAttribute.Code, diagnostic.Code)
    Assert.Equal("It must be followed by a definition", diagnostic.Message)

[<Fact>]
let ``Only allow can be written in a block`` () =
    let source =
        """def total: () -> int = { ->
  @entry
  def x = 1
  x
}
"""
    let diagnostic = diagnosticOf (source.ReplaceLineEndings "\n")
    Assert.Equal(DiagnosticCodes.UnknownAttribute.Code, diagnostic.Code)
    Assert.Equal("Unknown attribute '@entry'", Diagnostics.headline diagnostic)
    Assert.Equal("In a block, only '@allow' can come before a def", diagnostic.Message)

[<Fact>]
let ``A local def after an allow type-checks as it would without it`` () =
    let source =
        """export def total: () -> int = { ->
  @allow(default/unusedBindings)
  def inner = { ->
    def taxRate = 2
    100
  }
  inner() + 1
}

def main = { -> total() }
"""
    let result = Compiler.compile (source.ReplaceLineEndings "\n")
    Assert.Empty(result.Diagnostics)
