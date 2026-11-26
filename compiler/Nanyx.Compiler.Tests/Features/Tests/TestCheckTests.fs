/// Tests (#2930): a def marked `@test` is a function of no arguments that gives `()`, only another test may refer to
/// it, and it requires only the contexts the test host provides (`TestChecks`).
module Nanyx.Compiler.Tests.Features.Tests

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``A test is a block, which is a function of no arguments that gives ()`` () =
    let typed = compileClean "def double: int -> int = { n -> n * 2 }\n\n@test\ndef `doubles` = {\n  assert(double(2) == 4)\n}\n"
    Assert.Equal<(string * string) list>([ "main", "doubles" ], typed.Tests |> List.map (fun test -> test.Module, test.Name))

[<Fact>]
let ``A test may call another test`` () =
    let typed = compileClean "@test\ndef `adds` = {\n  assert(1 + 1 == 2)\n}\n\n@test\ndef `adds twice` = {\n  `adds`()\n  `adds`()\n}\n"
    Assert.Equal(2, typed.Tests.Length)

[<Fact>]
let ``A test that takes an argument is NYX0310 at the test`` () =
    let code, message, _ = onlyDiagnostic "@test\ndef takesOne: int -> () = { n -> assert(n > 0) }\n"
    Assert.Equal(DiagnosticCodes.NotATest.Code, code)
    Assert.Equal("A test takes no arguments and gives back `()`, as `{ assert(...) }` does, but 'takesOne' has the type int -> ()", message)

[<Fact>]
let ``Code that isn't a test referring to one is NYX0311 at the reference`` () =
    Assert.Equal(
        (DiagnosticCodes.ReferenceToTest.Code, "'adds' is a test, which only a test build has, so only another test can refer to it", "`adds`()"),
        onlyDiagnostic "@test\ndef `adds` = {\n  assert(1 + 1 == 2)\n}\n\ndef run = { `adds`() }\n")

[<Fact>]
let ``A test that requires a context the test host doesn't provide is NYX0312`` () =
    let code, message, _ = onlyDiagnostic "@test\ndef `prints` = {\n  println(\"hi\")\n}\n"
    Assert.Equal(DiagnosticCodes.TestContextNotProvided.Code, code)
    Assert.StartsWith("The test 'prints' requires the context 'Console', which the test host does not provide", message)

[<Fact>]
let ``@test takes no argument`` () =
    let code, _, _ = onlyDiagnostic "@test(1)\ndef `adds` = {\n  assert(1 + 1 == 2)\n}\n"
    Assert.Equal(DiagnosticCodes.UnexpectedAttributeArgument.Code, code)
