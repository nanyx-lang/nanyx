module Nanyx.Compiler.Tests.Features.IsExpressions

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The code of the one diagnostic `source` compiles to, and the text it is reported at.
let private unknownName (source: string) =
    let code, at = singleError source
    Assert.Equal(DiagnosticCodes.UnknownIdentifier.Code, code)
    at

[<Fact>]
let ``a test is a bool, as a def's value and a lambda's body`` () =
    Assert.Equal("bool", typeOf "def isQuux = #quux is #quux" "isQuux")
    Assert.Equal("#some(int) -> bool", typeOf "def big = { o: #some(int) -> o is #some(v) and v > 3 }" "big")

[<Fact>]
let ``the names a test binds are in scope for the rest of its and chain, outside an if`` () =
    Assert.Empty((Compiler.compile "def f = { a: #some(int) | #none, b: #some(int) | #none -> a is #some(x) and b is #some(y) and x + y > 3 }\n").Diagnostics)

[<Fact>]
let ``a name a test binds is not in scope after or`` () =
    Assert.Equal("v", unknownName "def f = { o: #some(int) | #none -> o is #some(v) or v > 3 }\n")

[<Fact>]
let ``a name a test binds is not in scope outside its chain`` () =
    Assert.Equal("v", unknownName "def f = { o: #some(bool) | #none -> (o is #some(v) and true) == v }\n")
    Assert.Equal("v", unknownName "def f = { o: #some(int) | #none -> not (o is #some(v) and v > 1) and v > 2 }\n")

[<Fact>]
let ``an if's chain binds its names in the then branch and not in the else branch`` () =
    Assert.Empty((Compiler.compile "def f = { o: #some(int) | #none -> if o is #some(v) and v > 1 then v else 0 }\n").Diagnostics)
    Assert.Equal("v", unknownName "def f = { o: #some(int) | #none -> if o is #some(v) and v > 1 then 1 else v }\n")
