using OmniSharp.Extensions.LanguageServer.Protocol;
using Xunit;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Nanyx.LanguageServer.Tests;

/// <summary>A name a module re-exports is the definition in the module it comes from.</summary>
public sealed class ReExportTests : LanguageServerTestBase
{
    [Fact]
    public async Task Go_to_definition_through_a_whole_module_re_export_lands_in_the_original_module()
    {
        File.WriteAllText(TempPath("acme.web.nyxp"), """
            (package = (name = "acme.web", version = "0.1.0", sources = ["src/**/*.nyx"]))
            """.ReplaceLineEndings("\n"));
        var shapes = TempPath("src", "shapes.nyx");
        File.WriteAllText(shapes, """
            module shapes

            export def area: int -> int = { n -> n * n }
            """.ReplaceLineEndings("\n"));
        File.WriteAllText(TempPath("src", "geometry.nyx"), """
            module geometry

            export shapes
            """.ReplaceLineEndings("\n"));
        var uri = DocumentUri.FromFileSystemPath(TempPath("src", "main.nyx"));

        Open(uri, """
            module main

            import geometry

            def value = area(2)
            """.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var definition = Assert.Single(await DefinitionsAt(uri, 4, 13));
        Assert.Equal(DocumentUri.FromFileSystemPath(shapes), definition.Location!.Uri);
        Assert.Equal(new Range(2, 11, 2, 15), definition.Location.Range);
    }
}
