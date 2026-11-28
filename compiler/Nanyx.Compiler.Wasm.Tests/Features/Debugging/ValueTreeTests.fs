/// The text a `dbg` printer writes read back as a tree, so that the debugger's Variables view expands records, tuples,
/// tags, lists, maps, sets and nominal types (see `ValueTree`).
module TranspilerWasmTests.Features.Debugging.ValueTrees

open Xunit
open Nanyx.Runtime.Debugging

/// Each part of `tree`, by its name, with its text.
let private partsOf (tree: ValueTree) = tree.Parts |> List.map (fun (name, part) -> name, part.Text)

[<Fact>]
let ``A number, a string, a bool, a char, unit and a function are leaves`` () =
    for text in [ "42"; "-3.5"; "\"Ada, the first\""; "true"; "'x'"; "()"; "<function int, int -> int>"; "<value of MArray(int)>" ] do
        Assert.Equal<(string * string) list>([], partsOf (ValueTree.parse text))

[<Fact>]
let ``A record expands into its fields by name, a tuple into its parts by place`` () =
    Assert.Equal<(string * string) list>([ "age", "36"; "name", "\"Ada\"" ], partsOf (ValueTree.parse "(age = 36, name = \"Ada\")"))
    Assert.Equal<(string * string) list>([ "0", "1"; "1", "\"a, b\"" ], partsOf (ValueTree.parse "(1, \"a, b\")"))

[<Fact>]
let ``A list, a set and an array expand into their elements by place, a map into its entries by key`` () =
    Assert.Equal<(string * string) list>([ "[0]", "1"; "[1]", "[2, 3]" ], partsOf (ValueTree.parse "[1, [2, 3]]"))
    Assert.Equal<(string * string) list>([ "[0]", "1"; "[1]", "2" ], partsOf (ValueTree.parse "Set[1, 2]"))
    Assert.Equal<(string * string) list>([ "\"tea\"", "3"; "\"cake\"", "(price = 2)" ], partsOf (ValueTree.parse "Map[\"tea\" => 3, \"cake\" => (price = 2)]"))

[<Fact>]
let ``A tag expands into its payload's parts, and one whose payload has none is a leaf`` () =
    Assert.Equal<(string * string) list>([ "x", "1"; "y", "2" ], partsOf (ValueTree.parse "#point((x = 1, y = 2))"))
    Assert.Equal<(string * string) list>([], partsOf (ValueTree.parse "#some(5)"))
    Assert.Equal<(string * string) list>([], partsOf (ValueTree.parse "#none"))

[<Fact>]
let ``A nominal type expands into its shape's parts, and one around a single value is a leaf`` () =
    Assert.Equal<(string * string) list>([ "name", "\"Ada\"" ], partsOf (ValueTree.parse "Person(name = \"Ada\")"))
    Assert.Equal<(string * string) list>([], partsOf (ValueTree.parse "Money(250)"))

[<Fact>]
let ``Parts nest, each with its own parts`` () =
    let tree = ValueTree.parse "(orders = [#shipped((id = 7)), #packed((id = 8))])"
    let orders = tree.Parts |> List.find (fst >> (=) "orders") |> snd
    let first = orders.Parts |> List.head |> snd
    Assert.Equal<(string * string) list>([ "id", "7" ], partsOf first)

[<Fact>]
let ``Text that isn't well bracketed is a leaf, whole`` () =
    let tree = ValueTree.parse "(1, [2"
    Assert.Equal("(1, [2", tree.Text)
    Assert.Equal<(string * string) list>([], partsOf tree)
