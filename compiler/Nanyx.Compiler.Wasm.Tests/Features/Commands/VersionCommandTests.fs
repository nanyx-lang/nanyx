module TranspilerWasmTests.Features.Commands.Version

open System.IO
open System.Xml.Linq
open Xunit
open TranspilerWasmTestHelpers

[<Fact>]
let ``Version - prints the release version from Directory.Build.props, in three parts`` () =
    let project = XDocument.Load(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "..", "Directory.Build.props"))
    let version = project.Descendants(XName.Get "Version") |> Seq.exactlyOne |> _.Value
    let outcome = nanyxWithInput "" [ "--version" ]
    Assert.Equal(0, outcome.ExitCode)
    Assert.Equal(version, normalizeOutput outcome.StandardOutput)
    Assert.Matches(@"^\d+\.\d+\.\d+$", version)
