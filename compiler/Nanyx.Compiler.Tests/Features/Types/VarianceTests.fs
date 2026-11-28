/// The variance of a nominal type's parameters, from its definition (#3206): `undefined` flows through an argument the
/// type only gives out, a value at one that it only takes in flows the other way, and one it does both with, or holds to
/// change, must be the same. A type's variances are kept on its definition, so a package's private type keeps its own
/// in the packages that import it.
module Nanyx.Compiler.Tests.Features.Types.Variance

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

let private variancesOf (source: string) (name: string) =
    (compileClean (withFinalNewline source)).TypeDefs.[name].Variances

[<Fact>]
let ``Each parameter's variance is read from where the definition names it`` () =
    let source =
        """import nanyx/marray

type Stack(a) = private list(a)
type Sink(a) = protected (put: a -> ())
type Both(a, b) = protected (get: () -> a, change: a -> a, unused: int)
type Buf(a) = [Memory] protected (items: MArray(a))
type Nested(a) = protected (stack: Stack(a), sink: Sink(Sink(a)))"""
    Assert.Equal<Variance list>([ Covariant ], variancesOf source "Stack")
    Assert.Equal<Variance list>([ Contravariant ], variancesOf source "Sink")
    Assert.Equal<Variance list>([ Invariant; Unused ], variancesOf source "Both")
    Assert.Equal<Variance list>([ Invariant ], variancesOf source "Buf")
    Assert.Equal<Variance list>([ Covariant ], variancesOf source "Nested")

[<Fact>]
let ``A stack of undefined is given where a stack of int is expected, and joined with one`` () =
    compileClean (
        withFinalNewline """type Stack(a) = private list(a)
def empty: Stack(undefined) = Stack([])
def one: Stack(int) = Stack([1])
def given: Stack(int) = empty
def joined = if true then empty else one
def size: Stack(int) -> int = { Stack(xs) -> list.length(xs) }
def n = size(joined)""")
    |> ignore

[<Fact>]
let ``A type that takes its argument in is given for one at undefined, and not the other way`` () =
    let source =
        withFinalNewline """type Sink(a) = protected (put: a -> ())
def ints: Sink(int) = Sink(put = { x: int -> () })
def fine: Sink(undefined) = ints
def none: Sink(undefined) = Sink(put = { x -> () })
def wrong: Sink(int) = none"""
    Assert.Equal<(string * string) list>([ DiagnosticCodes.TypeMismatch.Code, "none" ], reportedAtIn source (Compiler.compile source))

[<Fact>]
let ``A type that holds a mutable array of its argument is invariant`` () =
    let source =
        withFinalNewline """import nanyx/marray

type Buf(a) = [Memory] protected (items: MArray(a))

def size: [Memory] Buf(int) -> int = { b -> MArray.length(b.items) }

def t = { () ->
  memory {
    def m: MArray(undefined) = MArray.fromList([])
    def b: Buf(undefined) = Buf(items = m)
    size(b)
  }
}"""
    let code, message, _ = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.Equal("'Buf(undefined)' vs 'Buf(int)'", message)

[<Fact>]
let ``Two types that name each other get their variances together`` () =
    let source =
        """type Even(a) = protected (value: a, next: #some(Odd(a)) | #none)
type Odd(a) = protected (value: a, next: #some(Even(a)) | #none)
type In(a) = protected (take: a -> (), other: #some(Out(a)) | #none)
type Out(a) = protected (give: () -> a, other: #some(In(a)) | #none)"""
    Assert.Equal<Variance list>([ Covariant ], variancesOf source "Even")
    Assert.Equal<Variance list>([ Covariant ], variancesOf source "Odd")
    Assert.Equal<Variance list>([ Invariant ], variancesOf source "In")
    Assert.Equal<Variance list>([ Invariant ], variancesOf source "Out")
    let given = withFinalNewline (source + "\ndef e: Even(undefined) = Even(value = crash(\"never\"), next = #none)\ndef f: Even(int) = e\ndef i: In(undefined) = In(take = { x -> () }, other = #none)\ndef j: In(int) = i")
    Assert.Equal<(string * string) list>([ DiagnosticCodes.TypeMismatch.Code, "i" ], reportedAtIn given (Compiler.compile given))

/// A package that depends on `acme.util`, beside it, whose module `stacks` exports the private types `Stack(a)`, which
/// only gives its argument out, and `Sink(a)`, which only takes it in, and values of them at `undefined` and at `int`.
let private withUtil (main: string) =
    let manifest (name: string) (dependencies: string) =
        $"(\n  package = (name = \"{name}\", version = \"0.1.0\")\n  dependencies = [{dependencies}]\n)\n"
    [ "util/util.nyxp", manifest "acme.util" ""
      "util/stacks.nyx",
      withFinalNewline """export module stacks

export type Stack(a) = private list(a)
export type Sink(a) = private (put: a -> ())

export def empty: Stack(undefined) = Stack([])
export def size: Stack(int) -> int = { Stack(xs) -> list.length(xs) }
export def ints: Sink(int) = Sink(put = { x: int -> () })
export def anything: Sink(undefined) = Sink(put = { x -> () })"""
      "app/app.nyxp", (manifest "app" "(package = \"acme.util\", path = \"../util\")").Replace("\n)\n", $"\n  {consoleHostEntry}\n)\n")
      "app/main.nyx", withFinalNewline $"module main\n\nimport acme.util/stacks\n\n{main}" ]

[<Fact>]
let ``A package's private type keeps the variances of its definition in a package that imports it`` () =
    let build (main: string) =
        use package = new TempPackage(withUtil main)
        (Package.build (okOf (Package.findIn (SeededPackages.user ()) (package.Path "app")))).Diagnostics
    Assert.Empty(build "@entry\ndef run = { () -> ignore(size(empty)) }\n\ndef fine: Sink(undefined) = ints")
    Assert.Equal<string list>([ DiagnosticCodes.TypeMismatch.Code ], build "@entry\ndef run = { () -> () }\n\ndef wrong: Sink(int) = anything" |> List.map _.Code)
