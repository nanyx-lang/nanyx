module Nanyx.Compiler.Tests.Features.TypeBuilds

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A record description's `build` makes a value of the record from a function that gives each field's value, which must
// work for every field's type; a case's `build` makes a value of its union from its payload (see
// `TypeDescriptions.memberType`).

[<Fact>]
let ``A generic function builds records and union cases from their descriptions`` () =
    let typed =
        compileClean (
            normalized
                """def zeroOf: type a -> #some(a) | #error(string) = { t ->
  match t
    | #int -> #some(0)
    | #string -> #some("")
    | #list(elem) -> #some([])
    | #record(r) -> r.build { f -> zeroOf(f.type) }
    | #union(u) ->
      match u.cases
        | [c, ..._] ->
          match zeroOf(c.type)
            | #some(payload) -> #some(c.build(payload))
            | #error(e) -> #error(e)
        | [] -> #error("a union with no cases")
    | else -> #error("no zero")
}
"""
        )
    Assert.Equal("Type(a) -> #some(a) | #error(string)", Unifier.tyToString typed.Types.["zeroOf"])

[<Fact>]
let ``The function build is given must give a value of the field's type, whatever it is`` () =
    let code, at =
        singleError (
            normalized
                """def wrong: type a -> #some(a) | #error(string) = { t ->
  match t
    | #record(r) -> r.build { f -> #some(1) }
    | else -> #error("no")
}
"""
        )
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.Equal("{ f -> #some(1) }", at)

[<Fact>]
let ``A nominal type seen from outside its home module has no build`` () =
    let code, at =
        singleError (
            normalized
                """def make: type a -> #some(a) | #none = { t ->
  match t
    | #nominal(n) -> n.build { f -> #none }
    | else -> #none
}
"""
        )
    Assert.Equal(DiagnosticCodes.UnknownFunction.Code, code)
    Assert.Equal("n.build", at)
