module Nanyx.Compiler.Tests.Features.FunctionTypeContexts

open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

// A function type carries the contexts its function requires where it is called: `[Console] int -> int`.

let private typeOf (source: string) (name: string) =
    Unifier.tyToString (compileClean source).Types.[name]

[<Fact>]
let ``A parameter's function type keeps the contexts written on it`` () =
    let source =
        """
def apply: [Console] ([Console] int -> int), int -> int = { f, x -> f(x) }
"""
    Assert.Equal("([Console] int -> int), int -> int", typeOf source "apply")

[<Fact>]
let ``A def whose value names a function point-free is a function that requires the contexts it writes`` () =
    let source =
        """
def g: [Console] int -> () = println

def main: [Console] () -> () = {
  def h: [Console] string -> () = println
  g(3)
  h("four")
}
"""
    Assert.Equal("[Console] int -> ()", typeOf source "g")
    let diagnostic = Assert.Single((Compiler.compile "def g: [Console] int -> () = println\ndef f: int -> () = { x -> g(x) }\n").Diagnostics)
    Assert.Equal(DiagnosticCodes.UndeclaredContext.Code, diagnostic.Code)

[<Fact>]
let ``A result's function type keeps the contexts written on it`` () =
    let source =
        """
def adder: int -> ([Console] int -> int) = { n -> { x -> x + n } }
"""
    Assert.Equal("int -> ([Console] int -> int)", typeOf source "adder")

[<Fact>]
let ``A type alias of a function type keeps its contexts`` () =
    let source =
        """
type Handler = [Console] string -> ()

def handle: [Console] Handler, string -> () = { h, s -> h(s) }
"""
    let typed = compileClean source
    Assert.Equal("Handler, string -> ()", Unifier.tyToString typed.Types.["handle"])
    match Unifier.unalias typed.Types.["handle"] with
    | TyFunc(TyRecord(fields, _), _, []) -> Assert.Equal("[Console] string -> ()", Unifier.tyToString (Unifier.unalias fields.["0"]))
    | other -> failwith $"Unexpected type: {Unifier.tyToString other}"

[<Fact>]
let ``A record field's function type keeps its contexts`` () =
    let source =
        """
def run: (log: [Console] string -> ()) -> int = { r -> 1 }
"""
    Assert.Equal("(log: ([Console] string -> ())) -> int", typeOf source "run")

[<Fact>]
let ``A context variable on a parameter is shown with the def's own`` () =
    let source =
        """
def apply: [c] ([c] a -> b), a -> b = { f, x -> f(x) }
"""
    Assert.Equal(Some "def apply: [c] ([c] a -> b), a -> b", hoverType source "apply")
    Assert.Equal(Some "f: [c] a -> b", hoverType source "f(x)")

[<Fact>]
let ``A pure function fits where one that requires contexts is expected`` () =
    compileClean
        """
def twice: [Console] ([Console] int -> int), int -> int = { f, x -> f(f(x)) }

def double: int -> int = { x -> x * 2 }

def four = twice(double, 1)

def five = twice({ x -> x + 1 }, 4)

dbg(0)
"""
    |> ignore

[<Fact>]
let ``A function that requires a context does not fit where a pure one is expected`` () =
    let source =
        """
def runPure: (int -> int) -> int = { g -> g(5) }

def pass: ([Console] int -> int) -> int = { f -> runPure(f) }
"""
    let result = Compiler.compile source
    let diagnostic = Assert.Single result.Diagnostics
    Assert.Equal(DiagnosticCodes.ContextsNotAllowed.Code, diagnostic.Code)
    Assert.Contains("which does not require the context 'Console'", diagnostic.Message)

let private console = TyApply("Console", [])
let private intFunc contexts = TyFunc(TyPrimitive "int", TyPrimitive "int", contexts)

[<Fact>]
let ``Two function types that must be the same require the same contexts`` () =
    match Unifier.unify [ intFunc [ console ], intFunc [], Equal, SourceSpan.none ] with
    | Error (message, _) -> Assert.Contains("only the first requires 'Console'", message)
    | Ok _ -> failwith "Expected a context mismatch"
    Assert.True(Unifier.unify [ intFunc [ console ], intFunc [ console ], Equal, SourceSpan.none ] |> Result.isOk)

[<Fact>]
let ``A context variable takes the contexts of the function given for it`` () =
    let c = TyVar (TyVar.ordinary 1 (Some "c"))
    match Unifier.unify [ intFunc [ console ], intFunc [ c ], Assignable, SourceSpan.none ] with
    | Ok subst -> Assert.Equal("[Console] int -> int", Unifier.tyToString (Unifier.apply subst (intFunc [ c ])))
    | Error (message, _) -> failwith message
    // A pure function leaves alone a variable a type writes (#1701), and one inference made keeps room for more.
    match Unifier.unify [ intFunc [], intFunc [ c ], Assignable, SourceSpan.none ] with
    | Ok subst -> Assert.Equal("[c] int -> int", Unifier.tyToString (Unifier.apply subst (intFunc [ c ])))
    | Error (message, _) -> failwith message
    let made = TyVar (TyVar.ordinary 2 None)
    match Unifier.unify [ intFunc [], intFunc [ made ], Assignable, SourceSpan.none ] with
    | Ok subst -> Assert.Equal("int -> int", Unifier.tyToString (Unifier.apply subst (intFunc [ made ])))
    | Error (message, _) -> failwith message
