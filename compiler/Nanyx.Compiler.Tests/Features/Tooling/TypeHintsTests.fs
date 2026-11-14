module Nanyx.Compiler.Tests.Features.Tooling.TypeHints

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

// Type hints: the inferred types shown for declarations that do not write theirs.

/// Each hint as its name, type and where its name starts.
let private typeHints (hintsOf: TypeNotation -> string -> CompileResult -> TypeHints.TypeHint list) (source: string) =
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    hintsOf Unicode "" result |> List.map (fun hint -> hint.Name, hint.Type, hint.Span.Start)

[<Fact>]
let ``Lenses show the types of top-level defs that do not write theirs`` () =
    let source =
        normalized """def id = { x -> x }
def answer: int = 42
def pair = { a, b -> (a, b) }
def (p, q) = (1, "s")
"""
    Assert.Equal<(string * string * int) list>(
        [ "id", "α -> α", offsetOf source "id" 0 0
          "pair", "α, β -> (α, β)", offsetOf source "pair" 0 0
          "p", "int", offsetOf source "p," 0 0
          "q", "string", offsetOf source "q)" 0 0 ],
        typeHints TypeHints.typeLenses source)

[<Fact>]
let ``Inlay hints show the types of locals, parameters and pattern variables`` () =
    let source =
        normalized """def scale = { factor ->
  def offset = 1
  def written: int = 2
  factor * written + offset
}
def rest = { items -> match items
  | [_, ...tail] -> tail
  | _ -> items }
"""
    Assert.Equal<(string * string * int) list>(
        [ "factor", "int", offsetOf source "factor" 0 0
          "offset", "int", offsetOf source "offset" 0 0
          "items", "list(α)", offsetOf source "items" 0 0
          "tail", "list(α)", offsetOf source "tail" 0 0 ],
        typeHints localInlayHints source)

[<Fact>]
let ``A shorthand lambda's argument is not written, so it has no inlay hint`` () =
    let source = "def divisibleBy = { n -> { % n == 0 } }\n"
    Assert.Equal<(string * string * int) list>(
        [ "n", "int", offsetOf source "n ->" 0 0 ], typeHints localInlayHints source)

[<Fact>]
let ``Each hint says what kind of declaration it is for`` () =
    let source =
        normalized """def scale = { factor ->
  def offset = 1
  factor * offset
}
def rest = { items -> match items
  | [_, ...tail] -> tail
  | _ -> items }
"""
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Assert.Equal<(string * SymbolKind) list>(
        [ "scale", TopLevelValue
          "factor", Parameter
          "offset", LocalValue
          "rest", TopLevelValue
          "items", Parameter
          "tail", PatternVariable ],
        TypeHints.inlayHintsWithTopLevel Unicode "" result |> List.map (fun hint -> hint.Name, hint.Kind))

[<Fact>]
let ``A def that writes only its contexts has its type shown after them`` () =
    let source =
        normalized """context Log = (log: string -> ())
def greet: [Log] = { name -> log(name) }
def twice: [Log] = { s ->
  def say: [Log] = { line -> log(line) }
  say(s)
  say(s)
}
"""
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    let hints =
        TypeHints.inlayHintsWithTopLevel Unicode "" result
        |> List.map (fun hint -> hint.Name, hint.Type, hint.Context |> Option.map (fun span -> span.Start))
    let contextAt occurrence = Some (offsetOf source "[Log]" occurrence 0)
    Assert.Equal<(string * string * int option) list>(
        [ "greet", "string -> ()", contextAt 0
          // A parameter writes no context of its own, though its def does.
          "name", "string", None
          "twice", "string -> ()", contextAt 1
          "s", "string", None
          "say", "string -> ()", contextAt 2
          "line", "string", None ],
        hints)

/// A context that takes a type and a def that requires it, for a def that calls the one to be inferred to require it.
let private showSource = """
context Show(a) = (show: a -> string)
def describe: [Show(a)] a -> string = { value -> Show(a).show(value) }
"""

[<Fact>]
let ``A def's lens shows a plain context it was inferred to require`` () =
    let source = withFinalNewline """import nanyx/console
def greet = { println("hi") }"""
    let expected = [ "greet", "[Console] () -> ()", offsetOf source "greet" 0 0 ]
    Assert.Equal<(string * string * int) list>(expected, typeHints TypeHints.typeLenses source)
    Assert.Equal<(string * string * int) list>(expected, typeHints TypeHints.inlayHintsWithTopLevel source)

[<Fact>]
let ``A def's lens shows a parameterised context it was inferred to require, named with its parameters`` () =
    let source = (showSource + "def twice = { v -> describe(v) ++ describe(v) }\n").ReplaceLineEndings "\n"
    Assert.Equal<(string * string * int) list>(
        [ "twice", "[Show(α)] α -> string", offsetOf source "twice" 0 0 ], typeHints TypeHints.typeLenses source)
    Assert.Equal<(string * string * int) list>(
        [ "value", "a", offsetOf source "value ->" 0 0
          "twice", "[Show(α)] α -> string", offsetOf source "twice" 0 0
          "v", "α", offsetOf source "v ->" 0 0 ],
        typeHints TypeHints.inlayHintsWithTopLevel source)

