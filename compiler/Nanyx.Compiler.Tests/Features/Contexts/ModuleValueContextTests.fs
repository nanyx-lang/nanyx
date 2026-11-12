/// A top-level value of a named module runs in no context: one that needs the runtime's `Console` is an error.
module Nanyx.Compiler.Tests.Features.ModuleValueContexts

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The diagnostics compiling `source` gives, which must all be undeclared contexts: the text each is reported at, and
/// its message.
let private undeclaredContexts (source: string) =
    let source = source.ReplaceLineEndings "\n"
    let result = Compiler.compile source
    for diagnostic in result.Diagnostics do
        Assert.Equal(DiagnosticCodes.UndeclaredContext.Code, diagnostic.Code)
    result.Diagnostics |> List.map (fun diagnostic -> textAt source (spanOf diagnostic), diagnostic.Message)

[<Fact>]
let ``A top-level value of a named module cannot call a member of the runtime's Console`` () =
    match undeclaredContexts "module config\n\nimport nanyx/console\n\ndef greeting = println(\"Hi\")\n" with
    | [ text, message ] ->
        Assert.Equal("println", text)
        Assert.Contains("the context 'Console', which 'greeting' cannot have: a top-level value of a named module runs in no context", message)
    | other -> failwith $"Expected one diagnostic, got %A{other}"

[<Fact>]
let ``A top-level value of a named module cannot call a function that requires a context`` () =
    let source =
        """
module config

import nanyx/console

def greet: [Console] () -> unit = { () -> println("Hi") }
def greetInferred = { () -> println("Hi") }

def greeting = greet()
def inferredGreeting = greetInferred()
"""
    match undeclaredContexts source with
    | [ "greet", first; "greetInferred", second ] ->
        Assert.Contains("'greet' requires the context 'Console', which 'greeting' cannot have", first)
        Assert.Contains("'greetInferred' requires the context 'Console', which 'inferredGreeting' cannot have", second)
    | other -> failwith $"Expected two diagnostics, got %A{other}"

[<Fact>]
let ``A named module's context-free values and its functions that use Console are fine`` () =
    let source =
        """
module config

import nanyx/console

def maxRetries = 3
def timeout = maxRetries * 10
def greet = { () -> println("Hi") }
def greeter = { name: string -> println("Hi {name}") }
"""
    Assert.Empty(undeclaredContexts source)

[<Fact>]
let ``A top-level value of a script runs in the runtime's Console`` () =
    Assert.Empty(undeclaredContexts "import nanyx/console\n\ndef greeting = println(\"Hi\")\n")
