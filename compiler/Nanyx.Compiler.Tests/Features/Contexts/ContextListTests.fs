module Nanyx.Compiler.Tests.Features.ContextLists

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// Each diagnostic compiling `source` gives: its code and the text it is reported at.
let private reported (source: string) =
    (Compiler.compile source).Diagnostics |> List.map (fun diagnostic -> diagnostic.Code, textAt source (spanOf diagnostic))

/// `source` with the only fix of its first diagnostic applied.
let private fixedFirst (source: string) =
    let diagnostic = List.head (Compiler.compile source).Diagnostics
    applyFix source (Assert.Single diagnostic.Fixes)

[<Fact>]
let ``Contexts separated by commas are required together`` () =
    let typed =
        compileClean (
            normalized """def both: [Eq(a), Hash(a)] a, a -> bool = { x, y -> x == y and hash(x) == hash(y) }
def b = both(1, 2)
"""
        )
    Assert.Equal("bool", Unifier.tyToString typed.Types.["b"])

[<Fact>]
let ``Contexts joined with & or + are an error whose fix writes a comma`` () =
    let source = "def both: [Eq(a) & Hash(a)] a, a -> bool = { x, y -> x == y and hash(x) == hash(y) }\n"
    Assert.Equal<(string * string) list>([ DiagnosticCodes.ContextsJoined.Code, "&" ], reported source)
    Assert.Equal(source.Replace("Eq(a) & Hash(a)", "Eq(a), Hash(a)"), fixedFirst source)
    let plus = "def both: [Eq(a) + Hash(a), Console] a, a -> bool = { x, y -> x == y }\n"
    Assert.Equal<(string * string) list>([ DiagnosticCodes.ContextsJoined.Code, "+" ], reported plus)
    Assert.Equal(plus.Replace("Eq(a) + Hash(a)", "Eq(a), Hash(a)"), fixedFirst plus)

[<Fact>]
let ``A context variable joined with & is an error too`` () =
    let source = "def run: [c] ([Log & c] () -> ()) -> () = { f -> use Log(log = { m -> () }) in f() }\n"
    Assert.Equal<(string * string) list>([ DiagnosticCodes.ContextsJoined.Code, "&" ], reported source)
    Assert.Equal(source.Replace("Log & c", "Log, c"), fixedFirst source)

[<Fact>]
let ``An operator required by its member is an error whose fix writes the prelude's context that has it`` () =
    let cases =
        [ "def add: [`+`: a, a -> a] a, a -> a = { x, y -> x + y }\n", [ "`+`: a, a -> a" ], "[Add(a)]"
          "def sub: [`-`: (a, a) -> a] a, a -> a = { x, y -> x - y }\n", [ "`-`: (a, a) -> a" ], "[Sub(a)]"
          "def lerp: [(`+`: a, a -> a), (`*`: a, a -> a)] a, a, a -> a = { a, b, t -> a + b * t }\n",
          [ "(`+`: a, a -> a)"; "(`*`: a, a -> a)" ],
          "[Num(a)]"
          "def f: [(`+`: a, a -> a), Eq(a)] a, a -> a = { x, y -> if x == y then x + x else x }\n", [ "(`+`: a, a -> a)" ], "[Add(a), Eq(a)]" ]
    for source, members, written in cases do
        Assert.Equal<(string * string) list>(members |> List.map (fun text -> DiagnosticCodes.OperatorRequiredByMember.Code, text), reported source)
        let fixedSource = fixedFirst source
        Assert.StartsWith($"def {source.Split(' ').[1].TrimEnd(':')}: {written} ", fixedSource)
        Assert.Empty((Compiler.compile fixedSource).Diagnostics)

[<Fact>]
let ``Only an operator of a prelude context, at its member's type, could be required by its member`` () =
    Assert.Equal(
        (DiagnosticCodes.OperatorRequiredByMember.Code, "Only an operator can be required by its member, and 'toString' is not one: it is a member of ToString, so require [ToString(a)]", "toString: a -> string"),
        onlyDiagnostic (withFinalNewline """def write: [toString: a -> string] a -> string = { x -> "Wrote: {x}" }"""))
    Assert.Equal(
        (DiagnosticCodes.OperatorRequiredByMember.Code, "No context in the prelude has a member '`<`': it is read through Ord's 'compare', so require [Ord(a)]", "`<`: a, a -> bool"),
        onlyDiagnostic "def less: [`<`: a, a -> bool] a, a -> bool = { x, y -> x < y }\n")
    Assert.Equal(
        (DiagnosticCodes.OperatorRequiredByMember.Code, "'`+`' is 'a, a -> a' in Add(a), which 'a -> a' is not for any 'a'", "a -> a"),
        onlyDiagnostic "def add: [`+`: a -> a] a -> a = { x -> x }\n")
