using OmniSharp.Extensions.JsonRpc.Server;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Nanyx.LanguageServer.Tests;

/// <summary>
/// Go to definition, find references and rename on a dimension, where a type names it, and on a unit, where a number is
/// written with it or another unit's definition names it (#2866).
/// </summary>
public sealed class MeasureNavigationTests : LanguageServerTestBase
{
    /// <summary>A dimension, its units, and uses of each.</summary>
    private const string Lengths = """
        dimension Length(float)

        @baseUnit unit m: Length
        unit km: Length = 1_000m

        def walk = 2km + 350m
        def trip: Length = walk + 1km
        println(trip)

        """;

    private async Task<DocumentUri> Opened(string name, string text)
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath(name));
        Open(uri, text.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(uri)).Diagnostics!);
        return uri;
    }

    [Fact]
    public async Task Definition_of_a_unit_in_a_number_or_another_unit_s_definition_is_its_unit_line()
    {
        var uri = await Opened("measure-definition-unit.nyx", Lengths);

        // `km` in `2km`, and `m` in `1_000m`.
        Assert.Equal(new Location { Uri = uri, Range = new Range(3, 5, 3, 7) }, await SingleDefinitionAt(uri, 5, 12));
        Assert.Equal(new Location { Uri = uri, Range = new Range(2, 15, 2, 16) }, await SingleDefinitionAt(uri, 3, 23));
    }

    [Fact]
    public async Task Definition_of_a_dimension_in_a_type_is_its_dimension_line()
    {
        var uri = await Opened("measure-definition-dimension.nyx", Lengths);

        Assert.Equal(new Location { Uri = uri, Range = new Range(0, 10, 0, 16) }, await SingleDefinitionAt(uri, 6, 11));
    }

    [Fact]
    public async Task Definition_of_the_prelude_s_Time_and_ms_is_in_the_standard_library()
    {
        var uri = await Opened("measure-definition-prelude.nyx", "def t: Time(int) = 5ms\n");

        var dimension = await SingleDefinitionAt(uri, 0, 8);
        Assert.NotNull(dimension);
        Assert.EndsWith("prelude-time.nyx", dimension.Uri.Path);
        var unit = await SingleDefinitionAt(uri, 0, 20);
        Assert.NotNull(unit);
        Assert.EndsWith("prelude-time.nyx", unit.Uri.Path);
        Assert.Equal(2, unit.Range.End.Character - unit.Range.Start.Character);
    }

    [Fact]
    public async Task References_of_a_unit_and_a_dimension_are_their_declaration_and_every_use()
    {
        var uri = await Opened("measure-references.nyx", Lengths);

        var kilometres = await ReferencesAt(uri, 3, 6, includeDeclaration: true);
        Assert.Equal(
            [new Range(3, 5, 3, 7), new Range(5, 12, 5, 14), new Range(6, 27, 6, 29)],
            kilometres.Select(location => location.Range).OrderBy(range => range.Start.Line).ThenBy(range => range.Start.Character));

        var metres = await ReferencesAt(uri, 2, 15, includeDeclaration: false);
        Assert.Equal(
            [new Range(3, 23, 3, 24), new Range(5, 20, 5, 21)],
            metres.Select(location => location.Range).OrderBy(range => range.Start.Line).ThenBy(range => range.Start.Character));

        var length = await ReferencesAt(uri, 0, 12, includeDeclaration: true);
        Assert.Equal(
            [new Range(0, 10, 0, 16), new Range(2, 18, 2, 24), new Range(3, 9, 3, 15), new Range(6, 10, 6, 16)],
            length.Select(location => location.Range).OrderBy(range => range.Start.Line).ThenBy(range => range.Start.Character));
    }

    [Fact]
    public async Task A_derived_dimension_s_parts_are_uses_of_the_dimensions_it_names()
    {
        var uri = await Opened("measure-references-derived.nyx", """
            dimension Length
            dimension Time
            dimension Speed = Length / Time

            @baseUnit unit m: Length
            @baseUnit unit s: Time

            """);

        var length = await ReferencesAt(uri, 0, 12, includeDeclaration: false);
        Assert.Equal(
            [new Range(2, 18, 2, 24), new Range(4, 18, 4, 24)],
            length.Select(location => location.Range).OrderBy(range => range.Start.Line));
        Assert.Equal(new Location { Uri = uri, Range = new Range(1, 10, 1, 14) }, await SingleDefinitionAt(uri, 2, 28));
    }

    [Fact]
    public async Task Rename_of_a_unit_renames_every_number_written_with_it()
    {
        var uri = await Opened("measure-rename-unit.nyx", Lengths);

        var prepared = await _client.TextDocument.PrepareRename(
            new PrepareRenameParams { TextDocument = new TextDocumentIdentifier(uri), Position = new Position(5, 13) },
            _cancellation.Token);
        Assert.Equal(("km", new Range(5, 12, 5, 14)), (prepared!.PlaceholderRange!.Placeholder, prepared.PlaceholderRange.Range));

        var edit = await _client.TextDocument.RequestRename(
            new RenameParams { TextDocument = new TextDocumentIdentifier(uri), Position = new Position(5, 13), NewName = "kilo" },
            _cancellation.Token);
        Assert.Equal(
            Lengths.ReplaceLineEndings("\n").Replace("unit km:", "unit kilo:").Replace("2km", "2kilo").Replace("1km", "1kilo"),
            Applied(Lengths.ReplaceLineEndings("\n"), edit!.Changes![uri]));
    }

    [Fact]
    public async Task Rename_of_a_dimension_renames_every_type_and_unit_line_that_names_it()
    {
        var uri = await Opened("measure-rename-dimension.nyx", Lengths);

        var edit = await _client.TextDocument.RequestRename(
            new RenameParams { TextDocument = new TextDocumentIdentifier(uri), Position = new Position(6, 12), NewName = "Distance" },
            _cancellation.Token);
        Assert.Equal(Lengths.ReplaceLineEndings("\n").Replace("Length", "Distance"), Applied(Lengths.ReplaceLineEndings("\n"), edit!.Changes![uri]));
    }

    [Fact]
    public async Task Rename_of_a_unit_to_another_unit_s_name_or_one_no_number_can_be_written_with_fails()
    {
        var uri = await Opened("measure-rename-clash.nyx", Lengths);

        foreach (var newName in new[] { "m", "ms", "e5", "_km" })
        {
            var failure = await Assert.ThrowsAsync<RequestFailedException>(() => _client.TextDocument.RequestRename(
                new RenameParams { TextDocument = new TextDocumentIdentifier(uri), Position = new Position(3, 6), NewName = newName },
                _cancellation.Token));
            Assert.Equal(-32803, failure.ErrorCode);
        }
    }

    [Fact]
    public async Task Definition_references_and_rename_reach_a_unit_and_a_dimension_another_file_of_the_package_exports()
    {
        File.WriteAllText(TempPath("app.nyxp"), """
            (package = (name = "app", version = "0.1.0", sources = ["src/**/*.nyx"]))
            """.ReplaceLineEndings("\n"));
        var measuresText = """
            module measures

            export dimension Length(float)

            @baseUnit export unit m: Length
            export unit km: Length = 1_000m
            """.ReplaceLineEndings("\n");
        var measures = TempPath("src", "measures.nyx");
        File.WriteAllText(measures, measuresText);
        var mainText = """
            module main

            import app/measures

            def trip: Length = 2km
            """.ReplaceLineEndings("\n");
        var main = DocumentUri.FromFileSystemPath(TempPath("src", "main.nyx"));
        File.WriteAllText(TempPath("src", "main.nyx"), mainText);

        Open(main, mainText);
        Assert.Empty((await NextPublished(main)).Diagnostics!);

        var measuresUri = DocumentUri.FromFileSystemPath(measures);
        Assert.Equal(new Location { Uri = measuresUri, Range = new Range(5, 12, 5, 14) }, await SingleDefinitionAt(main, 4, 21));
        Assert.Equal(new Location { Uri = measuresUri, Range = new Range(2, 17, 2, 23) }, await SingleDefinitionAt(main, 4, 12));

        var references = await ReferencesAt(main, 4, 21, includeDeclaration: true);
        Assert.Contains(new Location { Uri = main, Range = new Range(4, 20, 4, 22) }, references);
        Assert.Contains(new Location { Uri = measuresUri, Range = new Range(5, 12, 5, 14) }, references);

        var edit = await _client.TextDocument.RequestRename(
            new RenameParams { TextDocument = new TextDocumentIdentifier(main), Position = new Position(4, 21), NewName = "kilo" },
            _cancellation.Token);
        Assert.Equal(mainText.Replace("2km", "2kilo"), Applied(mainText, edit!.Changes![main]));
        Assert.Equal(measuresText.Replace("unit km:", "unit kilo:"), Applied(measuresText, edit.Changes[measuresUri]));
    }
}
