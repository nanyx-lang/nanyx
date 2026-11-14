module Nanyx.Compiler.Tests.Features.StdLib.StandardLibraryCache

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// The standard library is checked once per process and shared by every compilation after (#694). These programs use
// the same generic prelude functions at different types, so a type variable of the shared prelude that leaked into a
// later compilation, or a change one compilation made to it, would show in the next one's types or diagnostics.

let private withInts =
    normalized """def name: #some(int) | #empty = #some(1)
def called = mapSome(name, { n -> n + 1 })
def r: Result(int, string) = #error("x")
def mapped = r \ Result.map({ n -> 1.5 })
def listed = name \ someToList"""

let private withStrings =
    normalized """def name: #some(string) | #empty = #some("a")
def called = mapSome(name, { s -> true })
def r: Result(string, int) = #error(1)
def mapped = r \ Result.map({ s -> [s] })
def listed = name \ someToList"""

let private withAnError =
    normalized """def name: #some(int) | #empty = #some(1)
def called: #some(string) | #empty = mapSome(name, { n -> n + 1 })"""

/// What a compilation found: its diagnostics, the type of each definition, and the type of each node of the
/// program in the order they are written (node ids differ between compilations; their order does not).
let private outcome (source: string) =
    let result = Compiler.compile source
    let typed = result.Typed.Value
    let shown (types: Map<_, Ty>) = types |> Map.toList |> List.map (snd >> Unifier.tyToString)
    result.Diagnostics |> List.map (fun diagnostic -> diagnostic.Code, diagnostic.Message, diagnostic.Span),
    typed.Types |> Map.map (fun _ ty -> Unifier.tyToString ty) |> Map.toList,
    shown typed.ExprTypes

[<Fact>]
let ``Compilations in a row that use the same generic prelude functions each get the types they get alone`` () =
    let ints = outcome withInts
    let strings = outcome withStrings
    let error = outcome withAnError
    let _, intTypes, _ = ints
    let _, stringTypes, _ = strings
    Assert.Equal<(string * string) list>(
        [ "called", "#some(int) | #empty"; "listed", "list(int)"; "mapped", "Result(float, string)"; "name", "#some(int) | #empty"; "r", "Result(int, string)" ],
        intTypes |> List.filter (fun (name, _) -> List.contains name [ "called"; "listed"; "mapped"; "name"; "r" ]))
    Assert.Equal<(string * string) list>(
        [ "called", "#some(bool) | #empty"; "listed", "list(string)"; "mapped", "Result(list(string), int)"; "name", "#some(string) | #empty"; "r", "Result(string, int)" ],
        stringTypes |> List.filter (fun (name, _) -> List.contains name [ "called"; "listed"; "mapped"; "name"; "r" ]))
    let errors, _, _ = error
    Assert.Equal<string list>([ DiagnosticCodes.TypeMismatch.Code ], errors |> List.map (fun (code, _, _) -> code))
    // Each again, after the others: everything the first compilation of each found is the same.
    Assert.Equal(strings, outcome withStrings)
    Assert.Equal(ints, outcome withInts)
    Assert.Equal(error, outcome withAnError)
    Assert.Equal(strings, outcome withStrings)
