using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Nanyx.LanguageServer.Tests;

/// <summary>A module's doc comment, the <c>--|</c> comment before its <c>module</c> line, wherever the module is named.</summary>
[Collection(LanguageServerTestBase.Collection)]
public sealed class ModuleDocTests : LanguageServerTestBase
{
    [Fact]
    public async Task Hover_on_a_module_line_shows_the_modules_doc_comment()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "module-doc.nyx"));

        Open(uri, """
            --| Plays fizzbuzz.
              See [`play`].
            module fizzbuzz

            export def play = 1

            """.ReplaceLineEndings("\n"));
        await NextPublished(uri);

        var hover = await HoverAt(uri, 2, 10);
        Assert.Equal(
            $"""
            ```nanyx
            module fizzbuzz
            ```

            ---

            Plays fizzbuzz.
            See [`play`]({uri}#L5,12).
            """.ReplaceLineEndings("\n"),
            hover?.Contents.MarkupContent?.Value);
        Assert.Equal(new Range(2, 0, 2, 15), hover?.Range);
    }

    [Fact]
    public async Task Hover_on_an_import_its_alias_and_a_qualifier_shows_the_imported_modules_doc_comment()
    {
        var directory = Directory.CreateTempSubdirectory("nanyx-module-doc-");
        try
        {
            var modulePath = Path.Combine(directory.FullName, "mod.nyx");
            File.WriteAllText(modulePath, """
                --| Wraps values.
                  See [`wrap`].
                module mod

                export def wrap: a -> #some(a) | #nothing = { x -> #some(x) }

                """.ReplaceLineEndings("\n"));
            var uri = DocumentUri.FromFileSystemPath(Path.Combine(directory.FullName, "main.nyx"));

            Open(uri, """
                module main
                import mod as m
                def w = m.wrap("hi")

                """.ReplaceLineEndings("\n"));
            Assert.Empty((await NextPublished(uri)).Diagnostics!);

            var expected = $"""
                ```nanyx
                module mod
                ```

                ---

                Wraps values.
                See [`wrap`]({DocumentUri.FromFileSystemPath(modulePath)}#L5,12).
                """.ReplaceLineEndings("\n");
            var path = await HoverAt(uri, 1, 8);
            Assert.Equal(expected, path?.Contents.MarkupContent?.Value);
            Assert.Equal(new Range(1, 7, 1, 15), path?.Range);
            Assert.Equal(expected, (await HoverAt(uri, 1, 14))?.Contents.MarkupContent?.Value);

            var qualifier = await HoverAt(uri, 2, 8);
            Assert.Equal(expected, qualifier?.Contents.MarkupContent?.Value);
            Assert.Equal(new Range(2, 8, 2, 9), qualifier?.Range);
            // The name after the qualifier is the def's.
            Assert.StartsWith("```nanyx\ndef wrap", (await HoverAt(uri, 2, 11))?.Contents.MarkupContent?.Value);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Hover_on_a_standard_library_import_shows_the_modules_doc_comment()
    {
        var uri = DocumentUri.Parse("untitled:ModuleDoc-1");

        Open(uri, "import nanyx/text\n\ndef shout = string.uppercase(\"hi\")\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        Assert.StartsWith(
            "```nanyx\nmodule text\n```\n\n---\n\nFunctions on strings.\n",
            (await HoverAt(uri, 0, 10))?.Contents.MarkupContent?.Value);
    }

    [Fact]
    public async Task Completion_in_an_import_shows_each_modules_doc_comment()
    {
        var directory = Directory.CreateTempSubdirectory("nanyx-module-doc-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "util.nyx"), """
                --| Utilities.
                module util

                """.ReplaceLineEndings("\n"));
            var uri = DocumentUri.FromFileSystemPath(Path.Combine(directory.FullName, "main.nyx"));

            Open(uri, "import nanyx/te\n");
            await NextPublished(uri);
            var text = (await CompletionsAt(uri, 0, 15)).Single(item => item.Label == "text");
            Assert.Equal(MarkupKind.Markdown, text.Documentation?.MarkupContent?.Kind);
            Assert.StartsWith("Functions on strings.\n", text.Documentation?.MarkupContent?.Value);

            Change(uri, version: 2, "import \"\n");
            await NextPublished(uri);
            var util = Assert.Single(await CompletionsAt(uri, 0, 8));
            Assert.Equal(("util.nyx", "Utilities."), (util.Label, util.Documentation?.MarkupContent?.Value));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Info_view_shows_an_imports_module_doc_comment()
    {
        var directory = Directory.CreateTempSubdirectory("nanyx-module-doc-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "mod.nyx"), """
                --| Wraps values.
                module mod

                export def wrap: a -> #some(a) | #nothing = { x -> #some(x) }

                """.ReplaceLineEndings("\n"));
            var uri = DocumentUri.FromFileSystemPath(Path.Combine(directory.FullName, "main.nyx"));

            Open(uri, """
                module main
                import mod as m
                def w = m.wrap("hi")

                """.ReplaceLineEndings("\n"));
            Assert.Empty((await NextPublished(uri)).Diagnostics!);

            var topLevel = Section(await InfoViewAt(uri, 2, 11), "topLevel").Items;
            var import = Assert.Single(topLevel, item => item.Detail == "import");
            Assert.Equal(("m", "Wraps values."), (import.Text, import.Documentation));
            Assert.Null(Assert.Single(topLevel, item => item.Text.StartsWith("w:")).Documentation);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
