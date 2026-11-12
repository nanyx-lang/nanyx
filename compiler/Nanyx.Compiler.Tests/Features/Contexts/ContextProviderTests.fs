module Nanyx.Compiler.Tests.Features.ContextProviders

open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

/// What provides each context each call in `source` requires (see `TypedModule.ContextProviders`), in the order the
/// calls are written: the name called, the context, and the provider, as the signature or `use` that provides it is
/// written.
let private providersIn (source: string) =
    let source = source.ReplaceLineEndings "\n"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let typed = result.Typed.Value
    let expressions = result.Syntax.Value |> List.collect SyntaxTree.ofItem |> List.collect SyntaxTree.expressionsWithin
    let spans = expressions |> List.map (fun expr -> expr.Id, expr.Span) |> Map.ofList
    let describe provider =
        match provider with
        | FromSignature span -> $"signature {textAt source span}"
        | FromUse value -> $"use {textAt source spans.[value]}"
        | FromInference -> "inferred"
        | FromRuntime -> "runtime"
    expressions
    |> List.choose (fun expr ->
        match expr.Kind with
        | FunctionCall(name, _, _)
        | Pipe(_, name, _, _) -> typed.ContextProviders |> Map.tryFind expr.Id |> Option.map (fun provided -> expr.Span.Start, name, provided)
        | _ -> None)
    |> List.sortBy (fun (start, _, _) -> start)
    |> List.collect (fun (_, name, provided) ->
        provided
        |> List.map (fun p ->
            name, Unifier.tyToString p.Context, describe p.Provider))

let private logContext = """
context Log = (log: string -> ())

def note: [Log] string -> () = { s -> log(s) }
"""

[<Fact>]
let ``A context the def's type declares is provided by its signature`` () =
    let source = logContext + """
def twice: [Log] string -> () = { s ->
  note(s)
  note(s)
}
"""
    Assert.Equal<(string * string * string) list>(
        [ "log", "Log", "signature Log"
          "note", "Log", "signature Log"
          "note", "Log", "signature Log" ],
        providersIn source)

[<Fact>]
let ``A context a use loads is provided by the use, in a call and in a pipe`` () =
    let source = logContext + """
def main = {
  use Log(log = { s -> () })
  note("a")
  "b" \note
}
"""
    Assert.Equal<(string * string * string) list>(
        [ "log", "Log", "signature Log"
          "note", "Log", "use Log(log = { s -> () })"
          "note", "Log", "use Log(log = { s -> () })" ],
        providersIn source)

[<Fact>]
let ``The nearest use provides the context`` () =
    let source = logContext + """
def main: [Log] () -> () = {
  note("outer")
  use Log(log = { s -> () }) in note("inner")
}
"""
    Assert.Equal<(string * string * string) list>(
        [ "log", "Log", "signature Log"
          "note", "Log", "signature Log"
          "note", "Log", "use Log(log = { s -> () })" ],
        providersIn source)

[<Fact>]
let ``The runtime's Console is provided by inference in an unannotated def, and by the runtime at the top level`` () =
    let source = """import nanyx/console

def greet = { println("hi") }

println("top")
"""
    Assert.Equal<(string * string * string) list>(
        [ "println", "Console", "inferred"
          "println", "Console", "runtime" ],
        providersIn source)

[<Fact>]
let ``A call of an unannotated def records its contexts once they are inferred`` () =
    let source = """import nanyx/console

def shout = { println("hi") }

def loud: [Console] () -> () = { shout() }

def quiet = { shout() }

shout()
"""
    Assert.Equal<(string * string * string) list>(
        [ "println", "Console", "inferred"
          "shout", "Console", "signature Console"
          "shout", "Console", "inferred"
          "shout", "Console", "runtime" ],
        providersIn source)

[<Fact>]
let ``A context a function value requires is provided where the call that runs it is`` () =
    let source = """
context Foo = (bar: string -> string)

def useFoo: [Foo] string -> string = { s -> bar(s) }

def run2 = { f, a -> f(a) }

def pass: [Foo] string -> string = { s -> run2(useFoo, s) }
"""
    Assert.Equal<(string * string * string) list>(
        [ "bar", "Foo", "signature Foo"
          "run2", "Foo", "signature Foo" ],
        providersIn source)

let private showContext = """
context Show(a) = (show: a -> string)

type Point = (x: int, y: int)

def describe: [Show(a)] a -> string = { x -> show(x) }
"""

[<Fact>]
let ``An instance of a parameterised context the def's type declares is provided by its signature`` () =
    let source = showContext + """
def twice: [Show(a)] a -> string = { x -> describe(x) }
"""
    Assert.Equal<(string * string * string) list>(
        [ "describe", "Show(a)", "signature Show(a)" ],
        providersIn source)

[<Fact>]
let ``An instance of a parameterised context a use loads is provided by the use`` () =
    let source = showContext + """
def showPoint: Show(Point) = (show = { p -> "p" })

def main = {
  use showPoint
  describe(Point(x = 1, y = 2))
  use Show(show = { n: int -> "n" }) in describe(5)
}
"""
    Assert.Equal<(string * string * string) list>(
        [ "describe", "Show(Point)", "use showPoint"
          "describe", "Show(int)", """use Show(show = { n: int -> "n" })""" ],
        providersIn source)

[<Fact>]
let ``An instance of a parameterised context an unannotated def is inferred to require is provided by inference`` () =
    let source = showContext + """
def twice = { x -> describe(x) }
"""
    Assert.Equal<(string * string * string) list>(
        [ "describe", "Show(a)", "inferred" ],
        providersIn source)

[<Fact>]
let ``An instance the compiler builds from attached definitions has no provider`` () =
    let source = showContext + """
def Point.show: Point -> string = { p -> "p" }

def main = { describe(Point(x = 1, y = 2)) }
"""
    Assert.Equal<(string * string * string) list>([], providersIn source)
