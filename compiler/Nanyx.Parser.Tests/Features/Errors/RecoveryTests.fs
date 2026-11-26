module RecoveryTests

open Xunit
open Parser.Program

/// The text of each item a recovering read of `source` keeps, and the line (from 1) and message of each error it notes.
let private recovered (source: string) =
    let items, _, errors = DescentTopLevel.readModuleRecovering "t.nyx" source
    items |> List.map (fun item -> source.Substring(item.Span.Start, item.Span.End - item.Span.Start)),
    errors |> List.map (fun error -> error.Span.StartLine + 1, error.Message)

[<Fact>]
let ``An item left open is left out and the items after it are read`` () =
    let items, errors = recovered "def a = 1\n\ndef b = (1 +\n\ndef c = a + 2\n"
    Assert.Equal<string list>([ "def a = 1"; "def c = a + 2" ], items)
    Assert.Equal<(int * string) list>([ 5, "Expected `)`" ], errors)

[<Fact>]
let ``An item that runs on into the line of the mistake is left out with it`` () =
    let items, errors = recovered "def a = 1\n\ndef d = a +\n\ndef e = a * 2\n\ndef f = e\n"
    Assert.Equal<string list>([ "def a = 1"; "def e = a * 2"; "def f = e" ], items)
    Assert.Equal<(int * string) list>([ 5, "Expected an expression" ], errors)

[<Fact>]
let ``Each mistake is noted, in the order of the text`` () =
    let items, errors = recovered "def a = )\ndef b = 1\ndef c = ]\ndef d = 2\n"
    Assert.Equal<string list>([ "def b = 1"; "def d = 2" ], items)
    Assert.Equal<int list>([ 1; 3 ], errors |> List.map fst)

[<Fact>]
let ``A mistake inside a block is read past to the next top-level item`` () =
    let items, errors = recovered "def main = {\n  def x = (1 +\n  x\n}\n\ndef after = 1\n"
    Assert.Equal<string list>([ "def after = 1" ], items)
    Assert.Equal(1, errors.Length)

[<Fact>]
let ``A text with no mistakes is read as a whole`` () =
    let items, errors = recovered "def a = 1\ndef b = a\n"
    Assert.Equal<string list>([ "def a = 1"; "def b = a" ], items)
    Assert.Empty(errors)
