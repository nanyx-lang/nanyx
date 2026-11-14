module Nanyx.Compiler.Tests.Features.Types.Nominals

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``Typecheck supports nominal types`` () =
    let source =
        normalized """type Email = protected string
def email: Email = "a@b.com"
def addr: string = email
def email2: Email = addr"""
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)
    let typed = result.Typed.Value
    match typed.Types.["email"] with
    | TyNominal({ Name = name }, underlying, false) ->
        Assert.Equal("Email", name)
        Assert.Equal(TyPrimitive "string", underlying)
    | other -> Assert.True(false, $"Unexpected nominal type: %A{other}")
    Assert.Equal(TyPrimitive "string", typed.Types.["addr"])
    match typed.Types.["email2"] with
    | TyNominal({ Name = name }, _, false) -> Assert.Equal("Email", name)
    | other -> Assert.True(false, $"Unexpected nominal type: %A{other}")

[<Fact>]
let ``Typecheck rejects distinct nominal types`` () =
    let source =
        normalized """type Email = protected string
type UserId = protected string
def email: Email = "a@b.com"
def user: UserId = email"""
    let result = Compiler.compile source
    assertReports DiagnosticCodes.TypeMismatch result
    let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
    Assert.Contains("Type mismatch", message)

[<Fact>]
let ``Typecheck allows local private nominal construction`` () =
    let source =
        "type Token = private string\n" +
        "def token: Token = \"secret\""
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)
    let typed = result.Typed.Value
    match typed.Types.["token"] with
    | TyNominal({ Name = name }, underlying, true) ->
        Assert.Equal("Token", name)
        Assert.Equal(TyPrimitive "string", underlying)
    | other -> Assert.True(false, $"Unexpected token type: %A{other}")

/// A module that exports a `Person` of its own, and a way to make one.
let private peopleModule =
    """
module people
export type Person = protected (name: string)
export def make: string -> Person = { name -> Person(name = name) }
"""

/// The diagnostics of compiling `ids` next to `peopleModule`: each one's code and message.
let private compiledWithPeople (ids: string) =
    let mutable diagnostics = []
    withTempDir (fun dir ->
        let idsPath = Path.Combine(dir, "ids.nyx")
        File.WriteAllText(Path.Combine(dir, "people.nyx"), peopleModule)
        File.WriteAllText(idsPath, ids)
        diagnostics <- (Compiler.compileFile idsPath).Diagnostics |> List.map (fun diagnostic -> diagnostic.Code, diagnostic.Message))
    diagnostics

[<Fact>]
let ``Nominal types of the same name from different modules are different types`` () =
    let ids =
        """
module ids
import people as p
type Person = protected (age: int)
def mine: Person = Person(age = 3)
def theirs: p.Person = mine
"""
    let diagnostics = compiledWithPeople ids
    Assert.Equal<string list>([ DiagnosticCodes.TypeMismatch.Code ], diagnostics |> List.map fst)
    Assert.Equal(
        "Type mismatch: Person vs Person. They are different types: the first is defined in 'ids.nyx', the second in 'people.nyx'.",
        snd diagnostics.Head)

[<Fact>]
let ``Each module's nominal type of a shared name is its own in its own module`` () =
    let ids =
        """
module ids
import people as p
type Person = protected (age: int)
def mine: Person = Person(age = 3)
def age: int = mine.age
def theirs: p.Person = p.make("Ada")
def name: string = theirs.name
"""
    Assert.Empty(compiledWithPeople ids)

[<Fact>]
let ``A module cannot make another module's nominal type from its shape under a name it also defines`` () =
    let ids =
        """
module ids
import people as p
type Person = protected (name: string)
def theirs: p.Person = (name = "Ada")
"""
    Assert.Equal<string list>([ DiagnosticCodes.TypeMismatch.Code ], compiledWithPeople ids |> List.map fst)

[<Fact>]
let ``A $ before a type's name is a syntax error at the $ that shows the protected form`` () =
    let source =
        """
type Email = protected string
def email: $Email = Email("a@b.com")
"""
    let source = source.ReplaceLineEndings "\n"
    match (Compiler.compile source).Diagnostics with
    | [ diagnostic ] ->
        Assert.Equal(DiagnosticCodes.SyntaxError.Code, diagnostic.Code)
        Assert.Contains("write `Email`, and declare a nominal type with `protected` after its `=`, as in `type Email = protected string`", diagnostic.Message.ReplaceLineEndings " ")
        Assert.Equal(source.IndexOf '$', diagnostic.Span.Value.Start)
    | other -> Assert.Fail($"Unexpected diagnostics: %A{other}")

[<Fact>]
let ``A protected type is constructed only in its home module, which the error names`` () =
    let ids =
        """
module ids
import people as p
def theirs: p.Person = p.Person(name = "Ada")
"""
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.NominalConstructedOutsideItsModule.Code,
          "The protected type 'p.Person' can only be constructed in the module that defines it; use a function that module exports instead" ],
        compiledWithPeople (ids.ReplaceLineEndings "\n"))

[<Fact>]
let ``A private type is nominal without a $`` () =
    let result = Compiler.compile "type UserId = private int\ndef id: UserId = UserId(1)\n"
    Assert.Empty(result.Diagnostics)
    match result.Typed.Value.Types.["id"] with
    | TyNominal({ Name = "UserId" }, TyPrimitive "int", true) -> ()
    | other -> Assert.Fail($"Unexpected type: %A{other}")