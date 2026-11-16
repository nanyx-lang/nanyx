module Nanyx.Compiler.Tests.Features.NestedCatchAlls

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A name bound by a catch-all inside a pattern, where an earlier arm takes every value of a tag, is never that tag
// (#1455): it has the union without it, and with only the rest left, the rest's type.

[<Fact>]
let ``A catch-all in a list pattern after an arm that takes a tag there has the union's rest`` () =
    let typed =
        compileClean
            """
def allSomeOnto: list(#some(a) | r), list(a) -> #some(list(a)) | r = {
  | [], acc -> #some(list.reverse(acc))
  | [#some(x), ...rest], acc -> allSomeOnto(rest, [x, ...acc])
  | [other, ...rest], acc -> other
}

def all = allSomeOnto([#some(1), #some(2)], [])
def stopped = allSomeOnto([#some(1), #none], [])
"""
    Assert.Equal("#some(list(int))", Unifier.tyToString typed.Types.["all"])
    Assert.Equal("#some(list(int)) | #none", Unifier.tyToString typed.Types.["stopped"])

[<Fact>]
let ``A catch-all in a tuple pattern after an arm that takes a tag there has the union's rest`` () =
    compileClean
        """
def label: (#some(int) | r, string) -> #some(string) | r = { pair ->
  match pair
    | (#some(n), name) -> #some("{name} {n}")
    | (other, _) -> other
}
"""
    |> ignore

[<Fact>]
let ``A catch-all in a tag's payload after an arm that takes a tag there has the union's rest`` () =
    compileClean
        """
def unwrap: #wrapped(#some(int) | r) -> #some(string) | r = {
  | #wrapped(#some(n)) -> #some("{n}")
  | #wrapped(other) -> other
}
"""
    |> ignore

[<Fact>]
let ``A nested catch-all that can still be the tag has the whole union`` () =
    // The first arm takes only lists of one item, so `other` in a longer list can still be a `#some`.
    let result =
        Compiler.compile (
            normalized
                """def allSomeOnto: list(#some(a) | r), list(a) -> #some(list(a)) | r = {
  | [], acc -> #some(list.reverse(acc))
  | [#some(x)], acc -> #some(list.reverse([x, ...acc]))
  | [other, ...rest], acc -> other
}
"""
        )
    assertReports DiagnosticCodes.TypeMismatch result
