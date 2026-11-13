module Nanyx.Compiler.Tests.Features.AttachedEquality

open Xunit
open NyxCompiler

[<Fact>]
let ``A nominal or protected type may attach ==`` () =
    for source in
        [ "type Email = protected string\ndef Email.`==`: Email, Email -> bool = { a, b -> true }\n"
          "type UserId = private int\ndef UserId.`==`: UserId, UserId -> bool = { a, b -> true }\n" ] do
        let result = Compiler.compile source
        Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

[<Fact>]
let ``Attaching == to a structural or built-in type, or != to any type, is an error`` () =
    let cases =
        [ "type Point = (x: int, y: int)\ndef Point.`==`: Point, Point -> bool = { p, q -> p.x == q.x }\n",
          "'Point.==' cannot be defined: 'Point' is a structural type, so its equality is always that of its structure. Declare it `protected` to define its own equality",
          "Point.`==`"
          "def string.`==`: string, string -> bool = { a, b -> true }\n",
          "'string.==' cannot be defined: 'string' is a built-in type, so its equality is the compiler's own. Make a nominal type of it to define its own equality",
          "string.`==`"
          "type Email = protected string\ndef Email.`!=`: Email, Email -> bool = { a, b -> false }\n",
          "'Email.!=' cannot be defined: '!=' is always 'not (a == b)'. Attach '==' instead",
          "Email.`!=`" ]
    for source, expectedMessage, expectedAt in cases do
        let diagnostic = Assert.Single((Compiler.compile source).Diagnostics)
        Assert.Equal(DiagnosticCodes.UnattachableDefinition.Code, diagnostic.Code)
        Assert.Equal(expectedMessage, diagnostic.Message)
        let span = diagnostic.Span.Value
        Assert.Equal(expectedAt, source.Substring(span.Start, span.End - span.Start))
