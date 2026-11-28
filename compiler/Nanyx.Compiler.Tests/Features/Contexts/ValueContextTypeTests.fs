module Nanyx.Compiler.Tests.Features.ValueContextTypes

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A value that is not a function can't require a context, and a later use doesn't fix its type: a def of one whose
// call needs a context of a type nothing in the def fixes is NYX0283 at the def (#2506).

let private decode =
    normalized """type Person = (name: string)
context Decode(a) = (decode: string -> a)
def Person.decode: string -> Person = { s -> Person(name = s) }
def parse: [Decode(a)] string -> a = { json -> decode(json) }

"""

[<Fact>]
let ``A top-level value whose call's context type only a later use fixes is an error at the def`` () =
    let source = decode + "def obj = parse(\"x\")\ndef person: Person = obj\nprintln(person.name)\n"
    let code, message, at = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.ValueContextTypeOpen.Code, code)
    Assert.Equal("obj", at)
    Assert.Equal(
        "'parse' requires 'Decode' of a type nothing in the definition of 'obj' fixes, and a later use of a value doesn't fix its type: write the type, as in 'def obj: T = ...', or pass it as a type argument to a function that takes one, as in 'parse(type T, ...)'",
        message)

[<Fact>]
let ``A local value whose call's context type only a later use fixes is an error at the def`` () =
    let source = decode + "def main = {\n  def obj = parse(\"x\")\n  def person: Person = obj\n  println(person.name)\n}\n"
    let code, _, at = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.ValueContextTypeOpen.Code, code)
    Assert.Equal("obj", at)

[<Theory>]
[<InlineData("def person: Person = parse(\"x\")\n")>]
[<InlineData("def main = {\n  def person: Person = parse(\"x\")\n  person.name\n}\n")>]
[<InlineData("def read = { s -> parse(s) }\ndef person: Person = read(\"x\")\n")>]
let ``A value whose def writes the type, or a function, finds the context`` (rest: string) =
    Assert.Empty((Compiler.compile (decode + rest)).Diagnostics)

[<Fact>]
let ``A context with an associated type whose own argument is open is still an error at the def`` () =
    let source =
        normalized """type Person = (name: string)
context Load(a) = (source: type, load: string -> a)
def Person.load: string -> Person = { s -> Person(name = s) }
def Person.source = "text"
def read: [Load(a)] string -> a = { text -> load(text) }

def obj = read("x")
def person: Person = obj
println(person.name)
"""
    let code, _, at = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.ValueContextTypeOpen.Code, code)
    Assert.Equal("obj", at)
