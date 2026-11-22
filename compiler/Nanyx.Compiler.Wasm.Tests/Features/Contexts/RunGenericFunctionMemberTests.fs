module TranspilerWasmTests.Features.Contexts.RunGenericFunctionMember

open Xunit
open TranspilerWasmTestHelpers

// A generic function given as a context member, or as a field of a record, at a concrete type is that function at the
// member's type, with the instances of the contexts it requires found at that type, as a call to it would find them
// (#2323).

[<Fact>]
let ``Run - println is the member of a context that logs strings`` () =
    let source =
        """context Log = (log: string -> ())

def greet: [Log] string -> () = { name -> log("Hello, {name}!") }

use Log(log = println) in greet("Nanyx")
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "Hello, Nanyx!\n"

[<Fact>]
let ``Run - a generic function of the program's own is a context member at a concrete type`` () =
    let source =
        """context Show = (show: int -> string)

def describe: [ToString(a)] a -> string = { x -> "<{x}>" }

def shown: [Show] int -> string = { n -> show(n) }

println(use Show(show = describe) in shown(3))
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "<3>\n"

[<Fact>]
let ``Run - a generic function is a record's field at a concrete type`` () =
    let source =
        """def describe: [ToString(a)] a -> string = { x -> "<{x}>" }

def shows: (int: int -> string, text: string -> string) = (int = describe, text = describe)

println(shows.int(3))
println(shows.text("x"))
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "<3>\n<x>\n"
