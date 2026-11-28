module Nanyx.Compiler.Tests.Features.Linters.ResultOnlyTypeVariable

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

let private code = "default/resultOnlyTypeVariable"

let private decode = "context Decode(a) = (decode: string -> a)\n\n"

/// What `default/resultOnlyTypeVariable` reports for `source`, compiled with every linter on: each diagnostic's message
/// with the text it is reported at.
let private reported (source: string) =
    let source = normalized source
    let result = Compiler.compileWith (everyLinter ()) source
    Assert.True(result.TypeChecked && not (result.Diagnostics |> List.exists Diagnostics.isError), sprintf "%A" result.Diagnostics)
    result.Diagnostics
    |> List.filter (fun diagnostic -> diagnostic.Code = code)
    |> List.map (fun diagnostic ->
        Assert.Equal(Severity.Warning, diagnostic.Severity)
        diagnostic.Message, textAt source (spanOf diagnostic))

[<Fact>]
let ``A context's type variable only in the result is reported at the def's name`` () =
    Assert.Equal<(string * string) list>(
        [ "Only its result has 'a', which a context it requires names, so nothing at a call says which type it is but a type written on the result: take it as a type argument, `Type(a)`, or a named one, `a: Type(a)`, which a call passes as `type T`",
          "parse" ],
        reported (decode + "def parse: [Decode(a)] string -> a = { json -> decode(json) }\n"))
    Assert.Equal<string list>([ "fallback" ], reported "def fallback: [Default(a)] () -> a = { -> default }\n" |> List.map snd)

[<Fact>]
let ``A def whose type is inferred is reported too`` () =
    let source = decode + "def parse: [Decode(a)] string -> a = { json -> decode(json) }\n\ndef read = { s -> parse(s) }\n"
    Assert.Equal<string list>([ "parse"; "read" ], reported source |> List.map snd)

[<Theory>]
[<InlineData("def parse: [Decode(a)] (Type(a), string) -> a = { t, json -> decode(json) }\n")>]
[<InlineData("def parse: [Decode(into)] (string, into: Type(into)) -> into = { json, into -> decode(json) }\n")>]
[<InlineData("def total: [Add(a)] list(a) -> a = { xs -> xs \\head ?? crash(\"none\") }\n")>]
[<InlineData("def twice: [Num(a)] a -> a = { x -> x + x }\n")>]
[<InlineData("def first: [Iterable(c, elem = a)] c -> a = { xs -> crash(\"none\") }\n")>]
let ``A type variable a parameter fixes is not reported`` (definition: string) =
    Assert.Empty(reported (decode + definition))
