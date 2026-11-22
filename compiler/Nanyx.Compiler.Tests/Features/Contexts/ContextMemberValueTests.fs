module Nanyx.Compiler.Tests.Features.ContextMemberValue

open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

// `C(T).member` written as a value, without a call, has the member's type in the context's definition (#925).

let private contextSource =
    """context ToText(a) = (textlen: a -> int)
"""

/// The types of the `C(T).member` values in `source`, in source order.
let private memberValueTypes (source: string) =
    let typed = compileClean source
    typed.Module
    |> List.collect SyntaxTree.ofItem
    |> List.collect SyntaxTree.expressionsWithin
    |> List.filter (function Kind (ContextMemberAccess _) -> true | _ -> false)
    |> List.map (fun access -> Unifier.tyToString typed.ExprTypes.[access.Id])

[<Fact>]
let ``A context member as a value has the member's type`` () =
    let source =
        contextSource
        + """def apply: (int -> int), int -> int = { f, x -> f(x) }
def viaApply: [ToText(int)] int -> int = { n -> apply(ToText(int).textlen, n) }
def kept: [ToText(int)] () -> (int -> int) = { () ->
  def g = ToText(int).textlen
  g
}
"""
    Assert.Equal<string list>([ "int -> int"; "int -> int" ], memberValueTypes source)

[<Fact>]
let ``A context member as a value is checked against what uses it`` () =
    let source =
        contextSource
        + """def wrong: [ToText(int)] int -> string = { n -> ToText(int).textlen }
"""
    let code, at = singleError source
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.Equal("ToText(int).textlen", at)

// A plain context's function member named as a value requires the context where it is called, so what it holds of an
// instance can't outlive the instance's `use`.

/// `wrap`, of the type `wrapType`, giving back `Holder`'s member `get` as a value.
let private wrapping (wrapType: string) =
    $"context Holder = (get: () -> int)\n\ndef wrap: {wrapType} = {{ get }}\n"

[<Fact>]
let ``A plain context's member named as a value requires the context where it is called`` () =
    let typed = compileClean (normalized (wrapping "[Holder] () -> ([Holder] () -> int)"))
    Assert.Equal("() -> ([Holder] () -> int)", Unifier.tyToString typed.Types.["wrap"])

[<Fact>]
let ``So it can't be given back as a function that requires nothing`` () =
    let code, at = singleError (normalized (wrapping "[Holder] () -> (() -> int)"))
    Assert.Equal(DiagnosticCodes.ContextsNotAllowed.Code, code)
    Assert.Equal("get", at)
