module Nanyx.Formatter.Tests.TypeDefinitionTests

open Xunit
open Nanyx.Formatter.Tests.TestHelpers

[<Fact>]
let ``A record type of more than three fields has one field per line`` () =
    Assert.Equal(
        lines [ "type Product = ("; "  sku: string"; "  name: string"; "  price: int"; "  stock: int"; ")" ],
        format "type Product = (sku: string, name: string, price: int, stock: int)")

[<Fact>]
let ``A record type of three fields or fewer stays on one line`` () =
    unchanged "type Point = (x: int, y: int, z: int)\n"

[<Fact>]
let ``A context's members are laid out the same way`` () =
    Assert.Equal(
        lines [ "export context Console = ("; "  print: string -> ()"; "  println: string -> ()"; "  eprint: string -> ()"; "  flush: () -> ()"; ")" ],
        format "export context Console = (print: string -> (), println: string -> (), eprint: string -> (), flush: () -> ())")

[<Fact>]
let ``A scoped context keeps scoped, before export`` () =
    unchanged "scoped context Tx = protected ()\n"
    unchanged "scoped export context Memory = protected ()\n"
    Assert.Equal("scoped export context Memory = protected ()\n", format "scoped   export   context Memory = protected ()")

[<Fact>]
let ``Mutable, defaulted and positional fields keep what they say`` () =
    unchanged (lines [ "type Settings = ("; "  int,"; "  mut count: int"; "  label: string"; "  limit: int = 10"; ")" ])

[<Fact>]
let ``A record type with a comment among its fields has one field per line`` () =
    unchanged (lines [ "type Point = ("; "  x: int -- across"; "  -- and then"; "  y: int"; ")" ])

[<Fact>]
let ``A protected or private type keeps its word after the =`` () =
    unchanged "type Email = protected string\n"
    unchanged "export type User = protected (name: string, age: int)\n"
    unchanged "type UserId = private int\n"

[<Fact>]
let ``A type in a scoped context's scope keeps its context before its kind`` () =
    unchanged "export type Cursor = [Tx] protected (row: int)\n"
    Assert.Equal("type MRef(a) = [Memory] protected (value: a)\n", format "type MRef(a) =   [ Memory ]   protected (value: a)")
[<Fact>]
let ``A union written over several lines keeps one case per line, each starting with a bar`` () =
    let written = lines [ "type Shape ="; "  | #circle(radius: float)"; "  | #rectangle(width: float, height: float)" ]
    unchanged written
    Assert.Equal(written, format "type Shape = #circle(radius: float)\n  | #rectangle(width: float, height: float)")

[<Fact>]
let ``A union written on one line that fits stays on it`` () =
    unchanged "type Answer = #yes | #no | #maybe\n"
    Assert.Equal("type Answer = #yes | #no\n", format "type Answer = | #yes | #no")

[<Fact>]
let ``A union too long for one line has one case per line, each starting with a bar`` () =
    Assert.Equal(
        lines [ "export type FsError ="; "  | #notFound"; "  | #permissionDenied"; "  | #isDirectory"; "  | #notDirectory"; "  | #alreadyExists"; "  | #other(string)"; "  | #unknown" ],
        format "export type FsError = #notFound | #permissionDenied | #isDirectory | #notDirectory | #alreadyExists | #other(string) | #unknown")

[<Fact>]
let ``A broken union keeps the word after the = on its line`` () =
    unchanged (lines [ "type Token = private"; "  | #word(string)"; "  | #number(int)" ])

[<Fact>]
let ``A record type too long for one line has one field per line`` () =
    Assert.Equal(
        lines [ "type Client = ("; "  baseUrl: string"; "  ingest: (channel: string, event: string) -> #some(id: string) | #error(string)"; ")" ],
        format "type Client = (baseUrl: string, ingest: (channel: string, event: string) -> #some(id: string) | #error(string))")

[<Fact>]
let ``Any other type too long for one line goes on the next one, indented`` () =
    Assert.Equal(
        lines [ "type Handler ="; "  (SomeLongRequestTypeName, AnotherLongContextTypeName, YetAnotherLongTypeName) -> SomeResponseType" ],
        format "type Handler = (SomeLongRequestTypeName, AnotherLongContextTypeName, YetAnotherLongTypeName) -> SomeResponseType")
    unchanged "type Handler = (int, string) -> int\n"
