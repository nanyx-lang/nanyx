using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Nanyx.LanguageServer.Tests;

/// <summary>
/// A <c>???</c> in the editor (#3200): hover shows the type that goes in its place and what would fill it, and each fill
/// is a quick fix of its error.
/// </summary>
public sealed class TypedHoleTests : LanguageServerTestBase
{
    private const string Text = "def describe: int -> string = { n -> \"n\" }\n\ndef report: string, int -> string = { name, count -> ??? }\n";

    [Fact]
    public async Task Hover_on_a_hole_shows_the_type_that_goes_there()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("typed-hole-hover.nyx"));
        Open(uri, Text);
        Assert.Equal("NYX0111", Assert.Single((await NextPublished(uri)).Diagnostics!).Code?.String);

        var hover = await HoverAt(uri, 2, 55);
        Assert.Equal(
            "```nanyx\nstring\n```\n\n`???` stands for code not written yet: a `string` goes here, such as `name` or `describe(???)`.",
            hover?.Contents.MarkupContent?.Value);
        Assert.Equal(new Range(2, 53, 2, 56), hover?.Range);
    }

    [Fact]
    public async Task Hover_on_a_hole_whose_type_nothing_says_shows_undefined()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("typed-hole-hover-unknown.nyx"));
        Open(uri, "def f = { x -> ??? }\n");
        Assert.Equal("NYX0111", Assert.Single((await NextPublished(uri)).Diagnostics!).Code?.String);

        Assert.Equal("```nanyx\nundefined\n```", (await HoverAt(uri, 0, 16))?.Contents.MarkupContent?.Value);
    }

    [Fact]
    public async Task Each_fill_is_a_quick_fix_that_writes_it_in_the_holes_place()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("typed-hole-fix.nyx"));
        Open(uri, Text);
        Assert.Equal("NYX0111", Assert.Single((await NextPublished(uri)).Diagnostics!).Code?.String);

        var fixes = (await CodeActionsIn(uri, new Range(2, 55, 2, 55))).Where(action => action.Kind == CodeActionKind.QuickFix).ToList();
        Assert.Equal(["Fill in `name`", "Fill in `describe(???)`"], fixes.Select(action => action.Title));
        Assert.Equal(
            "def describe: int -> string = { n -> \"n\" }\n\ndef report: string, int -> string = { name, count -> describe(???) }\n",
            Applied(Text, Assert.Single(fixes[1].Edit!.Changes![uri])));
    }
}
