module Nanyx.Compiler.Tests.Features.ExpectedTypeInstances

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A call that requires a parameterised context of its result's type finds the instance once the type the result is
// expected to have settles it: the type a def writes, or what the rest of the def does with the result (#621).

let private defaults =
    """context Default(a) = (default: string -> a)
def int.default: string -> int = { s -> 0 }
def get: [Default(a)] string -> a = { s -> default(s) }
"""

[<Theory>]
[<InlineData("""def n: int = get("x")""")>]
[<InlineData("""def m: string -> int = { s -> get(s) }""")>]
[<InlineData("""def k = { xs -> [get("x")] \List.map({ v -> v + 1 }) }""")>]
[<InlineData("""def j = { s -> get(s) + 1 }""")>]
let ``A call finds its instance at the type its result is expected to have`` (definition: string) =
    let result = Compiler.compile (normalized (defaults + definition + "\n"))
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)

[<Fact>]
let ``A call finds an instance of a context of two types at the type a def writes`` () =
    let source =
        """def readAll: [FromString(a, e)] list(string) -> list(#some(a) | e) = { texts -> texts \List.map({ t -> FromString(a, e).fromString(t) }) }
def ints: list(#some(int) | #invalidInt) = readAll(["1", "x"])
"""
    let result = Compiler.compile (normalized source)
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)

[<Fact>]
let ``A call whose result's type nothing settles still needs the context where it is`` () =
    let result = Compiler.compile (normalized (defaults + "def ignore: a -> int = { x -> 0 }\ndef u: string -> int = { s -> ignore(get(s)) }\n"))
    let diagnostic = Assert.Single(result.Diagnostics)
    Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, diagnostic.Code)
    Assert.Equal("'get' requires the context 'Default', which is not available here", diagnostic.Message)
