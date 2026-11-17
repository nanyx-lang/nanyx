module Nanyx.Compiler.Tests.Features.AssociatedTypeContexts

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A context the value of a def needs of an associated type its signature names, as a hole `"{item}"` in a `for` over an
// `[Iterable(i)] i` needs `ToString(elem)`: a def that writes its type must write it, as for any of its type
// variables (#2018).

[<Fact>]
let ``Interpolating an element of an iterable a def's type names needs ToString of its associated type`` () =
    let source =
        normalized
            """def iterate: [Iterable(i), Console] i -> () = { seq ->
  for seq | item ->
    println("Got item: {item}")
}
"""
    let message, at = missingContext source
    Assert.Equal(
        "Interpolating a value of type 'elem' requires the context 'ToString(elem)', which is not available here: add [ToString(elem)] to the type of 'iterate'",
        message
    )
    Assert.Equal("item", at)

[<Fact>]
let ``The fix for a missing ToString of an associated type writes it in the def's type`` () =
    let source =
        normalized
            """def iterate: [Iterable(i), Console] i -> () = { seq ->
  for seq | item ->
    println("Got item: {item}")
}
"""
    let after, _ = fixed' source
    Assert.Contains("def iterate: [Iterable(i), Console, ToString(elem)] i -> ()", after)
    compileClean after |> ignore

[<Fact>]
let ``Comparing elements of an iterable a def's type names needs Eq of its associated type`` () =
    let source =
        normalized
            """def same: [Iterable(i), Console] i -> () = { xs ->
  for xs | a ->
    if a == a then println("eq") else ()
}
"""
    let message, at = missingContext source
    Assert.Equal("'same' compares values of type 'elem' with ==, so its type must require Eq(elem): add [Eq(elem)] to the type of 'same'", message)
    Assert.Equal("a == a", at)

[<Fact>]
let ``Adding elements of an iterable a def's type names needs Add of its associated type`` () =
    let source =
        normalized
            """def keep: x -> () = { _ -> () }

def total: [Iterable(i)] i -> () = { xs ->
  for xs | a ->
    keep(a + a)
}
"""
    let message, at = missingContext source
    Assert.Equal("'total' adds values of type 'elem' with +, so its type must require Add(elem): add [Add(elem)] to the type of 'total'", message)
    Assert.Equal("a + a", at)

[<Fact>]
let ``Calling a function that needs a context of an element of an iterable a def's type names needs it written`` () =
    let source =
        normalized
            """def show: [ToString(a)] a -> string = { v -> "{v}" }

def all: [Iterable(i), Console] i -> () = { xs ->
  for xs | a ->
    println(show(a))
}
"""
    let message, at = missingContext source
    Assert.Equal("'show' requires the context 'ToString', which is not available here", message)
    Assert.Equal("show", at)

[<Fact>]
let ``A call to a def that writes the context of its associated type needs an instance at the call`` () =
    let source =
        normalized
            """import nanyx/stack

def iterate: [Iterable(i), ToString(elem), Console] i -> () = { seq ->
  for seq | item ->
    println("Got item: {item}")
}

def x: Stack(a -> b) = []

iterate(x)
"""
    let message, at = missingContext source
    Assert.Equal("'iterate' requires the context 'ToString(a -> b)', and 'a -> b' is a function: functions have no toString", message)
    Assert.Equal("iterate", at)

[<Fact>]
let ``A def that writes no type infers the context it needs of the elements it iterates`` () =
    let source =
        normalized
            """def iterate = { seq ->
  for seq | item ->
    println("Got item: {item}")
}

iterate([{ x: int -> x }])
"""
    let message, at = missingContext source
    Assert.Equal("'iterate' requires the context 'ToString(int -> int)', and 'int -> int' is a function: functions have no toString", message)
    Assert.Equal("iterate", at)
