module Nanyx.Compiler.Tests.Features.ExpectedTypeInstances

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A call that requires a parameterised context of its result's type finds the instance once the type the result is
// expected to have settles it: the type a def writes, or what the rest of the def does with the result (#621).

let private defaults =
    """context Fallback(a) = (fallback: string -> a)
def int.fallback: string -> int = { s -> 0 }
def get: [Fallback(a)] string -> a = { s -> fallback(s) }
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
        """def readAll: [FromString(a)] list(string) -> list(#some(a) | failure) = { texts -> texts \List.map({ t -> FromString(a).fromString(t) }) }
def ints: list(#some(int) | #invalidInt) = readAll(["1", "x"])
"""
    let result = Compiler.compile (normalized source)
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)

[<Fact>]
let ``A call whose result's type nothing settles still needs the context where it is`` () =
    let result = Compiler.compile (normalized (defaults + "def ignore: a -> int = { x -> 0 }\ndef u: string -> int = { s -> ignore(get(s)) }\n"))
    let diagnostic = Assert.Single(result.Diagnostics)
    Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, diagnostic.Code)
    Assert.Equal("'get' requires the context 'Fallback', which is not available here", diagnostic.Message)

let private monoid =
    """context Monoid(a) = (empty: a, combine: (a, a) -> a)
def e: [Monoid(a)] () -> a = { empty }
def withInt: ([Monoid(int)] () -> b) -> b = { f -> use Monoid(int)(empty = 5, combine = { x, y -> x + y }) in f() }
"""

// An instance at another type in scope puts the context's members in scope by name, but isn't the call's (#1653).
[<Theory>]
[<InlineData("""def s: string = use Monoid(int)(empty = 0, combine = { x, y -> x + y }) in e()""", "'e' requires the context 'Monoid(string)', which is not loaded here, and 'string.combine' and 'string.empty' are not defined: define them, or load an instance with `use`")>]
[<InlineData("""def h: string = withInt({ e() })""", "'e' requires the context 'Monoid(string)', which is not loaded here, and 'string.combine' and 'string.empty' are not defined: define them, or load an instance with `use`")>]
let ``A call at a type an instance in scope is not for is an error`` (definition: string, message: string) =
    let result = Compiler.compile (normalized (monoid + definition + "\n"))
    let diagnostic = Assert.Single(result.Diagnostics)
    Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, diagnostic.Code)
    Assert.Equal(message, diagnostic.Message)

// A value of a type nothing settles is one only a later use could fix (see `ContextInference.unfixedValueType`).
[<Fact>]
let ``A value of a type nothing settles, beside an instance at another type, is an error at the def`` () =
    let value = "def t = use Monoid(int)(empty = 0, combine = { x, y -> x + y }) in e()\n"
    let diagnostic = Assert.Single((Compiler.compile (normalized (monoid + value))).Diagnostics)
    Assert.Equal(DiagnosticCodes.ValueContextTypeOpen.Code, diagnostic.Code)

[<Theory>]
[<InlineData("""def w: string = use Monoid(string)(empty = "", combine = { x, y -> x ++ y }) in e()""")>]
[<InlineData("""def n: int = withInt({ e() })""")>]
[<InlineData("""def d: [Monoid(a)] () -> a = { e() }""")>]
let ``A call finds the instance in scope at the type its result is expected to have`` (definition: string) =
    let result = Compiler.compile (normalized (monoid + definition + "\n"))
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
[<Fact>]
let ``A function named as a value at the type the def writes needs an instance of that type (#2017)`` () =
    let source =
        """context Decode(a) = (decode: string -> a)
def des: [Decode(a)] string -> a = { text -> decode(text) }
def apply: (string -> a), string -> a = { f, text -> f(text) }
def u: bool = apply(des, "ab")
"""
    let diagnostic = Assert.Single((Compiler.compile (normalized source)).Diagnostics)
    Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, diagnostic.Code)
    Assert.StartsWith("'des' requires the context 'Decode(bool)', which is not loaded here", diagnostic.Message)