[<Fact>]
let ``A def's lens shows both kinds of context it was inferred to require, as hover does`` () =
    let source = ("import nanyx/console\n" + showSource + "def loud = { v -> println(describe(v)) }\n").ReplaceLineEndings "\n"
    let expected = "[Show(α) & Console] α -> ()"
    Assert.Equal<(string * string * int) list>(
        [ "loud", expected, offsetOf source "loud" 0 0 ], typeHints TypeHints.typeLenses source)
    Assert.Equal<(string * string * int) list>(
        [ "value", "a", offsetOf source "value ->" 0 0
          "loud", expected, offsetOf source "loud" 0 0
          "v", "α", offsetOf source "v ->" 0 0 ],
        typeHints TypeHints.inlayHintsWithTopLevel source)
    let hovered = Hover.typeAt Unicode (Compiler.compile source) (offsetOf source "loud" 0 0) |> Option.map _.Type
    Assert.Equal(Some $"def loud: {expected}", hovered)

[<Fact>]
let ``Hints name type variables as their top-level def's type does`` () =
    let source =
        normalized """def pair = { a, b -> (a, b) }
def second = { a, b -> b }
"""
    Assert.Equal<(string * string * int) list>(
        [ "a", "α", offsetOf source "{ a" 0 2
          "b", "β", offsetOf source "b ->" 0 0
          "a", "α", offsetOf source "{ a" 1 2
          "b", "β", offsetOf source "b ->" 1 0 ],
        typeHints localInlayHints source)
    Assert.Equal<(string * string * int) list>(
        [ "pair", "α, β -> (α, β)", 4; "second", "α, β -> β", offsetOf source "second" 0 0 ],
        typeHints TypeHints.typeLenses source)

[<Fact>]
let ``Inlay hints with top-level defs have the lenses' types too, in source order`` () =
    let source =
        normalized """def pair = { a, b -> (a, b) }
def answer: int = 42
def second = { a, b -> b }
"""
    Assert.Equal<(string * string * int) list>(
        [ "pair", "α, β -> (α, β)", 4
          "a", "α", offsetOf source "{ a" 0 2
          "b", "β", offsetOf source "b ->" 0 0
          "second", "α, β -> β", offsetOf source "second" 0 0
          "a", "α", offsetOf source "{ a" 1 2
          "b", "β", offsetOf source "b ->" 1 0 ],
        typeHints TypeHints.inlayHintsWithTopLevel source)

[<Fact>]
let ``A record's rest is left unnamed in every hint of a def when it is named nowhere else`` () =
    let source =
        normalized """def names = { people -> match people
  | [] -> []
  | [p, ...rest] -> [p.name, ...names(rest)] }
"""
    Assert.Equal<(string * string) list>(
        [ "names", "list(name: α, …) -> list(α)"
          "people", "list(name: α, …)"
          "p", "(name: α, …)"
          "rest", "list(name: α, …)" ],
        typeHints TypeHints.inlayHintsWithTopLevel source |> List.map (fun (name, ty, _) -> name, ty))

[<Fact>]
let ``A lambda's parameters take their types from the def's signature`` () =
    let source =
        normalized """def a: (x: string, y: int) = (x = "", y = 1)
def g: typeof a -> string = { x, y -> "x = {x}, y = {y}" }
def h: string, int -> string = { s, n -> s }
"""
    Assert.Equal<(string * string * int) list>(
        [ "x", "string", offsetOf source "x, y ->" 0 0
          "y", "int", offsetOf source "y ->" 0 0
          "s", "string", offsetOf source "s, n" 0 0
          "n", "int", offsetOf source "n ->" 0 0 ],
        typeHints localInlayHints source)

[<Fact>]
let ``A lambda's tag pattern parameter takes its names' types from the def's signature`` () =
    let source =
        normalized """def a: #some(x: string, y: string) = #some(x = "", y = "")
def g: typeof a -> string = { #some(x, y) -> "x = {x}, y = {y}" }
"""
    Assert.Equal<(string * string * int) list>(
        [ "x", "string", offsetOf source "x, y)" 0 0
          "y", "string", offsetOf source "y) ->" 0 0 ],
        typeHints localInlayHints source)

[<Fact>]
let ``Hints keep the names of type aliases`` () =
    let source =
        normalized """type Customer = (name: string)
def getCustomer = { name -> match name
  | "" -> #customerNotFound
  | _ -> #some(Customer(name = name)) }
type Box(a) = (value: a)
def unbox: Box(int) -> int = { b -> b.value }
def rename: Customer -> Customer = { c -> c with name = "x" }
def firstName = { c -> rename(c).name }
"""
    Assert.Equal<(string * string * int) list>(
        [ "getCustomer", "string -> #customerNotFound | #some(Customer)", offsetOf source "getCustomer" 0 0
          "firstName", "Customer -> string", offsetOf source "firstName" 0 0 ],
        typeHints TypeHints.typeLenses source)
    Assert.Equal<(string * string * int) list>(
        [ "name", "string", offsetOf source "name ->" 0 0
          "b", "Box(int)", offsetOf source "b ->" 0 0
          "c", "Customer", offsetOf source "c ->" 0 0
          "c", "Customer", offsetOf source "c ->" 1 0 ],
        typeHints localInlayHints source)
