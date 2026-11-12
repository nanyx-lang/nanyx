module Nanyx.Compiler.Tests.Features.AliasedContexts

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A def can declare a context from a module imported under an alias (`[c.Console]`), and its callers supply it from
// the ambient instance, whose members are in scope under the alias (#736).

[<Fact>]
let ``A def that declares an aliased Console can be called from the entry point`` () =
    compileClean """module main
import nanyx/console as c

def g: [c.Console] () -> () = { c.println("5") }

@entry
def main = { g() }
"""
    |> ignore

[<Fact>]
let ``A def that declares Console from an unaliased import can be called from the entry point`` () =
    compileClean """module main
import nanyx/console

def g: [Console] () -> () = { println("5") }

@entry
def main = { g() }
"""
    |> ignore

[<Fact>]
let ``An aliased Console passes from one def that declares it to another`` () =
    compileClean """module main
import nanyx/console as c

def g: [c.Console] () -> () = {
  c.write("a")
  c.flush()
}

def h: [c.Console + ToString(a)] a -> () = { value ->
  g()
  c.println(value)
}

@entry
def main = { h([1]) }
"""
    |> ignore

[<Fact>]
let ``A def that does not declare an aliased Console cannot call one that does`` () =
    let source = """module main
import nanyx/console as c

def g: [c.Console] () -> () = { c.println("5") }

def k: () -> () = { g() }
"""
    match (Compiler.compile source).Diagnostics with
    | [ diagnostic ] ->
        Assert.Equal(DiagnosticCodes.UndeclaredContext.Code, diagnostic.Code)
        Assert.Equal(
            "'g' requires the context 'c.Console', which 'k' does not declare: add [c.Console] to the type of 'k', or load an instance with `use`",
            diagnostic.Message)
    | other -> failwith $"Expected one diagnostic, got %A{other}"

[<Fact>]
let ``A def that declares a context of a user module imported under an alias can be called from the entry point`` () =
    let greeter = """module greeter
export context Greeter = (greeting: string -> string)
export def greeting: string -> string = { name -> "hello " ++ name }
"""
    let main = """module main
import mod as gr

def g: [gr.Greeter] () -> string = { gr.greeting("ada") }

@entry
def main = { g() }
"""
    let result = compileImporting greeter main
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
