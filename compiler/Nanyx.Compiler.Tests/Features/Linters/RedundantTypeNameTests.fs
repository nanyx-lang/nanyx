module Nanyx.Compiler.Tests.Features.Linters.RedundantTypeName

open Xunit
open NyxCompiler

// A pipe's type name that the piped value's type already says, `people \list.map`, is drawn faint, with a fix that
// removes it (#3159).

/// What `default/redundantTypeName` reports for `source`, compiled with every linter on: each message with the text it
/// is reported at, and the text its fix gives. The module must type-check, or no linter would run.
let private reported (source: string) =
    let source = source.ReplaceLineEndings "\n"
    let result = Compiler.compileWith Linters.everyLinter source
    Assert.True(result.TypeChecked && not (result.Diagnostics |> List.exists Diagnostics.isError), sprintf "%A" result.Diagnostics)
    result.Diagnostics
    |> List.filter (fun diagnostic -> diagnostic.Code = "default/redundantTypeName")
    |> List.map (fun diagnostic ->
        Assert.Equal((Severity.Hint, true), (diagnostic.Severity, diagnostic.Unnecessary))
        let span = diagnostic.Span.Value
        let fix = Assert.Single diagnostic.Fixes
        let edit = Assert.Single fix.Edits
        let fixedText = source.Substring(0, edit.Span.Start) + edit.NewText + source.Substring edit.Span.End
        diagnostic.Message, source.Substring(span.Start, span.End - span.Start), fixedText)

let private people =
    """type Person = (name: string)

def people: list(Person) = [Person(name = "Ada")]
"""

[<Fact>]
let ``A pipe to a list's definition, of a list, has its type name drawn faint, and its fix leaves it out`` () =
    let source = people + "\ndef names = people \\list.map { p -> p.name }\n"
    let message, at, fixedText = Assert.Single(reported source)
    Assert.Equal("`map` is found from the piped value's type, which is a `list`: `list.` can be left out", message)
    Assert.Equal("list.", at)
    Assert.Equal(people + "\ndef names = people \\map { p -> p.name }\n", fixedText)
    // The fixed pipe calls the same definition, and has no type name left to report.
    let fixedResult = Compiler.compileWith Linters.everyLinter fixedText
    Assert.Empty(fixedResult.Diagnostics |> List.filter (fun diagnostic -> diagnostic.Code = "default/redundantTypeName" || Diagnostics.isError diagnostic))
    let typeOf (result: CompileResult) = result.Typed.Value.Types.["names"]
    Assert.Equal(typeOf (Compiler.compileWith Linters.none (source.ReplaceLineEndings "\n")), typeOf fixedResult)

[<Fact>]
let ``A value whose type is a type variable where it is piped is not reported`` () =
    Assert.Empty(reported "def mapped = { xs, f -> xs \\list.map(f) }\n\ndef twice = mapped([1, 2], { x -> x * 2 })\n")

[<Fact>]
let ``A pipe is not reported where a plain definition of the name is in scope, which a reader might take it for`` () =
    Assert.Empty(reported (people + "\ndef map = 1\n\ndef names = people \\list.map { p -> p.name }\n"))
    Assert.Empty(reported (people + "\ndef names = { map: int -> people \\list.map { p -> p.name } }\n"))

[<Fact>]
let ``A pipe written through a module's alias is not reported`` () =
    Assert.Empty(reported "import nanyx/text as t\n\ndef trimmed = \" a \" \\t.string.trim\n")

[<Fact>]
let ``A pipe is not reported where the value's type has a definition of the name of its own, which the name alone would find`` () =
    let source =
        """type Names = list(string)

def Names.map: Names, (string -> string) -> Names = { names, f -> list.map(names, f) }

def names: Names = ["a"]

def shouted = names \list.map { n -> n ++ "!" }
"""
    Assert.Empty(reported source)

[<Fact>]
let ``A pipe to a definition of a type the value is not of, a nominal type's own, is not reported`` () =
    let source =
        """type Ids = protected list(int)

def Ids.fromList: list(int) -> Ids = { xs -> Ids(xs) }

def ids = [1, 2] \Ids.fromList
"""
    Assert.Empty(reported source)
