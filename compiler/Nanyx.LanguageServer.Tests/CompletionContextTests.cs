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

    /// <summary>The path before the dot the word at <c>|</c> in <paramref name="marked"/> follows, dot-separated.</summary>
    private static string? MemberPathAt(string marked)
    {
        var cursor = marked.IndexOf('|');
        var text = marked.Remove(cursor, 1);
        return CompletionContext.MemberPath(text, CompletionContext.WordStart(text, cursor)) is { } path
            ? string.Join('.', path)
            : null;
    }

    [Theory]
    [InlineData("def a = p.|", "p")]
    [InlineData("def a = p.na|", "p")]
    [InlineData("def a = f(p.inner.|", "p.inner")]
    [InlineData("def a = \"{t.String.|}\"", "t.String")]
    public void The_path_before_a_dot_is_the_names_written_there(string marked, string path) =>
        Assert.Equal(path, MemberPathAt(marked));

    [Theory]
    [InlineData("def a = p|")]
    [InlineData("def a = 1.|")]
    [InlineData("def a = 1..|")]
    [InlineData("def a = f(x).|")]
    [InlineData("def a = #tag.|")]
    [InlineData("def a = 1 -- p.|")]
    [InlineData("def a = \"p.|\"")]
    public void There_is_no_path_where_the_word_follows_no_dot_or_no_name(string marked) =>
        Assert.Null(MemberPathAt(marked));

    /// <summary>The names piped into the word at <c>|</c> in <paramref name="marked"/>, dot-separated.</summary>
    private static string? PipedPathAt(string marked)
    {
        var cursor = marked.IndexOf('|');
        var text = marked.Remove(cursor, 1);
        return CompletionContext.PipedPath(text, CompletionContext.WordStart(text, cursor)) is { } path
            ? string.Join('.', path)
            : null;
    }

    [Theory]
    [InlineData("def a = s \\|", "s")]
    [InlineData("def a = s\\tr|", "s")]
    [InlineData("def a = s \\ tr|", "s")]
    [InlineData("def a = p.inner \\|", "p.inner")]
    [InlineData("def a = xs\n  \\|", "xs")]
    [InlineData("def a = f(x) \\|", "")]
    [InlineData("def a = \"s\" \\|", "")]
    [InlineData("def a = 12 \\|", "")]
    public void The_value_piped_is_read_from_the_names_before_the_pipe(string marked, string path) =>
        Assert.Equal(path, PipedPathAt(marked));

    [Theory]
    [InlineData("def a = s|")]
    [InlineData("def a = s \\split(o|")]
    [InlineData("def a = \"s \\|\"")]
    [InlineData("def a = 1 -- s \\|")]
    public void There_is_no_piped_value_where_the_word_follows_no_pipe(string marked) =>
        Assert.Null(PipedPathAt(marked));

    private static ImportPath? ImportPathAt(string marked)
    {
        var cursor = marked.IndexOf('|');
        return CompletionContext.ImportPathAt(marked.Remove(cursor, 1), cursor);
    }

    [Theory]
    [InlineData("import |", false, "")]
    [InlineData("import nan|", false, "nan")]
    [InlineData("import nanyx/te|", false, "nanyx/te")]
    [InlineData("module main\n\nimport nanyx/text.|", false, "nanyx/text.")]
    [InlineData("import (\n  nanyx/console\n  nanyx/|", false, "nanyx/")]
    [InlineData("import (nanyx/console |", false, "")]
    [InlineData("import \"|", true, "")]
    [InlineData("import \"sub/ut|", true, "sub/ut")]
    [InlineData("import (\n  \"../shared/|", true, "../shared/")]
    public void The_import_path_written_up_to_the_cursor_is_read(string marked, bool isFile, string written) =>
        Assert.Equal(new ImportPath(isFile, written), ImportPathAt(marked));

    [Theory]
    [InlineData("def a = nanyx|")]
    [InlineData("imp|")]
    [InlineData("import nanyx/text as t|")]
    [InlineData("import \"util.nyx\"|")]
    [InlineData("import nanyx/text\ndef a = b|")]
    [InlineData("import (\n  nanyx/text\n)\ndef a = b|")]
    [InlineData("import (\n  nanyx/text\n)|")]
    public void There_is_no_import_path_outside_an_imports_items(string marked) =>
        Assert.Null(ImportPathAt(marked));
}
