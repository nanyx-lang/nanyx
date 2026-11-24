module Nanyx.Compiler.Tests.Features.TypeMatches

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A match on a `Type(a)` inspects the type it stands for: an arm whose tag says what `a` is narrows it, and a record's
// fields package types the checker never learns (see `TypeMatches`, `TypeDescriptions`).

let private describe =
    """def describe: a, Type(a) -> string = { value, t ->
  match t
    | #int -> "int {value + 1}"
    | #string -> "string " ++ value
    | #list(elem) -> value \list.map { x -> describe(x, elem) } \string.join(", ")
    | #map(k, v) -> "a map"
    | #record(r) -> r.fields \list.map { f -> f.name ++ " = " ++ describe(f.get(value), f.type) } \string.join(", ")
    | #union(u) -> u.cases \list.map { c -> c.name } \string.join(" | ")
    | #nominal(n) -> n.name ++ " from " ++ n.module
    | else -> "?"
}
"""

[<Fact>]
let ``Each arm of a Type match narrows the type, and a field's packaged type checks a recursive call`` () =
    let typed = compileClean (normalized describe)
    Assert.Equal("a, Type(a) -> string", Unifier.tyToString typed.Types.["describe"])

[<Fact>]
let ``An arm gives the match's expected type with the type narrowed in it`` () =
    compileClean (
        normalized
            """def parse: string, Type(a) -> #some(a) | #none = { text, t ->
  match t
    | #int ->
        match int.fromString(text)
          | #some(n) -> #some(n)
          | _ -> #none
    | #string -> #some(text)
    | else -> #none
}
"""
    )
    |> ignore

[<Fact>]
let ``A value of the narrowed type is only the type its arm says`` () =
    let code, at =
        singleError (
            normalized
                """def wrong: a, Type(a) -> string = { value, t ->
  match t
    | #int -> value
    | else -> ""
}
"""
        )
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.Equal("value", at)

[<Fact>]
let ``A Type match that leaves out a tag is non-exhaustive`` () =
    let code, message, at =
        onlyDiagnostic (
            normalized
                """def kind: Type(a) -> string = { t ->
  match t
    | #int -> "int"
    | #string -> "string"
}
"""
        )
    Assert.Equal(DiagnosticCodes.NonExhaustiveMatch.Code, code)
    Assert.StartsWith("The type's description may be #float, #bool", message)
    Assert.Equal("match t", at)

[<Fact>]
let ``A pattern that isn't a description's tag is reported`` () =
    let code, message, at =
        onlyDiagnostic (
            normalized
                """def kind: Type(a) -> string = { t ->
  match t
    | #integer -> "int"
    | else -> "other"
}
"""
        )
    Assert.Equal(DiagnosticCodes.TypeDescriptionPattern.Code, code)
    Assert.StartsWith("'#integer' is not a tag of a type's description", message)
    Assert.Equal("#integer", at)

[<Fact>]
let ``A tag's payload is matched by a name`` () =
    let code, at =
        singleError (
            normalized
                """def kind: Type(a) -> string = { t ->
  match t
    | #list(#int) -> "ints"
    | else -> "other"
}
"""
        )
    Assert.Equal(DiagnosticCodes.TypeDescriptionPattern.Code, code)
    Assert.Equal("#int", at)

[<Fact>]
let ``A field's value can't leave the lambda that opened it`` () =
    let code, message, at =
        onlyDiagnostic (
            normalized
                """def values: a, Type(a) -> int = { value, t ->
  match t
    | #record(r) ->
        def got = r.fields \list.map { f -> f.get(value) }
        0
    | else -> 0
}
"""
        )
    Assert.Equal(DiagnosticCodes.PackagedTypeEscapes.Code, code)
    Assert.StartsWith("This gives a value of 'b', whose type is the type of a field", message)
    Assert.Equal("f.get(value)", at)

[<Fact>]
let ``An element's type can't leave its arm`` () =
    let code, at =
        singleError (
            normalized
                """def elementType: Type(a) -> int = { t ->
  def found =
    match t
      | #list(elem) -> [elem]
      | else -> []
  0
}
"""
        )
    Assert.Equal(DiagnosticCodes.PackagedTypeEscapes.Code, code)
    Assert.Equal("[elem]", at)

[<Fact>]
let ``A description type can be written in an annotation`` () =
    let typed =
        compileClean (
            normalized
                """def fieldNames: RecordInfo(a) -> list(string) = { r -> r.fields \list.map { f -> f.name } }
"""
        )
    Assert.Equal("RecordInfo(a) -> list(string)", Unifier.tyToString typed.Types.["fieldNames"])
