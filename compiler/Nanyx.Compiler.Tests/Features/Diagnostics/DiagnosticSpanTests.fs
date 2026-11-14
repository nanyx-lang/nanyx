module Nanyx.Compiler.Tests.Features.Diagnostics.DiagnosticSpans

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``An unknown identifier is reported at the reference, not an earlier mention of the name`` () =
    // The old positioning searched the source for the first quoted name, which is in the string.
    let source = "def note = \"missing\"\ndef value = missing"
    let diagnostic = single (Compiler.compile source)
    Assert.Equal(DiagnosticCodes.UnknownIdentifier.Code, diagnostic.Code)
    Assert.Equal(DiagnosticCodes.UnknownIdentifier.Title, diagnostic.Title)
    let span = spanOf diagnostic
    Assert.Equal((1, 12), (span.StartLine, span.StartColumn))
    Assert.Equal("missing", textAt source span)

[<Fact>]
let ``A type mismatch in an argument is reported at the argument`` () =
    let source = "import nanyx/console\n\nwrite(42)"
    let diagnostic = single (Compiler.compile source)
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, diagnostic.Code)
    Assert.Equal("42", textAt source (spanOf diagnostic))

[<Fact>]
let ``A type mismatch between operands is reported at the operation`` () =
    let source = "def a = 1\ndef b = a + \"x\""
    let diagnostic = single (Compiler.compile source)
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, diagnostic.Code)
    Assert.Equal("a + \"x\"", textAt source (spanOf diagnostic))

[<Fact>]
let ``An unknown member is reported at the member name`` () =
    let source = "def point = (x = 1, y = 2)\ndef z = point.z"
    let diagnostic = single (Compiler.compile source)
    Assert.Equal(DiagnosticCodes.UnknownMember.Code, diagnostic.Code)
    Assert.Equal("z", textAt source (spanOf diagnostic))
    Assert.Equal(1, (spanOf diagnostic).StartLine)

[<Fact>]
let ``A non-exhaustive match is reported at its head`` () =
    let source =
        normalized """def value: #some(int) | #none = #none
def result = match value
| #some(v) -> v
"""
    let diagnostic = single (Compiler.compile source)
    Assert.Equal(DiagnosticCodes.NonExhaustiveMatch.Code, diagnostic.Code)
    let span = spanOf diagnostic
    Assert.Equal((1, 13), (span.StartLine, span.StartColumn))
    Assert.Equal("match value", textAt source span)

[<Fact>]
let ``A pattern of the wrong type is reported at the pattern`` () =
    let source =
        normalized """def value = 1
def result = match value
| 0 -> 0
| "one" -> 1
| _ -> 2
"""
    let diagnostic = single (Compiler.compile source)
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, diagnostic.Code)
    Assert.Equal("\"one\"", textAt source (spanOf diagnostic))

[<Fact>]
let ``A mismatch inside a tuple pattern is reported within the pattern, not at the whole match`` () =
    let source =
        normalized """def value = (1, 2)
def result = match value
| (a, "two") -> a
| _ -> 0
"""
    let diagnostic = single (Compiler.compile source)
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, diagnostic.Code)
    // The element's constraint is solved first, so the tuple's own constraint is the one that fails.
    Assert.Equal("""(a, "two")""", textAt source (spanOf diagnostic))

[<Fact>]
let ``A reference to a private definition links to where it is declared`` () =
    let source = "module inner =\n  def secret = 1\n  export def open = secret + 1\ndef value = inner.secret"
    let diagnostic = single (Compiler.compile source)
    Assert.Equal(DiagnosticCodes.NotExported.Code, diagnostic.Code)
    Assert.Equal("inner.secret", textAt source (spanOf diagnostic))
    let relatedSpan, relatedMessage = Assert.Single(diagnostic.Related)
    Assert.Equal((1, 6), (relatedSpan.StartLine, relatedSpan.StartColumn))
    Assert.Contains("declared here", relatedMessage)

