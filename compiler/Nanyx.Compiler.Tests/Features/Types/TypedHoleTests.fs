module Nanyx.Compiler.Tests.Features.Types.TypedHoles

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A `???` says the type the code around it expects in its place, and the names in scope that would fill it, each a fix
// (#3200). Inference is unchanged: the hole is still `undefined`, so these read what it is in once the module is inferred.

/// The message of the one NYX0111 compiling `source` gives.
let private holeMessage (source: string) =
    let diagnostic =
        (Compiler.compile (normalized source)).Diagnostics |> List.filter (fun diagnostic -> diagnostic.Code = DiagnosticCodes.NotImplemented.Code) |> Assert.Single
    diagnostic.Message

let private written = "`???` stands for code not written yet"

[<Theory>]
// An argument, of a call with one argument or several.
[<InlineData("def shout: string -> string = { s -> s }\ndef x = shout(???)\n", "a `string` goes here")>]
[<InlineData("def g: string, int -> string = { s, n -> s }\ndef x = g(\"a\", ???)\n", "an `int` goes here")>]
// The value piped, which is the first argument.
[<InlineData("def shout: string -> string = { s -> s }\ndef x = ??? \\shout\n", "a `string` goes here")>]
// The type a def writes, of its value or of its function's result.
[<InlineData("def count: int = ???\n", "an `int` goes here")>]
[<InlineData("def f: int -> bool = { n -> ??? }\n", "a `bool` goes here")>]
[<InlineData("def main = {\n  def label: string = ???\n  println(label)\n}\n", "a `string` goes here")>]
// The other branch of an `if`, and its condition.
[<InlineData("def f: bool -> int = { b -> if b then ??? else 2 }\n", "an `int` goes here")>]
[<InlineData("def f = { n -> if ??? then n else 2 }\n", "a `bool` goes here")>]
// The other arms of a match.
[<InlineData("def f = { b -> match b | #yes -> \"y\" | #no -> ??? }\n", "a `string` goes here")>]
// The other elements of a list, or the element of the list written.
[<InlineData("def xs = [1, 2, ???]\n", "an `int` goes here")>]
[<InlineData("def xs: list(string) = [???]\n", "a `string` goes here")>]
// The other operand.
[<InlineData("def f: int -> int = { n -> n * ??? }\n", "an `int` goes here")>]
// A field of the record a literal is checked against, by name or by place, and a tag's payload.
[<InlineData("type Person = (name: string, age: int)\ndef p: Person = (name = \"Ada\", age = ???)\n", "an `int` goes here")>]
[<InlineData("def p: (int, string) = (1, ???)\n", "a `string` goes here")>]
[<InlineData("def t: #small | #large(int) = #large(???)\n", "an `int` goes here")>]
let ``A hole says the type the code around it expects`` (source: string, says: string) =
    let message = holeMessage source
    Assert.StartsWith($"{written}: {says}", message)

[<Fact>]
let ``A hole whose type nothing says is reported as it was`` () =
    Assert.Equal(written, holeMessage "def f = { x -> ??? }\n")
    Assert.Equal(written, holeMessage "def main = {\n  def x = ???\n  println(\"a\")\n}\n")

[<Fact>]
let ``The names that would fill a hole are the locals nearest it, then the module's defs, then its functions' calls`` () =
    let source =
        """def label: string = "total"

def describe: int -> string = { n -> "n" }

def report: string, int -> string = { name, count -> ??? }
"""
    Assert.Equal($"{written}: a `string` goes here, such as `name`, `label` or `describe(???)`", holeMessage source)

[<Fact>]
let ``A local hides an earlier one of its name, and a name declared after the hole is not in scope there`` () =
    let source =
        """def shout: string -> string = { s -> s }

def main = {
  def a = "first"
  def b = "second"
  def a = "third"
  println(shout(???))
  def c = "after"
  println(c)
}
"""
    Assert.Equal($"{written}: a `string` goes here, such as `a`, `b` or `shout(???)`", holeMessage source)

[<Fact>]
let ``A hole doesn't offer the def it is in, and offers at most five fills`` () =
    let source =
        """def one = 1
def two = 2
def three = 3
def four = 4
def five = 5
def six = 6
def count: int = ???
"""
    Assert.Equal($"{written}: an `int` goes here, such as `five`, `four`, `one`, `six` or `three`", holeMessage source)

[<Fact>]
let ``A call that fills a hole has a hole for each argument`` () =
    let source =
        """def none: () -> int = { () -> 0 }

def double: int -> int = { n -> n * 2 }

def add: int, int -> int = { a, b -> a + b }

def count: int = ???
"""
    Assert.Equal($"{written}: an `int` goes here, such as `add(???, ???)`, `double(???)` or `none()`", holeMessage source)

[<Fact>]
let ``A generic def fills a hole its type fits`` () =
    let source =
        """def empty: list(a) = []

def numbers: list(int) = ???
"""
    Assert.Equal($"{written}: a `list(int)` goes here, such as `empty`", holeMessage source)

[<Fact>]
let ``Each fill is a fix that writes it in the hole's place`` () =
    let source =
        normalized
            """def describe: int -> string = { n -> "n" }

def report: string -> string = { name -> ??? }
"""
    let diagnostic = Assert.Single (Compiler.compile source).Diagnostics
    Assert.Equal<(string * string list) list>(
        [ "Fill in `name`", [ "name" ]; "Fill in `describe(???)`", [ "describe(???)" ] ],
        diagnostic.Fixes |> List.map (fun fix -> fix.Title, fix.Edits |> List.map _.NewText)
    )
    Assert.All(diagnostic.Fixes |> List.collect _.Edits, fun edit -> Assert.Equal("???", textAt source edit.Span))

[<Fact>]
let ``A hole is still deferred, and still undefined`` () =
    let result = Compiler.compile "def area: int -> int = { r -> ??? }\ndef total: int = area(2) + 1"
    Assert.True(result.ErrorsDeferred)
    Assert.Equal("int -> int", Unifier.tyToString result.Typed.Value.Types.["area"])
