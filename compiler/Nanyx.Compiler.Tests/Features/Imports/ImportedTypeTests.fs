module Nanyx.Compiler.Tests.Features.ImportedTypes

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

/// A module `people` that exports the nominal `Person`, with a function that makes one and one that takes one.
let private peopleModule =
    normalized """module people
export type Person = protected (name: string)
export def Person.new: string -> Person = { name -> Person(name = name) }
export def greet: Person -> string = { p -> "Hello, " ++ p.name }
"""

/// The diagnostics compiling `main`, beside the `people` module, gives: each one's code and the text it is reported at.
let private compiledWithPeople (main: string) =
    let mutable result = []
    withTempDir (fun dir ->
        let mainPath = Path.Combine(dir, "main.nyx")
        File.WriteAllText(Path.Combine(dir, "people.nyx"), peopleModule)
        File.WriteAllText(mainPath, main)
        result <-
            (Compiler.compileFile mainPath).Diagnostics
            |> List.map (fun diagnostic -> diagnostic.Code, textAt main (spanOf diagnostic)))
    result

[<Fact>]
let ``An aliased import's nominal type cannot be named without the alias`` () =
    let main = withFinalNewline """module main
import people as people
def p: Person = people.Person.new("Ada")"""
    Assert.Equal<(string * string) list>([ DiagnosticCodes.UnknownType.Code, "Person" ], compiledWithPeople main)

[<Fact>]
let ``An aliased import's constructor cannot be called without the alias`` () =
    let main = withFinalNewline """module main
import people as people
def p = Person(name = "Ada")"""
    Assert.Equal<(string * string) list>([ DiagnosticCodes.UnknownFunction.Code, "Person" ], compiledWithPeople main)

[<Fact>]
let ``An aliased import's types and their attached definitions are named with the alias`` () =
    let main =
        normalized """module main
import people as people
def p: people.Person = people.Person.new("Ada")
def greeting: string = people.greet(p)
def all: list(people.Person) = [ p ]
"""
    Assert.Empty(compiledWithPeople main)

[<Fact>]
let ``An unaliased import brings its types into scope unqualified`` () =
    let main = withFinalNewline """module main
import people
def p: Person = Person.new("Ada")
def greeting: string = greet(p)"""
    Assert.Empty(compiledWithPeople main)

[<Fact>]
let ``A nominal type cannot be constructed outside the module that defines it`` () =
    let unaliased = withFinalNewline """module main
import people
def p = Person(name = "Ada")"""
    Assert.Equal<(string * string) list>([ DiagnosticCodes.NominalConstructedOutsideItsModule.Code, "Person" ], compiledWithPeople unaliased)
    let aliased = withFinalNewline """module main
import people as people
def p = people.Person(name = "Ada")"""
    Assert.Equal<(string * string) list>([ DiagnosticCodes.NominalConstructedOutsideItsModule.Code, "people.Person" ], compiledWithPeople aliased)

[<Fact>]
let ``A nominal type can be constructed in the module that defines it`` () =
    let result = Compiler.compile (withFinalNewline """type Person = protected (name: string)
def p: Person = Person(name = "Ada")
def name: string = p.name""")
    Assert.Empty(result.Diagnostics)

[<Fact>]
let ``A nominal value is assignable to its shape, but the shape is not assignable to the nominal type`` () =
    let toShape = withFinalNewline """module main
import people
def nameOf: (name: string) -> string = { r -> r.name }
def n: string = nameOf(Person.new("Ada"))"""
    Assert.Empty(compiledWithPeople toShape)
    let fromShape = withFinalNewline """module main
import people
def g: string = greet((name = "Ada"))"""
    match compiledWithPeople fromShape with
    | [ code, _ ] -> Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    | other -> failwith $"Expected one type mismatch, got %A{other}"

[<Fact>]
let ``An aliased import's contexts are named with the alias`` () =
    withTempDir (fun dir ->
        let mainPath = Path.Combine(dir, "main.nyx")
        File.WriteAllText(Path.Combine(dir, "logs.nyx"), "module logs\nexport context Logger = (log: string -> ())\n")
        File.WriteAllText(mainPath, (withFinalNewline """module main
import logs as logs
def f: [logs.Logger] () -> () = { log("x") }
def g: [Logger] () -> () = { log("x") }"""))
        let diagnostic = single (Compiler.compileFile mainPath)
        Assert.Equal(DiagnosticCodes.UnknownContext.Code, diagnostic.Code)
        Assert.Contains("Unknown context 'Logger' in the signature of 'g'", diagnostic.Message))

[<Fact>]
let ``An aliased import's functions cannot be called without the alias`` () =
    let main = withFinalNewline """module main
import people as people
def g: string = greet(people.Person.new("Ada"))"""
    Assert.Equal<(string * string) list>([ DiagnosticCodes.UnknownFunction.Code, "greet" ], compiledWithPeople main)

[<Fact>]
let ``An aliased import's attached definitions cannot be called without the alias`` () =
    let main = withFinalNewline """module main
import people as people
def p: people.Person = Person.new("Ada")"""
    match compiledWithPeople main with
    | [ _, text ] -> Assert.Equal("Person.new", text)
    | other -> failwith $"Expected one diagnostic at 'Person.new', got %A{other}"

[<Fact>]
let ``A shape is not assignable to a nominal type through an annotation`` () =
    let main = withFinalNewline """module main
import people as people
def p: people.Person = (name = "Ada")"""
    match compiledWithPeople main with
    | [ code, _ ] -> Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    | other -> failwith $"Expected one type mismatch, got %A{other}"

[<Fact>]
let ``A function that returns a nominal type cannot return its shape`` () =
    let main = "module main\nimport people\ndef make: string -> Person = { name -> (name = name) }\n"
    match compiledWithPeople main with
    | [ code, _ ] -> Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    | other -> failwith $"Expected one type mismatch, got %A{other}"

[<Fact>]
let ``A list of a nominal type cannot hold its shape`` () =
    let main = withFinalNewline """module main
import people
def all: list(Person) = [ Person.new("Ada"), (name = "Grace") ]"""
    match compiledWithPeople main with
    | [ code, _ ] -> Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    | other -> failwith $"Expected one type mismatch, got %A{other}"

[<Fact>]
let ``A list of a nominal type is assignable to a list of its shape`` () =
    let main =
        normalized """module main
import people
def namesOf: list((name: string)) -> int = { xs -> list.length(xs) }
def people: list(Person) = [ Person.new("Ada") ]
def n: int = namesOf(people)
"""
    Assert.Empty(compiledWithPeople main)

[<Fact>]
let ``A nominal type cannot be constructed outside its module from inside a lambda`` () =
    let main = withFinalNewline """module main
import people
def all = [ "Ada", "Grace" ] \list.map { name -> Person(name = name) }"""
    Assert.Equal<(string * string) list>([ DiagnosticCodes.NominalConstructedOutsideItsModule.Code, "Person" ], compiledWithPeople main)
