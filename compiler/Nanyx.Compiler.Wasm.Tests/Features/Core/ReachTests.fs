/// A module emits only the functions its start reaches, and binds no static closure that nothing reads (see
/// `CoreIrReach`, #1329).
module TranspilerWasmTests.Features.Core.Reach

open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``A function the start never reaches is not emitted`` () =
    let wat =
        transpileWatNotInlined (
            withFinalNewline
                """def used: int -> int = { x -> x + 1 }
def unused: int -> int = { x -> x * 2 }
def alsoUnused: int -> int = { x -> unused(x) }

dbg(used(1))"""
        )
    Assert.Contains("(func $used", wat)
    Assert.DoesNotContain("(func $unused", wat)
    Assert.DoesNotContain("(func $alsoUnused", wat)

[<Fact>]
let ``A function reached only through a closure is emitted`` () =
    let source =
        withFinalNewline
            """def double: int -> int = { x -> x * 2 }
def apply: (int -> int), int -> int = { f, x -> f(x) }

dbg(apply(double, 4))"""
    let wat = transpileWatNotInlined source
    Assert.Contains("(func $double", wat)
    runSource source |> withoutDbgLocations |> assertRan "8\n"

[<Fact>]
let ``A module without a start emits every function`` () =
    let wat = transpileWat (withFinalNewline "def unused: int -> int = { x -> x * 2 }")
    Assert.Contains("(func $unused", wat)

[<Fact>]
let ``A use member that nothing calls is not bound, nor its function emitted`` () =
    let source =
        withFinalNewline
            """context Greeter = (
  greet: string -> string
  part: string -> string
)

def hello: string -> string = { name -> "hello {name}" }
def bye: string -> string = { name -> "bye {name}" }

def main = {
  use Greeter(greet = hello, part = bye)
  dbg(greet("Ada"))
}

main()"""
    let wat = transpileWat source
    Assert.DoesNotContain("(func $bye", wat)
    Assert.DoesNotContain("__use_part", wat)
    runSource source |> withoutDbgLocations |> assertRan "\"hello Ada\"\n"
