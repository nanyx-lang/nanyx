using OmniSharp.Extensions.LanguageServer.Protocol;
using Xunit;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Nanyx.LanguageServer.Tests;

/// <summary>
/// Hover on a built-in type's name written in a type (#2876): what the type is, and its other name where it has one, as
/// <c>int</c> is <c>I64</c>.
/// </summary>
public sealed class BuiltInTypeHoverTests : LanguageServerTestBase
{
    private const string SizedNumbers = "See [Sized numbers](https://nanyx.dev/docs/advanced/sized-numbers).";
    private const string Types = "See [Types](https://nanyx.dev/docs/tour/types).";

    private const string Int =
        "```nanyx\ntype int = I64\n```\n\n`int` is another name for `I64`, a 64-bit signed integer on every target: an `int` is an `I64`, not converted to one. "
        + SizedNumbers;

    private const string Float =
        "```nanyx\ntype float = F64\n```\n\n`float` is another name for `F64`, a 64-bit floating-point number: a `float` is an `F64`. " + SizedNumbers;

    private const string Char = "```nanyx\ntype char\n```\n\nOne Unicode scalar value, written `'a'`. " + Types;

    private const string List =
        "```nanyx\ntype list(a)\n```\n\nThe built-in list type, also written `List`: `List(int)` is `list(int)`, the same type. " + Types;

    [Fact]
    public async Task A_built_in_type_in_a_signature_says_what_it_is()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("hover-builtin-signature.nyx"));
        Open(uri, "def half: int -> float = { _ -> 1.0 }\n\ndef first: char -> I64 = { _ -> 1 }\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var intHover = await HoverAt(uri, 0, 11);
        Assert.Equal(Int, intHover?.Contents.MarkupContent?.Value);
        Assert.Equal(new Range(0, 10, 0, 13), intHover?.Range);

        var floatHover = await HoverAt(uri, 0, 19);
        Assert.Equal(Float, floatHover?.Contents.MarkupContent?.Value);
        Assert.Equal(new Range(0, 17, 0, 22), floatHover?.Range);

        Assert.Equal(Char, (await HoverAt(uri, 2, 12))?.Contents.MarkupContent?.Value);

        // `I64` is `int`, so it says the same.
        var i64 = await HoverAt(uri, 2, 21);
        Assert.Equal(Int, i64?.Contents.MarkupContent?.Value);
        Assert.Equal(new Range(2, 19, 2, 22), i64?.Range);
    }

    [Fact]
    public async Task A_built_in_type_in_a_type_argument_says_what_it_is()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("hover-builtin-argument.nyx"));
        Open(uri, "def xs: List(int) = [1, 2]\n\ndef ys: list(float) = [1.0]\n\ndef zs: list(I64) = []\n\ndef cs: List(char) = []\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var list = await HoverAt(uri, 0, 9);
        Assert.Equal(List, list?.Contents.MarkupContent?.Value);
        Assert.Equal(new Range(0, 8, 0, 12), list?.Range);
        Assert.Equal(Int, (await HoverAt(uri, 0, 14))?.Contents.MarkupContent?.Value);
        Assert.Equal(List, (await HoverAt(uri, 2, 9))?.Contents.MarkupContent?.Value);
        Assert.Equal(Float, (await HoverAt(uri, 2, 15))?.Contents.MarkupContent?.Value);
        Assert.Equal(Int, (await HoverAt(uri, 4, 14))?.Contents.MarkupContent?.Value);
        Assert.Equal(Char, (await HoverAt(uri, 6, 14))?.Contents.MarkupContent?.Value);
    }

    [Fact]
    public async Task A_type_the_module_defines_is_that_type_and_an_expression_still_shows_its_type()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("hover-builtin-own.nyx"));
        Open(uri, "type Set(a) = (items: list(a))\n\ndef s: Set(int) = (items = [1])\n\ndef n = 1\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        Assert.StartsWith("```nanyx\ntype Set(a)", (await HoverAt(uri, 2, 8))?.Contents.MarkupContent?.Value);
        Assert.Equal("```nanyx\ndef n: int\n```", (await HoverAt(uri, 4, 4))?.Contents.MarkupContent?.Value);
    }
}
