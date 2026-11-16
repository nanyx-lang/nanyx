module Nanyx.Compiler.Tests.Features.OwnInstances

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A type's own instance always wins: a `use` can't replace it (#1501).

/// The one error `source` has, which must be a `use` replacing a type's own instance: its message, and the text it is
/// reported at.
let private replaced (source: string) =
    let code, at = singleError (normalized source)
    Assert.Equal(DiagnosticCodes.OwnInstanceReplaced.Code, code)
    let diagnostic = (Compiler.compile (normalized source)).Diagnostics.Head
    diagnostic.Message, at

[<Fact>]
let ``A use can't replace a built-in type's ==`` () =
    let message, at =
        replaced
            """
def same: [Eq(a)] a, a -> bool = { x, y -> x == y }
def main = {
  use Eq(int)(`==` = { x, y -> true })
  same(1, 2)
}
"""
    Assert.Equal("Eq(int)(`==` = { x, y -> true })", at)
    Assert.Equal(
        "'int' already has `==`: a `use` can't replace it. For a different equality, wrap the value in a protected type with its own `==`, or pass the comparison to a `...By` function",
        message)

[<Fact>]
let ``A use can't replace a type's attached ==`` () =
    let _, at =
        replaced
            """
type Email = protected string
def Email.`==`: Email, Email -> bool = { a, b -> a == b }
def ignoringCase = (`==` = { a: Email, b: Email -> true })
def n = use ignoringCase in 1
"""
    Assert.Equal("ignoringCase", at)

[<Fact>]
let ``A use can't replace the == derived for a record`` () =
    let _, at =
        replaced
            """
def n = use Eq((x: int, y: int))(`==` = { p, q -> p.x == q.x }) in 1
"""
    Assert.Equal("Eq((x: int, y: int))(`==` = { p, q -> p.x == q.x })", at)

[<Fact>]
let ``A use gives an instance to a type that has none of its own`` () =
    let source =
        """
type Button = (label: string, onClick: () -> ())
def same = {
  use Eq(Button)(`==` = { p, q -> p.label == q.label })
  Button(label = "a", onClick = { -> () }) == Button(label = "a", onClick = { -> () })
}
"""
    compileClean (normalized source) |> ignore

[<Fact>]
let ``A use can't replace a built-in type's toString`` () =
    let message, _ =
        replaced
            """
def s = {
  use ToString(int)(toString = { pence -> "£{pence / 100}" })
  "{250}"
}
"""
    Assert.StartsWith("'int' already has `toString`: a `use` can't replace it.", message)

[<Fact>]
let ``A use can't replace an instance built from a type's attached definitions`` () =
    let message, at =
        replaced
            """
def n = {
  use (zero = 0, `+` = { x, y -> x * y }, `-` = { x, y -> x }, negate = { x -> x })
  1 + 2
}
"""
    Assert.Equal("(zero = 0, `+` = { x, y -> x * y }, `-` = { x, y -> x }, negate = { x -> x })", at)
    Assert.StartsWith("'int' already has its own Add: a `use` can't replace it.", message)

[<Fact>]
let ``A use of a context of a type variable is still allowed`` () =
    let source =
        """
def describe: [Eq(a)] a, a -> bool = { x, y ->
  use Eq(a)(`==` = { p, q -> true })
  x == y
}
"""
    compileClean (normalized source) |> ignore
