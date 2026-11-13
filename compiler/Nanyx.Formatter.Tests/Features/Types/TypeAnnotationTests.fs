module Nanyx.Formatter.Tests.Features.Types.TypeAnnotations

open Xunit
open Nanyx.Formatter
open Nanyx.Formatter.Tests.TestHelpers

[<Fact>]
let ``Type annotations read as the docs write them`` () =
    unchanged (lines [ "def f: int, int -> int = { a, b ->"; "  a + b"; "}" ])
    unchanged (lines [ "def g: (int -> int), int -> int = { h, x ->"; "  h(x)"; "}" ])
    unchanged "def h: list(a -> b) -> int = { fs -> 0 }\n"
    unchanged "type Point = (x: int, y: int)\n"
    unchanged "type Shape = #circle(radius: float) | #square(float)\n"
    unchanged "export context Console(a) = (print: a -> ())\n"

[<Fact>]
let ``A hole in a type is printed as written`` () =
    unchanged "def xs: list(_) = [1, 2]\n"
    unchanged (lines [ "def f: int, _ -> _ = { a, b ->"; "  a + b"; "}" ])
    unchanged "def g = { (x: _), y -> x + y }\n"
    Assert.Equal("def m: map(string, _) = m\n", format "def m:map(string,_)=m")

[<Fact>]
let ``An empty context list is kept, as it says on purpose that a function requires no contexts`` () =
    unchanged "def write: [] string -> () = { s -> () }\n"
    unchanged (lines [ "def apply: ([] int -> int), int -> int = { f, x ->"; "  f(x)"; "}" ])
    unchanged "type Handler = [] string -> ()\n"
    unchanged "context Logger = (log: [] string -> ())\n"
    Assert.Equal("def write: [] string -> () = { s -> () }\n", format "def write:[ ] string->()={s->()}")

[<Fact>]
let ``An open record type keeps its ... and the name of its other fields`` () =
    unchanged (lines [ "def g: (x: int, y: int, ...) -> int = { x, y ->"; "  x + y"; "}" ])
    unchanged (lines [ "def moveRight: (x: int, ...r) -> (x: int, ...r) = { p ->"; "  p with x = p.x + 1"; "}" ])
    Assert.Equal("def p: (x: int, ...r) = p\n", format "def p:(x:int,...r)=p")
