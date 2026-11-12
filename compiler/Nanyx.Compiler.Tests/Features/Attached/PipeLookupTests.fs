module Nanyx.Compiler.Tests.Features.PipeLookups

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The one diagnostic `result` has: its code, its message, and the text its span covers in `source`. Spans are
/// offsets in the source with its line endings made `\n`, whatever this file's are.
let private onlyDiagnostic (source: string) (result: CompileResult) =
    let diagnostic = Assert.Single(result.Diagnostics)
    let span = diagnostic.Span.Value
    diagnostic.Code, diagnostic.Message, source.ReplaceLineEndings("\n").Substring(span.Start, span.End - span.Start)

let private shoutingModules =
    [ "one.nyx", """module one
export def string.shout: string -> string = { s -> s ++ "!" }"""
      "two.nyx", """module two
export def string.shout: string -> string = { s -> s ++ "?" }""" ]

[<Fact>]
let ``A pipe on a value of a type not known yet names the attached definitions it could not choose between`` () =
    let source = """def string.shout: string -> string = { s -> s }
def list.shout: list(int) -> list(int) = { xs -> xs }
def f = { x -> x \shout }"""
    let code, message, at = onlyDiagnostic source (Compiler.compile source)
    Assert.Equal(DiagnosticCodes.UnknownFunction.Code, code)
    Assert.Equal("The type of the value piped to 'shout' is not known here, so the pipe cannot choose between 'list.shout' and 'string.shout': annotate the value's type, or name the definition, as in `\\list.shout`", message)
    Assert.Equal("shout", at)

[<Fact>]
let ``A pipe on a value of a type not known yet names the one attached definition it could not find`` () =
    let source = """def string.shout: string -> string = { s -> s }
def f = { x -> x \shout }"""
    let code, message, _ = onlyDiagnostic source (Compiler.compile source)
    Assert.Equal(DiagnosticCodes.UnknownFunction.Code, code)
    Assert.Equal("The type of the value piped to 'shout' is not known here, so the pipe cannot find 'string.shout': annotate the value's type, or name the definition, as in `\\string.shout`", message)

[<Fact>]
let ``A pipe to a name nothing attaches or defines says the type has none attached`` () =
    let source = """def s = "x"
def t = s \shout"""
    let code, message, at = onlyDiagnostic source (Compiler.compile source)
    Assert.Equal(DiagnosticCodes.UnknownFunction.Code, code)
    Assert.Equal("'string' has no attached 'shout', and there is no function 'shout' in scope", message)
    Assert.Equal("shout", at)

[<Fact>]
let ``A pipe to a name only another type attaches names what is attached`` () =
    let source = """def list.shout: list(int) -> list(int) = { xs -> xs }
def t = "x" \shout"""
    let code, message, at = onlyDiagnostic source (Compiler.compile source)
    Assert.Equal(DiagnosticCodes.UnknownFunction.Code, code)
    Assert.Equal("'string' has no attached 'shout', and there is no function 'shout' in scope. Only 'list.shout' is in scope", message)
    Assert.Equal("shout", at)

[<Fact>]
let ``A pipe to a plain function that does not take the value says what it takes`` () =
    let source = """def shout: int -> int = { n -> n }
def t = "x" \shout"""
    let code, message, at = onlyDiagnostic source (Compiler.compile source)
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.Equal("'string' has no attached 'shout', and the function 'shout' in scope takes 'int', not 'string'", message)
    Assert.Equal("shout", at)

[<Fact>]
let ``A pipe with arguments to a plain function whose first parameter does not take the value says what it takes`` () =
    let source = """def shout: (int, string) -> int = { n, s -> n }
def t = "x" \shout("y")"""
    let code, message, at = onlyDiagnostic source (Compiler.compile source)
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.Equal("'string' has no attached 'shout', and the function 'shout' in scope takes 'int', not 'string'", message)
    Assert.Equal("shout", at)

[<Fact>]
let ``A pipe to a plain function that takes the value calls it`` () =
    let result = Compiler.compile """def shout: string -> string = { s -> s }
def t = "x" \shout"""
    Assert.Empty(result.Diagnostics)

[<Fact>]
let ``A pipe on the result of a function defined later finds the attached definition`` () =
    let source = """def string.shout: string -> string = { s -> s }
def main = {
  def y = g(5)
  y \shout
}
def g = { x -> "text {x}" }"""
    let result = Compiler.compile (source.ReplaceLineEndings "\n")
    Assert.Empty(result.Diagnostics)

[<Fact>]
let ``A pipe to a definition only an aliased import attaches says how to reach it`` () =
    let source = """module main
import one as t
def a = "x" \shout"""
    let code, message, at = onlyDiagnostic source (compileImportingAll (List.take 1 shoutingModules) source)
    Assert.Equal(DiagnosticCodes.UnknownFunction.Code, code)
    Assert.Equal("'string' has no attached 'shout', and there is no function 'shout' in scope. 'string.shout' is available as 't.string.shout'", message)
    Assert.Equal("shout", at)

[<Fact>]
let ``A pipe to a definition a standard-library module attaches says which module to import`` () =
    let source = "def a = \" x \" \\trim"
    let code, message, _ = onlyDiagnostic source (Compiler.compile source)
    Assert.Equal(DiagnosticCodes.UnknownFunction.Code, code)
    Assert.Equal("'string' has no attached 'trim', and there is no function 'trim' in scope. 'string.trim' is defined in 'nanyx/text': add `import nanyx/text`", message)

[<Fact>]
let ``A pipe to a standard-library definition imported with as says how to reach it`` () =
    let source = "import nanyx/text as t\ndef a = \" x \" \\trim"
    let _, message, _ = onlyDiagnostic source (Compiler.compile source)
    Assert.Equal("'string' has no attached 'trim', and there is no function 'trim' in scope. 'string.trim' is available as 't.string.trim'", message)

[<Fact>]
let ``A pipe to a definition two imports attach names both modules`` () =
    let source = """module main
import one
import two
def a = "x" \shout"""
    let code, message, at = onlyDiagnostic source (compileImportingAll shoutingModules source)
    Assert.Equal(DiagnosticCodes.AmbiguousAttachedFunction.Code, code)
    Assert.Equal("'shout' is ambiguous for 'string': 'string.shout' is defined in 'one' and 'two'; import all but one of them with 'as'", message)
    Assert.Equal("shout", at)

[<Fact>]
let ``A pipe on a value whose type has no name keeps the unknown function error`` () =
    let source = """def r = (a = 1)
def t = r \shout"""
    let code, message, _ = onlyDiagnostic source (Compiler.compile source)
    Assert.Equal(DiagnosticCodes.UnknownFunction.Code, code)
    Assert.Equal("Unknown function 'shout'", message)
