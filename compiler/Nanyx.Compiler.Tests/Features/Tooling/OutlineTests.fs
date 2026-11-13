module Nanyx.Compiler.Tests.Features.Tooling.Outline

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

// The outline: what a module declares, as a tree.

/// An outline item as its name, kind, the text its name span covers, and its children likewise.
type private Shape = Shape of string * Outline.OutlineKind * string * Shape list

let rec private shapes (source: string) (items: Outline.OutlineItem list) : Shape list =
    items
    |> List.map (fun item ->
        Shape(item.Name, item.Kind, source.Substring(item.NameSpan.Start, item.NameSpan.End - item.NameSpan.Start), shapes source item.Children))

[<Fact>]
let ``The outline lists top-level declarations with what is declared inside them`` () =
    let source =
        "type Point = (x: int, y: int)\n" +
        "type Shape = #circle(int) | #square(int)\n" +
        "type Count = int\n" +
        "context Logger = (log: string -> (), level: int)\n" +
        "def origin: Point = (x = 0, y = 0)\n" +
        "def area = { s ->\n" +
        "  def side = 2\n" +
        "  side * side\n" +
        "}\n" +
        "module geometry =\n" +
        "  def unit = 1\n"
    let outline = (Outline.ofSource "" source).Value
    Assert.Equal<Shape list>(
        [ Shape("Point", Outline.OutlineKind.RecordType, "Point",
                [ Shape("x", Outline.OutlineKind.RecordField, "x", []); Shape("y", Outline.OutlineKind.RecordField, "y", []) ])
          Shape("Shape", Outline.OutlineKind.UnionType, "Shape", [])
          Shape("Count", Outline.OutlineKind.OtherType, "Count", [])
          Shape("Logger", Outline.OutlineKind.ContextType, "Logger",
                [ Shape("log", Outline.OutlineKind.ContextFunction, "log", []); Shape("level", Outline.OutlineKind.ContextValue, "level", []) ])
          Shape("origin", Outline.OutlineKind.Value, "origin", [])
          Shape("area", Outline.OutlineKind.Function, "area", [ Shape("side", Outline.OutlineKind.Value, "side", []) ])
          // The parser lifts a block's defs to the top level; the outline puts them back in the block.
          Shape("geometry", Outline.OutlineKind.ModuleBlock, "geometry", [ Shape("unit", Outline.OutlineKind.Value, "unit", []) ]) ],
        shapes source outline)
    // An item spans all of its declaration.
    let area = outline |> List.find (fun item -> item.Name = "area")
    Assert.Equal("def area = { s ->\n  def side = 2\n  side * side\n}", source.Substring(area.Span.Start, area.Span.End - area.Span.Start))

[<Fact>]
let ``A module with a type error has an outline, and one that does not parse has none`` () =
    Assert.Equal(1, (Outline.ofSource "" "def broken: int = \"text\"\n").Value.Length)
    Assert.Equal(None, Outline.ofSource "" "def broken = (\n")


[<Fact>]
let ``A def's local defs in an if branch, a lambda it passes and a memory block are listed under it`` () =
    let source =
        """def f = { x ->
  def y = x + 1
  y
}
def g = { xs ->
  xs \list.map({ n ->
    def m = n * 2
    m
  })
}
def h = { b ->
  if b then
    def k = 1
    k
  else 2
}
def sum: list(int) -> int = { xs ->
  memory {
    mut total = 0
    for xs | x -> total := total + x
    total
  }
}
def outer = { a ->
  def inner = { c ->
    def deep = c
    deep
  }
  inner(a)
}
"""
    let names (items: Outline.OutlineItem list) = items |> List.map (fun item -> item.Name, item.Children |> List.map _.Name)
    Assert.Equal<(string * string list) list>(
        [ "f", [ "y" ]; "g", [ "m" ]; "h", [ "k" ]; "sum", [ "total" ]; "outer", [ "inner" ] ],
        names (Outline.ofSource "" source).Value)
    // A local def's own defs are its children, not the enclosing def's.
    let outer = (Outline.ofSource "" source).Value |> List.find (fun item -> item.Name = "outer")
    Assert.Equal<string list>([ "deep" ], outer.Children.Head.Children |> List.map _.Name)