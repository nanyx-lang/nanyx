using Xunit;

namespace Nanyx.LanguageServer.Tests;

public sealed class CompletionContextTests
{
    /// <summary>Whether names are offered where <c>|</c> is written in <paramref name="marked"/>.</summary>
    private static bool OffersNamesAt(string marked)
    {
        var cursor = marked.IndexOf('|');
        var text = marked.Remove(cursor, 1);
        return CompletionContext.OffersNames(text, CompletionContext.WordStart(text, cursor));
    }

    [Fact]
    public void The_word_being_typed_starts_after_the_last_character_a_name_cannot_have()
    {
        Assert.Equal(8, CompletionContext.WordStart("def a = fac", 11));
        Assert.Equal(12, CompletionContext.WordStart("def a = fac ", 12));
    }

    [Theory]
    [InlineData("def a = fa|")]
    [InlineData("def a = (fa|")]
    [InlineData("def a = \"x {fa|} y\"")]
    [InlineData("def a = \"x {{ y\"\ndef b = fa|")]
    [InlineData("-- a comment\ndef b = fa|")]
    [InlineData("---\nin a block comment\n---\ndef b = fa|")]
    [InlineData("def a = 'raw {x}'\ndef b = fa|")]
    [InlineData("def a = { x -> x }\ndef b = fa|")]
    public void Names_are_offered_in_code(string marked) => Assert.True(OffersNamesAt(marked));

    [Theory]
    [InlineData("def a = 1 -- fa|")]
    [InlineData("---\nfa|\n---\n")]
    [InlineData("def a = \"fa|")]
    [InlineData("def a = \"x {b} fa|\"")]
    [InlineData("def a = \"x \\\" fa|\"")]
    [InlineData("def a = 'fa|'")]
    [InlineData("def a = point.fa|")]
    [InlineData("def a = #fa|")]
    [InlineData("def a = 12|")]
    [InlineData("def fa|")]
    [InlineData("  rec fa|")]
    public void Names_are_not_offered_outside_code_after_a_dot_or_for_a_new_def(string marked) =>
        Assert.False(OffersNamesAt(marked));
}
