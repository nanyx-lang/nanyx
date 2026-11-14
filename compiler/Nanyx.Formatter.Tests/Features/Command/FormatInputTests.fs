/// `nanyx format --stdin`: formatting standard input to standard output, for an editor's unsaved buffer.
module Nanyx.Formatter.Tests.FormatInputTests

open System.IO
open System.Text
open Xunit
open Nanyx.Formatter
open Nanyx.Formatter.FormatCommand
open Nanyx.Formatter.Tests.TestHelpers

/// Formats `input` as standard input, as a file in `directory`, giving the result and what was written out.
let private formatInput (mode: Mode) (directory: string) (input: byte array) =
    use output = new MemoryStream()
    let _, result = runInput (Settings.cached ()) mode directory (new MemoryStream(input)) output
    result, output.ToArray()

let private bytes (text: string) = Encoding.UTF8.GetBytes text

[<Fact>]
let ``Write mode writes the formatted text`` () =
    let result, written = formatInput Write (directoryWith []) (bytes "def add={a,b->a +  b}")
    Assert.Equal(Changed, result)
    Assert.Equal("def add = { a, b -> a + b }\n", Encoding.UTF8.GetString written)

[<Fact>]
let ``Text already formatted is written as it is`` () =
    let result, written = formatInput Write (directoryWith []) (bytes "def x = 1\r\n")
    Assert.Equal(Unchanged, result)
    Assert.Equal("def x = 1\r\n", Encoding.UTF8.GetString written)

[<Fact>]
let ``Check mode writes nothing`` () =
    let result, written = formatInput Check (directoryWith []) (bytes "def add={a,b->a +  b}")
    Assert.Equal(Changed, result)
    Assert.Empty written

[<Fact>]
let ``Text that does not parse writes nothing and names standard input`` () =
    let directory = directoryWith []
    match formatInput Write directory (bytes "def = = =") with
    | Failed(NotParsed error), written ->
        Assert.Equal(Path.Combine(directory, inputName), error.Span.File)
        Assert.Empty written
    | other -> failwith $"Unexpected: %A{other}"

[<Fact>]
let ``A byte order mark is kept`` () =
    let encoding = UTF8Encoding(true)
    let _, written = formatInput Write (directoryWith []) (Array.append (encoding.GetPreamble()) (bytes "def x={1}"))
    Assert.Equal<byte array>(Array.append (encoding.GetPreamble()) (bytes "def x = { 1 }\n"), written)

[<Fact>]
let ``The settings are those of a file in the directory`` () =
    let directory = directoryWith [ "app.nyxp", "[formatter]\nindentSize = 4\n" ]
    let _, written = formatInput Write directory (bytes "def x =\n  def a = 1\n  a\n")
    Assert.Equal("def x =\n    def a = 1\n    a\n", Encoding.UTF8.GetString written)
