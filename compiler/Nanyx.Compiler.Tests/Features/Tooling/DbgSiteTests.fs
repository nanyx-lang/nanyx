/// Where the `dbg` calls of a file are, and where one a run reached is once the file has changed (`DbgSites`), so that
/// an editor keeps each value on its `dbg`'s line (#2054).
module Nanyx.Compiler.Tests.Features.DbgSites

open Xunit
open NyxCompiler

let private source =
    "def total = [3, 4] \\list.sum\ndbg(total)\n\ndef twice: int -> int = { n ->\n  dbg(n)\n  n * 2 \\dbg\n}\n\nmodule shapes =\n  def area: int -> int = { side -> dbg(side * side) }\n"

[<Fact>]
let ``Each dbg is found where its name is, with its definition, its place there and its text`` () =
    let sites = DbgSites.sitesIn source |> Option.get
    Assert.Equal<(string * int * int * int * string) list>(
        [ "", 0, 2, 1, "dbg(total)"
          "twice", 0, 5, 3, "dbg(n)"
          "twice", 1, 6, 10, "2 \\dbg"
          "shapes.area", 0, 10, 36, "dbg(side * side)" ],
        sites |> List.map (fun site -> site.Definition, site.Index, site.Line, site.Column, site.Text))

[<Fact>]
let ``A dbg keeps its value when lines are added above it`` () =
    let now = "-- a comment\n\n" + source
    Assert.Equal(Some [ Some (4, 1); Some (7, 3); Some (8, 10) ], DbgSites.moved source now [ 2, 1; 5, 3; 6, 10 ])

[<Fact>]
let ``A dbg follows its definition when definitions are moved`` () =
    let now = "module shapes =\n  def area: int -> int = { side -> dbg(side * side) }\n\ndef twice: int -> int = { n ->\n  dbg(n)\n  n * 2 \\dbg\n}\n\ndef total = [3, 4] \\list.sum\ndbg(total)\n"
    Assert.Equal(Some [ Some (10, 1); Some (5, 3); Some (2, 36) ], DbgSites.moved source now [ 2, 1; 5, 3; 10, 36 ])

[<Fact>]
let ``Deleting one dbg of a definition leaves the others on theirs, and drops its own`` () =
    let now = source.Replace("  dbg(n)\n", "")
    Assert.Equal(Some [ None; Some (5, 10) ], DbgSites.moved source now [ 5, 3; 6, 10 ])

[<Fact>]
let ``A place that names no dbg has none, and a text that doesn't parse moves nothing`` () =
    Assert.Equal(Some [ None ], DbgSites.moved source source [ 1, 1 ])
    Assert.Equal(None, DbgSites.moved source "def broken = (\n" [ 2, 1 ])
