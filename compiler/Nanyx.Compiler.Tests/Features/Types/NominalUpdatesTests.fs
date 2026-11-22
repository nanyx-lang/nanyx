module Nanyx.Compiler.Tests.Features.Types.NominalUpdates

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The code, message and text of each diagnostic compiling `source` gives, in source order.
let private diagnosticsOf (source: string) =
    (Compiler.compile source).Diagnostics
    |> List.map (fun diagnostic -> diagnostic.Code, diagnostic.Message, spanOf diagnostic)
    |> List.sortBy (fun (_, _, span) -> span.Start)
    |> List.map (fun (code, message, span) -> code, message, textAt source span)

let private secure =
    """module secure =
  export type User = protected (name: string, admin: bool)
  export def User.guest: string -> User = { name -> User(name = name, admin = false) }
  export def promote: User -> string = { u -> if u.admin then "ADMIN ACCESS for {u.name}" else "denied for {u.name}" }
  export type Secret = private (key: string)
  export def Secret.make: () -> Secret = { Secret(key = "s3cret") }
  export def Secret.isMaster: Secret -> bool = { s -> s.key == "master" }
"""

[<Fact>]
let ``with on a protected or private value outside its module is an error, as its constructor is`` () =
    let uses =
        """
def u = secure.User.guest("mallory")
println(secure.promote(u with admin = true))
println(secure.Secret.isMaster(secure.Secret.make() with key = "master"))
"""
    let source = normalized (secure + uses)
    Assert.Equal<(string * string * string) list>(
        [ DiagnosticCodes.NominalConstructedOutsideItsModule.Code,
          "The protected type 'User' can only be updated with `with` in the module that defines it: make one with 'secure.User.guest'",
          "u with admin = true"
          DiagnosticCodes.NominalConstructedOutsideItsModule.Code,
          "The private type 'Secret' can only be updated with `with` in the module that defines it: make one with 'secure.Secret.make'",
          "secure.Secret.make() with key = \"master\"" ],
        diagnosticsOf source)

[<Fact>]
let ``with on a protected value in its own module makes a value of the type`` () =
    let source =
        normalized
            """module secure =
  export type User = protected (name: string, admin: bool)
  export def User.guest: string -> User = { name -> User(name = name, admin = false) }
  export def User.promoted: User -> User = { u -> u with admin = true }

println(secure.User.promoted(secure.User.guest("ada")).admin)
"""
    Assert.Empty((Compiler.compile source).Diagnostics)

[<Fact>]
let ``with on a generic protected value outside its module is an error`` () =
    let source =
        normalized
            """module boxes =
  export type Box(a) = protected (v: a)
  export def Box.of: a -> Box(a) = { x -> Box(v = x) }

def b: boxes.Box(int) = boxes.Box.of(1)
def c = b with v = 2
"""
    Assert.Equal(
        (DiagnosticCodes.NominalConstructedOutsideItsModule.Code,
         "The protected type 'Box' can only be updated with `with` in the module that defines it: make one with 'boxes.Box.of'",
         "b with v = 2"),
        onlyDiagnostic source)

[<Fact>]
let ``with on a protected value from a module imported under an alias is an error`` () =
    let moduleSource =
        normalized
            """export type User = protected (name: string, admin: bool)
export def User.guest: string -> User = { name -> User(name = name, admin = false) }
"""
    let source =
        normalized
            """import mod as secure

def u = secure.User.guest("mallory")
def admin = u with admin = true
"""
    let code, message, at = onlyDiagnosticIn source (compileImporting moduleSource source)
    Assert.Equal(DiagnosticCodes.NominalConstructedOutsideItsModule.Code, code)
    Assert.StartsWith("The protected type 'User' can only be updated with `with` in the module that defines it", message)
    Assert.Equal("u with admin = true", at)
