module Nanyx.Compiler.Tests.Features.ModuleValueContexts

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// Each diagnostic of `source`'s, as its code, the text it is reported at and its message.
let private errorsOf (source: string) =
    let source = source.ReplaceLineEndings "\n"
    (Compiler.compile source).Diagnostics
    |> List.map (fun diagnostic -> diagnostic.Code, textAt source diagnostic.Span.Value, diagnostic.Message)

let private undeclared = DiagnosticCodes.UndeclaredContext.Code

[<Fact>]
let ``A named module's value cannot call a function that requires a context`` () =
    let errors =
        errorsOf """
module config

def greeting = println("Hi")

dbg(0)
"""
    Assert.Equal<(string * string * string) list>(
        [ undeclared,
          "println",
          "'println' requires the context 'Console', which 'greeting' cannot have: a named module's top-level values are built without contexts, so make 'greeting' a function" ],
        errors)

[<Fact>]
let ``A named module's value cannot call a function whose contexts are inferred`` () =
    let errors =
        errorsOf """
module config

def hello = { () -> println("Hi") }
def greeting = hello()
"""
    Assert.Equal<(string * string) list>(
        [ undeclared, "hello" ],
        errors |> List.map (fun (code, text, _) -> code, text))

[<Fact>]
let ``A named module's function may require a context`` () =
    compileClean """
module config

def maxRetries = 3
def timeout = maxRetries * 10
def greet = { () -> println("Hi") }
"""
    |> ignore

[<Fact>]
let ``A lambda in a named module's value may use a context, since building the value does not run it`` () =
    compileClean """
module config

def handlers = [ { (x: string) -> println(x) } ]
"""
    |> ignore

[<Fact>]
let ``A lambda a named module's value passes to a call may be run, so it cannot use a context`` () =
    let errors =
        errorsOf """
module config

def printed = list.map([ "a" ], { x -> println(x) })
"""
    Assert.Equal<(string * string) list>([ undeclared, "println" ], errors |> List.map (fun (code, text, _) -> code, text))

[<Fact>]
let ``A script's value runs where the runtime's Console is`` () =
    compileClean """
def greeting = println("Hi")

dbg(0)
"""
    |> ignore

let private greeter = "context Greeter = (greet: string -> ())\n"

// A member a `use` gives a lambda runs with the instances where the `use` is, so the `Console` its lambda uses is the
// value's to compute, as a call's would be. It was left to the backend, which couldn't compile it (#2512).
[<Fact>]
let ``A script's value that uses a context through the member a use gives is NYX0211`` () =
    let errors = errorsOf (greeter + "\ndef result = use Greeter(greet = { name -> println(name) }) in greet(\"world\")\n")
    Assert.Equal<(string * string * string) list>(
        [ undeclared,
          "result",
          "'result' needs the context 'Console' to be computed, which only a script's statements run in: compute it in a statement, or make 'result' a function" ],
        errors)

[<Fact>]
let ``A script's statement may use a context through the member a use gives`` () =
    compileClean (greeter + "\nuse Greeter(greet = { name -> println(name) }) in greet(\"world\")\n") |> ignore
