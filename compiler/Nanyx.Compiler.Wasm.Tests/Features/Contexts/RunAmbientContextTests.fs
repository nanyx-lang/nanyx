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

// A function that does not write its type, and is inferred to require the ambient `Console`, is passed the members of
// the instance where it is called, as one that declares `[Console]` is (#1245).

[<Fact>]
let ``Run - a function inferred to require Console writes through the instance a use loads where it is called`` () =
    let source =
        """import nanyx/console

def shout = { text -> println(text ++ "!") }

def main = {
  shout("plain")
  use Console(
    write = { text -> write("[" ++ text ++ "]") }
    writeError = writeError
    readLine = readLine
    readToEnd = readToEnd
    flush = flush
  )
  shout("framed")
}

main()
"""
    runSource source |> assertRan "plain!\n[framed!][\n]"
