module Nanyx.Compiler.Tests.Features.Memory.ScopedContexts

open Xunit
open Parser.Program
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A scoped context such as `Memory` is of the scope of the call that supplies it, which a function type carries as
// the context's argument and a reader never sees (#1057).

/// The scope of the `Memory` that `ty`'s function type requires, if it requires one.
let private scopeOf (ty: Ty) =
    match Unifier.unalias ty with
    | TyFunc(_, _, contexts) -> contexts |> List.tryPick (function FunctionContexts.ScopedContext("Memory", scope) -> Some scope | _ -> None)
    | _ -> None

/// Each lambda of `source` whose type requires `Memory`, in the order they are written: its type as a reader sees it,
/// and its scope.
let private lambdaScopes (source: string) =
    let typed = compileClean source
    typed.Module
    |> List.collect SyntaxTree.ofItem
    |> List.collect SyntaxTree.expressionsWithin
    |> List.choose (fun expr ->
        match expr.Kind, typed.ExprTypes |> Map.tryFind expr.Id with
        | Lambda _, Some ty -> scopeOf ty |> Option.map (fun scope -> Unifier.tyToString ty, scope)
        | _ -> None)

[<Fact>]
let ``The lambdas over the cells of one block require Memory of one scope, which is not written`` () =
    let lambdas = lambdaScopes """
def tally = { words: list(string) ->
  memory {
    mut count = 0
    def note = { word: string -> count := count + 1 }
    def current = { count }
    for words | word -> note(word)
    current()
  }
}
"""
    Assert.Contains("[Memory] string -> ()", lambdas |> List.map fst)
    Assert.Contains("[Memory] () -> int", lambdas |> List.map fst)
    Assert.Equal(1, lambdas |> List.map snd |> List.distinct |> List.length)

[<Fact>]
let ``Each memory block has a scope of its own`` () =
    let lambdas = lambdaScopes """
def two = {
  def a = memory {
    mut x = 0
    def f = { x }
    f()
  }
  def b = memory {
    mut y = 1
    def g = { y }
    g()
  }
  a + b
}
"""
    Assert.Equal(2, lambdas |> List.map snd |> List.distinct |> List.length)

[<Fact>]
let ``The Memory a signature writes twice is of one scope`` () =
    let typed = compileClean """
def both: ([Memory] () -> int) -> ([Memory] () -> int) -> int = { f -> { g -> 0 } }
"""
    let ty = typed.Types.["both"]
    Assert.Equal("([Memory] () -> int) -> ([Memory] () -> int) -> int", Unifier.tyToString ty)
    match Unifier.unalias ty with
    | TyFunc(first, TyFunc(second, _, _), _) ->
        let scopes = [ scopeOf first; scopeOf second ]
        Assert.True(scopes |> List.forall Option.isSome, $"%A{scopes}")
        Assert.Equal(1, scopes |> List.distinct |> List.length)
    | other -> Assert.Fail $"Expected a curried function, got %A{other}"


/// The one error `source` has, which must be a scoped context's escape: its message, the text it is reported at, and
/// the text of its related span, the call that supplies the context, if it has one.
let private escapeOf (source: string) =
    let source = source.ReplaceLineEndings "\n"
    match (Compiler.compile source).Diagnostics with
    | [ diagnostic ] ->
        Assert.Equal(DiagnosticCodes.ScopedContextEscapes.Code, diagnostic.Code)
        let text (span: SourceSpan) = source.Substring(span.Start, span.End - span.Start)
        diagnostic.Message, text diagnostic.Span.Value, diagnostic.Related |> List.tryHead |> Option.map (fst >> text)
    | other -> failwith $"Expected one diagnostic, got %A{other}"

[<Fact>]
let ``A lambda over a cell cannot be the result of its memory block`` () =
    let message, text, related = escapeOf """
def tryToFool = {
  memory {
    mut y = 0

    { y := y + 1 }
  }
}
"""
    Assert.Equal("y", text)
    Assert.Contains("the cell 'y'", message)
    Assert.Equal(Some "memory {\n    mut y = 0\n\n    { y := y + 1 }\n  }", related)

