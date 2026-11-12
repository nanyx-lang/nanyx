using System.IO.Pipelines;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using OmniSharp.Extensions.LanguageServer.Client;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;
using Xunit;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;
using Linter = NyxCompiler.Linter;
using LinterOptions = NyxCompiler.LinterOptions;
using Fix = NyxCompiler.Fix;
using Report = NyxCompiler.Report;
using Rule = NyxCompiler.Rule;
using SemanticModel = NyxCompiler.SemanticModel;
using Severity = NyxCompiler.Severity;

namespace Nanyx.LanguageServer.Tests;

/// <summary>Find references.</summary>
[Collection(LanguageServerTestBase.Collection)]
public sealed class ReferencesTests : LanguageServerTestBase
{
    [Fact]
    public void Server_advertises_references()
    {
        Assert.NotNull(_client.ServerSettings.Capabilities.ReferencesProvider);
    }

    [Fact]
    public async Task References_are_the_names_that_refer_to_a_declaration_and_the_declaration_when_asked()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-5");

        Open(uri, "def twice = { n -> n * 2 }\ndef four = twice(2)\ndef eight = twice(four)\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var calls = new[] { new Range(1, 11, 1, 16), new Range(2, 12, 2, 17) };
        // From a use, without the declaration.
        Assert.Equal(calls, (await ReferencesAt(uri, 2, 13, includeDeclaration: false)).Select(location => location.Range));
        // From the declaration, with it, which comes first.
        Assert.Equal(
            calls.Prepend(new Range(0, 4, 0, 9)),
            (await ReferencesAt(uri, 0, 5, includeDeclaration: true)).Select(location => location.Range));
        Assert.All(await ReferencesAt(uri, 0, 5, includeDeclaration: true), location => Assert.Equal(uri, location.Uri));
        // A parameter.
        Assert.Equal(
            [new Range(0, 19, 0, 20)],
            (await ReferencesAt(uri, 0, 14, includeDeclaration: false)).Select(location => location.Range));
    }

    [Fact]
    public async Task References_hover_and_definition_on_a_user_defined_operator_point_at_the_operator()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "user-operator.nyx"));

        Open(uri, """
            type Point = (x: int, y: int)
            --| Adds two points.
            def Point.`+`: Point, Point -> Point = { a, b -> Point(x = a.x + b.x, y = a.y + b.y) }
            def q: Point = Point(x = 1, y = 2) + Point(x = 3, y = 4)
            def r: Point = q + q
            """);
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var uses = new[] { new Range(3, 35, 3, 36), new Range(4, 17, 4, 18) };
        // From a use, and from the declaration; the `+`s on ints in its body are not among them.
        Assert.Equal(uses, (await ReferencesAt(uri, 3, 35, includeDeclaration: false)).Select(location => location.Range));
        Assert.Equal(
            uses.Prepend(new Range(2, 4, 2, 13)),
            (await ReferencesAt(uri, 2, 5, includeDeclaration: true)).Select(location => location.Range));

        var hover = await HoverAt(uri, 3, 35);
        Assert.Equal("```nanyx\ndef Point.+: Point, Point -> Point\n```\n\n---\n\nAdds two points.", hover?.Contents.MarkupContent?.Value);
        Assert.Equal(new Range(3, 35, 3, 36), hover?.Range);
        Assert.Equal(new Range(2, 4, 2, 13), (await SingleDefinitionAt(uri, 3, 35))?.Range);
        // Beside the operator, an operand is hovered as before.
        Assert.Equal("```nanyx\ndef q: Point\n```", (await HoverAt(uri, 4, 15))?.Contents.MarkupContent?.Value);
    }
}