[<Fact>]
let ``An annotation more general than its body is reported at the definition's name`` () =
    let source = "def id: a -> a = { x -> x + 1 }"
    let diagnostic = single (Compiler.compile source)
    Assert.Equal(DiagnosticCodes.AnnotationTooGeneral.Code, diagnostic.Code)
    Assert.Equal("id", textAt source (spanOf diagnostic))

[<Fact>]
let ``A parse error has a code and the position where parsing failed`` () =
    let result = Compiler.compile "def x = 1\ndef = 2"
    let diagnostic = single result
    Assert.Equal(DiagnosticCodes.SyntaxError.Code, diagnostic.Code)
    let span = spanOf diagnostic
    Assert.Equal(1, span.StartLine)
    // The span already says where; the message does not repeat it.
    Assert.DoesNotContain("Ln:", diagnostic.Message)

[<Fact>]
let ``A parse error's message leaves out the file however long its path is`` () =
    // FParsec wraps the line naming the file and position when the path is long.
    let directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nanyx-diagnostics-tests", String.replicate 6 "a-rather-long-directory-name-", System.Guid.NewGuid().ToString("N"))
    System.IO.Directory.CreateDirectory directory |> ignore
    let file = System.IO.Path.Combine(directory, "broken.nyx")
    System.IO.File.WriteAllText(file, "def = 2")
    let diagnostic = single (Compiler.compileFile file)
    Assert.Equal(DiagnosticCodes.SyntaxError.Code, diagnostic.Code)
    Assert.DoesNotContain("a-rather-long", diagnostic.Message)
    Assert.DoesNotContain("Ln:", diagnostic.Message)
    Assert.StartsWith("def = 2", diagnostic.Message)

[<Fact>]
let ``An unknown standard library module is reported at the import`` () =
    let source = "import nanyx/sockets\n\ndbg(1)"
    let diagnostic = single (Compiler.compile source)
    Assert.Equal(DiagnosticCodes.UnknownStandardLibraryModule.Code, diagnostic.Code)
    Assert.Equal("nanyx/sockets", textAt source (spanOf diagnostic))

[<Fact>]
let ``A missing file is a critical diagnostic about the whole compilation`` () =
    let diagnostic = single (Compiler.compileFile (Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".nyx")))
    Assert.Equal(DiagnosticCodes.FileNotFound.Code, diagnostic.Code)
    Assert.Equal(Severity.Critical, diagnostic.Severity)
    Assert.True(diagnostic.Span.IsNone)

[<Fact>]
let ``A diagnostic in an imported module is reported in that module's file`` () =
    withTempDir (fun dir ->
        let modulePath = Path.Combine(dir, "mod.nyx")
        let mainPath = Path.Combine(dir, "main.nyx")
        File.WriteAllText(modulePath, "module mod\nexport def value = missing")
        // The root mentions `missing` too, where the old positioning would have put the error.
        File.WriteAllText(mainPath, "module main\nimport mod\ndef missing2 = \"missing\"")
        let diagnostic = single (Compiler.compileFile mainPath)
        Assert.Equal(DiagnosticCodes.UnknownIdentifier.Code, diagnostic.Code)
        let span = spanOf diagnostic
        Assert.Equal(Path.GetFullPath modulePath, span.File)
        Assert.Equal((1, 19), (span.StartLine, span.StartColumn)))

[<Fact>]
let ``An import cycle is reported at the import that closes it`` () =
    withTempDir (fun dir ->
        let mainPath = Path.Combine(dir, "main.nyx")
        File.WriteAllText(Path.Combine(dir, "a.nyx"), "module a\nimport main\nexport def x = 1")
        File.WriteAllText(mainPath, "module main\nimport a\ndef y = x")
        let diagnostic = single (Compiler.compileFile mainPath)
        Assert.Equal(DiagnosticCodes.ImportCycle.Code, diagnostic.Code)
        let span = spanOf diagnostic
        Assert.Equal(Path.Combine(dir, "a.nyx") |> Path.GetFullPath, span.File)
        Assert.Equal(1, span.StartLine))
