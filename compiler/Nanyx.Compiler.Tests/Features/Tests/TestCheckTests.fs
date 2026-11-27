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
    let code, message, _ = onlyDiagnostic "@test\ndef `reads the arguments` = {\n  assert(args() == [])\n}\n"
    Assert.Equal(DiagnosticCodes.TestContextNotProvided.Code, code)
    Assert.StartsWith("The test 'reads the arguments' requires the context 'Env', which the test host does not provide", message)

[<Fact>]
let ``A test may require Console and Random, which the test host provides`` () =
    let typed = compileClean "import nanyx/random as random\n\n@test\ndef `prints a roll` = {\n  println(\"{random.below(6)}\")\n}\n"
    Assert.Equal(1, typed.Tests.Length)

[<Fact>]
let ``@test takes no argument but #io`` () =
    let code, message, place = onlyDiagnostic "@test(#fast)\ndef `adds` = {\n  assert(1 + 1 == 2)\n}\n"
    Assert.Equal(DiagnosticCodes.UnexpectedAttributeArgument.Code, code)
    Assert.Equal("#fast", place)
    Assert.StartsWith("It takes no argument, or `#io`", message)

[<Fact>]
let ``A test marked @test(#io) may require Env, and is one of the module's tests`` () =
    let typed = compileClean "@test(#io)\ndef `reads the arguments` = {\n  assert(args() == [])\n}\n"
    Assert.Equal<(string * bool) list>([ "reads the arguments", true ], typed.Tests |> List.map (fun test -> test.Name, test.Io))

[<Fact>]
let ``NYX0312 for a context @test(#io) provides says to mark the test so, and its fix does`` () =
    let source = "@test\ndef `reads the arguments` = {\n  assert(args() == [])\n}\n"
    let diagnostic = Assert.Single((Compiler.compile source).Diagnostics)
    Assert.Equal(DiagnosticCodes.TestContextNotProvided.Code, diagnostic.Code)
    Assert.EndsWith("with `use` in the test, or mark the test `@test(#io)` to use the real one", diagnostic.Message)
    let fix = Assert.Single(diagnostic.Fixes)
    Assert.Equal("Mark the test `@test(#io)`", fix.Title)
    Assert.Equal("@test(#io)\ndef `reads the arguments` = {\n  assert(args() == [])\n}\n", applyFix source fix)

[<Fact>]
let ``NYX0312 for a context @test(#io) doesn't provide says only to load one, with no fix`` () =
    let source = "def fetch: [Http] () -> () = { _ -> () }\n\n@test(#io)\ndef `fetches` = {\n  fetch()\n}\n"
    let diagnostic = Assert.Single((Compiler.compile source).Diagnostics)
    Assert.Equal(DiagnosticCodes.TestContextNotProvided.Code, diagnostic.Code)
    Assert.Equal(
        "The test 'fetches' requires the context 'Http', which the test host does not provide: load an instance of it, such as a fake, with `use` in the test",
        diagnostic.Message)
    Assert.Empty(diagnostic.Fixes)

[<Fact>]
let ``NYX0312 holds for a context a test requires only through a function whose contexts are inferred`` () =
    let code, message, _ = onlyDiagnostic "def count = { list.length(args()) }\n\n@test\ndef `counts the arguments` = {\n  assert(count() == 0)\n}\n"
    Assert.Equal(DiagnosticCodes.TestContextNotProvided.Code, code)
    Assert.StartsWith("The test 'counts the arguments' requires the context 'Env'", message)
