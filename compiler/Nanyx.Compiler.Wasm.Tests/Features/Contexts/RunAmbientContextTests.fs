module TranspilerWasmTests.Features.Contexts.RunAmbientContexts

open Xunit
open TranspilerWasmTestHelpers

// A function that requires `Console`, imported from a module that imports `nanyx/console` itself, is passed the
// runtime's instance from the definitions the type checker records as the ambient context's members (#895).

let private main =
    """
module main

import greeter

@entry
def main = { greet("Ada") }
"""

let private run (greeter: string) =
    let files = [ "greeter.nyx", greeter; "main.nyx", main ] |> List.map (fun (path, text) -> path, text.ReplaceLineEndings "\n")
    fst (nanyxOnPackage files [ "run"; "{package}" ])

[<Fact>]
let ``Run - a context carried in with an imported function is the runtime's`` () =
    run """
module greeter

import nanyx/console

export def greet: [Console] string -> () = { name -> println("hello {name}") }
"""
    |> assertRan "hello Ada\n"

[<Fact>]
let ``Run - a context carried in from an aliased import is the runtime's`` () =
    run """
module greeter

import nanyx/console as c

export def greet: [c.Console] string -> () = { name -> c.println("hello {name}") }
"""
    |> assertRan "hello Ada\n"
