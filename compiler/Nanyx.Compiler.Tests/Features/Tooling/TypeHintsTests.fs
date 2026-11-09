module Nanyx.Compiler.Tests.Features.Tooling.TypeHints

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

// Type hints: the inferred types shown for declarations that do not write theirs.

/// Each hint as its name, type and where its name starts.
let private typeHints (hintsOf: TypeVarNames -> string -> CompileResult -> TypeHints.TypeHint list) (source: string) =
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    hintsOf Greek "" result |> List.map (fun hint -> hint.Name, hint.Type, hint.Span.Start)

[<Fact>]
let ``Lenses show the types of top-level defs that do not write theirs`` () =
    let source =
        "def id = { x -> x }\n" +
        "def answer: int = 42\n" +
        "def pair = { a, b -> (a, b) }\n" +
        "def (p, q) = (1, \"s\")\n"
    Assert.Equal<(string * string * int) list>(
        [ "id", "α -> α", offsetOf source "id" 0 0
          "pair", "α, β -> (α, β)", offsetOf source "pair" 0 0
          "p", "int", offsetOf source "p," 0 0
          "q", "string", offsetOf source "q)" 0 0 ],
        typeHints TypeHints.typeLenses source)

[<Fact>]
let ``Inlay hints show the types of locals, parameters and pattern variables`` () =
    let source =
        "def scale = { factor ->\n" +
        "  def offset = 1\n" +
        "  def written: int = 2\n" +
        "  factor * written + offset\n" +
        "}\n" +
        "def rest = { items -> match items\n" +
        "  | [_, ...tail] -> tail\n" +
        "  | _ -> items }\n"
    Assert.Equal<(string * string * int) list>(
        [ "factor", "int", offsetOf source "factor" 0 0
          "offset", "int", offsetOf source "offset" 0 0
          "items", "list(α)", offsetOf source "items" 0 0
          "tail", "list(α)", offsetOf source "tail" 0 0 ],
        typeHints TypeHints.inlayHints source)

[<Fact>]
let ``A shorthand lambda's argument is not written, so it has no inlay hint`` () =
    let source = "def divisibleBy = { n -> { % n == 0 } }\n"
    Assert.Equal<(string * string * int) list>(
        [ "n", "int", offsetOf source "n ->" 0 0 ], typeHints TypeHints.inlayHints source)

[<Fact>]
let ``A def that writes only its contexts has its type shown after them`` () =
    let source =
        "context Log = (log: string -> ())\n" +
        "def greet: [Log] = { name -> log(name) }\n" +
        "def twice: [Log] = { s ->\n" +
        "  def say: [Log] = { line -> log(line) }\n" +
        "  say(s)\n" +
        "  say(s)\n" +
        "}\n"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    let hints =
        TypeHints.inlayHintsWithTopLevel Greek "" result
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

[<Fact>]
let ``Hints name type variables as their top-level def's type does`` () =
    let source =
        "def pair = { a, b -> (a, b) }\n" +
        "def second = { a, b -> b }\n"
    Assert.Equal<(string * string * int) list>(
        [ "a", "α", offsetOf source "{ a" 0 2
          "b", "β", offsetOf source "b ->" 0 0
          "a", "α", offsetOf source "{ a" 1 2
          "b", "β", offsetOf source "b ->" 1 0 ],
        typeHints TypeHints.inlayHints source)
    Assert.Equal<(string * string * int) list>(
        [ "pair", "α, β -> (α, β)", 4; "second", "α, β -> β", offsetOf source "second" 0 0 ],
        typeHints TypeHints.typeLenses source)

[<Fact>]
let ``Inlay hints with top-level defs have the lenses' types too, in source order`` () =
    let source =
        "def pair = { a, b -> (a, b) }\n" +
        "def answer: int = 42\n" +
        "def second = { a, b -> b }\n"
    Assert.Equal<(string * string * int) list>(
        [ "pair", "α, β -> (α, β)", 4
          "a", "α", offsetOf source "{ a" 0 2
          "b", "β", offsetOf source "b ->" 0 0
          "second", "α, β -> β", offsetOf source "second" 0 0
          "a", "α", offsetOf source "{ a" 1 2
          "b", "β", offsetOf source "b ->" 1 0 ],
        typeHints TypeHints.inlayHintsWithTopLevel source)

[<Fact>]
let ``Hints keep the names of type aliases`` () =
    let source =
        "type Customer = (name: string)\n" +
        "def getCustomer = { name -> match name\n" +
        "  | \"\" -> #customerNotFound\n" +
        "  | _ -> #some(Customer(name = name)) }\n" +
        "type Box(a) = (value: a)\n" +
        "def unbox: Box(int) -> int = { b -> b.value }\n" +
        "def rename: Customer -> Customer = { c -> c with name = \"x\" }\n" +
        "def firstName = { c -> rename(c).name }\n"
    Assert.Equal<(string * string * int) list>(
        [ "getCustomer", "string -> #customerNotFound | #some(Customer)", offsetOf source "getCustomer" 0 0
          "firstName", "Customer -> string", offsetOf source "firstName" 0 0 ],
        typeHints TypeHints.typeLenses source)
    Assert.Equal<(string * string * int) list>(
        [ "name", "string", offsetOf source "name ->" 0 0
          "b", "Box(int)", offsetOf source "b ->" 0 0
          "c", "Customer", offsetOf source "c ->" 0 0
          "c", "Customer", offsetOf source "c ->" 1 0 ],
        typeHints TypeHints.inlayHints source)
