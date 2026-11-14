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

import nanyx/console

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

import nanyx/console

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

import nanyx/console

def maxRetries = 3
def timeout = maxRetries * 10
def greet = { () -> println("Hi") }
"""
    |> ignore

[<Fact>]
let ``A lambda in a named module's value may use a context, since building the value does not run it`` () =
    compileClean """
module config

import nanyx/console

def handlers = [ { (x: string) -> println(x) } ]
"""
    |> ignore

[<Fact>]
let ``A lambda a named module's value passes to a call may be run, so it cannot use a context`` () =
    let errors =
        errorsOf """
module config

import nanyx/console

def printed = list.map([ "a" ], { x -> println(x) })
"""
    Assert.Equal<(string * string) list>([ undeclared, "println" ], errors |> List.map (fun (code, text, _) -> code, text))

[<Fact>]
let ``A script's value runs where the runtime's Console is`` () =
    compileClean """
import nanyx/console

def greeting = println("Hi")

dbg(0)
"""
    |> ignore