[<Fact>]
let ``Nor can a structure that holds one, a record or a list`` () =
    let record = """
def f = memory {
  mut x = 0
  def next = { x }
  (count = 1, next = next)
}
"""
    let _, text, _ = escapeOf record
    Assert.Equal("x", text)
    let list = """
def f = memory {
  mut x = 0
  [{ x }, { x + 1 }]
}
"""
    let message, _, _ = escapeOf list
    Assert.Contains("the cell 'x'", message)

[<Fact>]
let ``A block inside another may give back a lambda over the outer block's cells`` () =
    let typed = compileClean """
def nested = memory {
  mut y = 0
  def g = memory { { y := y + 1 } }
  g()
  y
}

def nestedWithCells = memory {
  mut y = 0
  def g = memory {
    mut z = 1
    z := z + 1
    def held = z
    { y := y + held }
  }
  g()
  y
}
"""
    Assert.Equal("int", Unifier.tyToString typed.Types.["nested"])
    Assert.Equal("int", Unifier.tyToString typed.Types.["nestedWithCells"])

[<Fact>]
let ``A function that passes its parameter a block's Memory supplies it in turn`` () =
    let source = """
def run = { make -> memory { make() } }

def mk: [Memory] () -> ([Memory] () -> int) = { { 1 } }

def fine = run({ 2 })
def leaked = run(mk)
"""
    let message, text, related = escapeOf source
    Assert.Equal("mk", text)
    Assert.Contains("'run' call", message)
    Assert.Equal(Some "run(mk)", related)

[<Fact>]
let ``A parameter named as a top-level def does not take the def's contexts`` () =
    let message, text, related = escapeOf """
def run = { mk -> memory { mk() } }

def mk: [Memory] () -> ([Memory] () -> int) = { { 1 } }

def leaked = run(mk)
"""
    Assert.Equal("mk", text)
    Assert.Contains("'run' call", message)
    Assert.Equal(Some "run(mk)", related)

[<Fact>]
let ``A local def named as a top-level def does not take the def's contexts`` () =
    let typed = compileClean """
def mk: [Memory] () -> ([Memory] () -> int) = { { 1 } }

def two: () -> int = {
  def mk = { 2 }
  mk()
}
"""
    Assert.Equal("() -> int", Unifier.tyToString typed.Types.["two"])

[<Fact>]
let ``A call to a function that requires Memory gives a result of the scope it is called in`` () =
    let message, text, _ = escapeOf """
def mk: [Memory] () -> ([Memory] () -> int) = { { 1 } }

def leaked = memory { mk() }
"""
    Assert.Equal("mk()", text)
    Assert.Contains("'memory' call", message)

[<Fact>]
let ``A type variable bound outside the block is safe`` () =
    let typed = compileClean """
def run2: (() -> b) -> b = { f -> memory { f() } }

def two = run2({ 2 })
"""
    Assert.Equal("int", Unifier.tyToString typed.Types.["two"])

[<Fact>]
let ``A function annotated to require Memory cannot give its parameter a block's`` () =
    let message, text, related = escapeOf """
def run: [Memory] ([Memory] () -> b) -> b = { mk -> memory { mk() } }
"""
    Assert.Equal("mk()", text)
    Assert.Contains("outside", message)
    Assert.Equal(Some "memory { mk() }", related)

[<Fact>]
let ``Only a scoped context is kept in the call that supplies it`` () =
    compileClean """import nanyx/console

context Log = (log: string -> ())

def withConsoleLog: [Console & c] ([Log & c] () -> a) -> a = { body ->
  use Log(log = { m -> println(m) })
  body()
}

def greet: [Log] () -> () = { log("x") }

def f = withConsoleLog { greet }

dbg(0)
"""
    |> ignore