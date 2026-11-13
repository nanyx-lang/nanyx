/// A lifted lambda captures the context members of its owner and of the `use`s in scope only when it may call them
/// (#934).
module TranspilerWasmTests.Features.Contexts.LambdaMemberCaptures

open System
open Xunit
open TranspilerWasmTestHelpers

/// The text of the one lifted lambda in `wat`, up to its export.
let private lambdaText (wat: string) =
    let lambdas = Text.RegularExpressions.Regex.Matches(wat, @"\(func \$(__lambda_\d+) ")
    Assert.Equal(1, lambdas.Count)
    let name = lambdas.[0].Groups.[1].Value
    wat.Substring(lambdas.[0].Index, wat.IndexOf($"(export \"{name}\"", lambdas.[0].Index) - lambdas.[0].Index)

[<Fact>]
let ``A lambda in a function that requires a context captures none of its members when it names none`` () =
    let source = """import nanyx/console

context Greet = (greet: string -> string)

def shout: [Greet] list(string) -> list(string) = { names ->
  names \list.map({ s -> s ++ "!" }) \list.map(greet)
}

def hello = { s -> "hi " ++ s }

def main = {
  use (greet = hello)
  for shout(["a", "b"]) | s -> println(s)
}

main()
"""
    Assert.DoesNotContain("__ctx", lambdaText (transpileWat source))
    runSource source |> assertRan "hi a!\nhi b!\n"

[<Fact>]
let ``A lambda in a function that requires a context captures the member it names`` () =
    let source = """import nanyx/console

context Greet = (greet: string -> string)

def shout: [Greet] list(string) -> list(string) = { names ->
  names \list.map({ s -> greet(s) ++ "!" })
}

def hello = { s -> "hi " ++ s }

def main = {
  use (greet = hello)
  for shout(["a", "b"]) | s -> println(s)
}

main()
"""
    Assert.Contains("__ctx_greet", lambdaText (transpileWat source))
    runSource source |> assertRan "hi a!\nhi b!\n"

[<Fact>]
let ``A lambda after a use captures none of its members when it names none`` () =
    let source = """import nanyx/console

context Greet = (greet: string -> string)

def hello = { s -> "hi " ++ s }

def main = {
  use (greet = hello)
  def marked = ["a", "b"] \list.map({ s -> s ++ "?" })
  for marked | s -> println(greet(s))
}

main()
"""
    Assert.DoesNotContain("__use_", lambdaText (transpileWat source))
    runSource source |> assertRan "hi a?\nhi b?\n"
