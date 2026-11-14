module Nanyx.Formatter.Tests.Features.Command.RangeFormatting

open Xunit
open Nanyx.Formatter
open Nanyx.Formatter.RangeFormatting
open Nanyx.Formatter.Tests.TestHelpers

// Formatting the top-level items a range touches, for an editor's "Format Selection".

/// `source` with the edits that format the items from `start` to `finish` made.
let private formatBetween (source: string) (start: int) (finish: int) =
    match formatRange Formatter.Options.defaults "" source start finish with
    | Result.Ok edits ->
        edits
        |> List.sortByDescending _.Start
        |> List.fold (fun (text: string) edit -> text.Substring(0, edit.Start) + edit.Text + text.Substring edit.End) source
    | Result.Error failed -> failwith $"%A{failed}"

let private source =
    normalized """def a   =   1
def b   =   2
def c   =   3"""

[<Fact>]
let ``Only the top-level item the range is in is formatted`` () =
    let offset = source.IndexOf "def b"
    let expected =
        normalized """def a   =   1
def b = 2
def c   =   3"""
    Assert.Equal(expected, formatBetween source (offset + 2) (offset + 3))

[<Fact>]
let ``Every item the range touches is formatted`` () =
    let expected =
        normalized """def a = 1
def b = 2
def c   =   3"""
    Assert.Equal(expected, formatBetween source 3 (source.IndexOf "def b" + 1))

[<Fact>]
let ``The last item, without a line break after it, is formatted`` () =
    let text = "def a = 1\ndef b   =   2"
    Assert.Equal("def a = 1\ndef b = 2", formatBetween text (text.Length - 1) text.Length)

[<Fact>]
let ``A formatted item gives no edits`` () =
    let text = normalized """def a = 1
def b   =   2"""
    Assert.Equal(Result.Ok [], formatRange Formatter.Options.defaults "" text 0 3)

[<Fact>]
let ``An item that spans several lines is formatted whole`` () =
    let text =
        normalized """def a   =   1
def f = { x ->
        x   +   1 }
def c   =   3"""
    let expected =
        normalized """def a   =   1
def f = { x ->
  x + 1
}
def c   =   3"""
    Assert.Equal(expected, formatBetween text (text.IndexOf "def f") (text.IndexOf "def f"))

[<Fact>]
let ``A file that does not parse gives the reason`` () =
    match formatRange Formatter.Options.defaults "" "def = " 0 1 with
    | Result.Error (FormatCommand.NotParsed _) -> ()
    | other -> failwith $"%A{other}"
