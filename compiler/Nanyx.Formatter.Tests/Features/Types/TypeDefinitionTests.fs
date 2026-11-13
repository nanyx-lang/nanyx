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
    unchanged "scoped context $Tx = ()\n"
    unchanged "scoped export context $Memory = ()\n"
    Assert.Equal("scoped export context $Memory = ()\n", format "scoped   export   context $Memory = ()")

[<Fact>]
let ``Mutable, defaulted, member and positional fields keep what they say`` () =
    unchanged (lines [ "type Settings = ("; "  int,"; "  mut count: int"; "  label: string"; "  limit: int = 10"; "  type Item = string"; ")" ])

[<Fact>]
let ``A record type with a comment among its fields has one field per line`` () =
    unchanged (lines [ "type Point = ("; "  x: int -- across"; "  -- and then"; "  y: int"; ")" ])
