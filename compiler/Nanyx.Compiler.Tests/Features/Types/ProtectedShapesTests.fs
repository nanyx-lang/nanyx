module Nanyx.Compiler.Tests.Features.Types.ProtectedShapes

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// A module block that defines `Point`, `modifier` (`protected` or `private`), with the attached definitions `makers`,
/// and then `uses` outside it.
let private points (modifier: string) (makers: string) (uses: string) =
    normalized $"""module Points =
  export type Point = {modifier} (x: int, y: int)
{makers}
import Points

{uses}
"""

let private checkedNew =
    """
  export def Point.new: int, int -> #negative | #some(Points.Point) = { x, y ->
    if x < 0 or y < 0
      then #negative
      else #some(Point(x = x, y = y))
  }
"""

[<Fact>]
let ``A record of a protected type's shape outside its module says why it isn't one, and how to make one`` () =
    let source = points "protected" checkedNew "def p: Point = (x = 3, y = 4)"
    let result = Compiler.compile source
    Assert.Equal(
        (DiagnosticCodes.ProtectedShape.Code,
         "'Point' is protected, so only the module 'Points' makes values of it, and a record of its shape isn't a 'Point' here: make one with 'Point.new'",
         "(x = 3, y = 4)"),
        onlyDiagnosticIn source result)
    // `Point.new` gives a `#some(Point)`, not a `Point`, so making it with that is not offered.
    Assert.Empty((single result).Fixes)

[<Fact>]
let ``A record of a private type's shape outside its module says the type is private`` () =
    let source = points "private" checkedNew "def p: Point = (x = 3, y = 4)"
    Assert.Equal(
        (DiagnosticCodes.ProtectedShape.Code,
         "'Point' is private, so only the module 'Points' makes values of it, and a record of its shape isn't a 'Point' here: make one with 'Point.new'",
         "(x = 3, y = 4)"),
        onlyDiagnostic source)

[<Fact>]
let ``With no attached definition that makes one, the error says only the module makes them`` () =
    let source = points "protected" "" "def p: Point = (x = 3, y = 4)"
    Assert.Equal(
        (DiagnosticCodes.ProtectedShape.Code,
         "'Point' is protected, so only the module 'Points' makes values of it, and a record of its shape isn't a 'Point' here",
         "(x = 3, y = 4)"),
        onlyDiagnostic source)

[<Fact>]
let ``Several ways to make one are each named`` () =
    let makers =
        """
  export def Point.origin: () -> Points.Point = { -> Point(x = 0, y = 0) }
  export def Point.onAxis: int -> Points.Point = { x -> Point(x = x, y = 0) }
  export def Point.at: int, int -> Points.Point = { x, y -> Point(x = x, y = y) }
  export def Point.norm: Points.Point -> int = { p -> p.x + p.y }
"""
    let source = points "protected" makers "def p: Point = (x = 3, y = 4)"
    let code, message, _ = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.ProtectedShape.Code, code)
    Assert.EndsWith(": make one with 'Point.at', 'Point.onAxis' or 'Point.origin'", message)

[<Fact>]
let ``The one way to make one that takes the fields is offered as a fix`` () =
    let makers =
        """
  export def Point.at: int, int -> Points.Point = { x, y -> Point(x = x, y = y) }
"""
    let source = points "protected" makers "def p: Point = (y = 4, x = 3)"
    let diagnostic = diagnosticOf source
    Assert.Equal(DiagnosticCodes.ProtectedShape.Code, diagnostic.Code)
    let fix = Assert.Single(diagnostic.Fixes)
    Assert.Equal("Make it with `Point.at`", fix.Title)
    let fixedSource = applyFix source fix
    Assert.Contains("def p: Point = Point.at(3, 4)", fixedSource)
    Assert.Empty((Compiler.compile fixedSource).Diagnostics)

[<Fact>]
let ``A function argument of the type's shape gets the same error`` () =
    let source = points "protected" "" "def norm: Point -> int = { _ -> 0 }\n\ndef n = norm((x = 3, y = 4))"
    let code, _, text = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.ProtectedShape.Code, code)
    Assert.Equal("(x = 3, y = 4)", text)

[<Fact>]
let ``A record of another shape is an ordinary mismatch`` () =
    let source = points "protected" checkedNew "def p: Point = (x = 3, z = 4)"
    let code, _, _ = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)

[<Fact>]
let ``Constructing the type outside its module names the ways to make one`` () =
    let source = points "protected" checkedNew "def p = Point(x = 3, y = 4)"
    Assert.Equal(
        (DiagnosticCodes.NominalConstructedOutsideItsModule.Code,
         "The protected type 'Point' can only be constructed in the module that defines it: make one with 'Point.new'",
         "Point"),
        onlyDiagnostic source)

/// The one diagnostic of `source`, which must be a type mismatch: its message and its one suggestion, the note.
let private mismatchWithNote (source: string) =
    let diagnostic = Assert.Single((Compiler.compile source).Diagnostics)
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, diagnostic.Code)
    diagnostic.Message, Assert.Single(diagnostic.Suggestions)

[<Fact>]
let ``A value of another type given for a private type outside its module is a mismatch that says who makes values of it`` () =
    // Not only a value of its shape: one of another type could never be one here either (#1804).
    let source = points "private" checkedNew "def p: Point = \"\""
    Assert.Equal(
        ("'string' vs 'Points.Point'", "'Point' is private, so only the module 'Points' makes values of it: make one with 'Point.new'"),
        mismatchWithNote source)

[<Fact>]
let ``A value of another type given for a protected type with no maker says no code outside its module can make one`` () =
    let source = points "protected" "" "def p: Point = 3"
    Assert.Equal(
        ("'int' vs 'Points.Point'",
         "'Point' is protected, so only the module 'Points' makes values of it, and it exports nothing that makes one: no code outside 'Points' can make a 'Point'"),
        mismatchWithNote source)

[<Fact>]
let ``A value of a nominal type's shape is still NYX0260, and in its own module a mismatch has no note`` () =
    let source = points "private" checkedNew "def p: Point = (x = 3, y = 4)"
    Assert.Equal(DiagnosticCodes.ProtectedShape.Code, (Assert.Single((Compiler.compile source).Diagnostics)).Code)
    let home = normalized "type Point = private (x: int, y: int)\ndef p: Point = \"\"\n"
    let diagnostic = Assert.Single((Compiler.compile home).Diagnostics)
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, diagnostic.Code)
    Assert.Empty(diagnostic.Suggestions)
