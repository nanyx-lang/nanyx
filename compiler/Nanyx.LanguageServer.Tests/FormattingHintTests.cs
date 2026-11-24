using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Nanyx.LanguageServer.Tests;

/// <summary>Showing where a document differs from its formatted text, with quick fixes that format it.</summary>
public sealed class FormattingHintTests : LanguageServerTestBase
{
    private Task WithHints() => RestartWithAsync(TestSettings with { FormattingHints = true });

    private static List<Diagnostic> Hints(PublishDiagnosticsParams published) =>
        published.Diagnostics!.Where(diagnostic => diagnostic.Code?.String == FormattingHints.Code).ToList();

    [Fact]
    public async Task Each_block_of_lines_formatting_changes_is_a_hint()
    {
        await WithHints();
        var uri = DocumentUri.Parse("untitled:Untitled-hints");
        Open(uri, """
            def a={1}
            def b = 2
            def c = 3

            def d = (  4)
            """.ReplaceLineEndings("\n") + "\n");

        var hints = Hints(await NextPublished(uri));
        Assert.Equal([new Range(0, 0, 0, 9), new Range(4, 0, 4, 13)], hints.Select(hint => hint.Range));
        Assert.All(hints, hint => Assert.Equal((DiagnosticSeverity.Hint, FormattingHints.Message), (hint.Severity!.Value, hint.Message)));
    }

    [Fact]
    public async Task A_formatted_document_has_no_hints()
    {
        await WithHints();
        var uri = DocumentUri.Parse("untitled:Untitled-hints-formatted");
        Open(uri, "def a = { 1 }\ndef b = 2\n");

        Assert.Empty(Hints(await NextPublished(uri)));
    }

    [Fact]
    public async Task A_document_with_a_type_error_has_hints_and_one_that_does_not_parse_has_none()
    {
        await WithHints();
        var typeError = DocumentUri.Parse("untitled:Untitled-hints-type-error");
        Open(typeError, "def a={1}\ndef b: string = 2\n");
        var published = await NextPublished(typeError);
        Assert.Contains(published.Diagnostics!, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Single(Hints(published));

        var parseError = DocumentUri.Parse("untitled:Untitled-hints-parse-error");
        Open(parseError, "def a={1}\ndef = = =\n");
        Assert.Empty(Hints(await NextPublished(parseError)));
    }

    [Fact]
    public async Task A_hints_quick_fix_formats_its_lines_and_another_formats_the_document()
    {
        await WithHints();
        var uri = DocumentUri.Parse("untitled:Untitled-hints-fix");
        const string text = "def a={1}\ndef b = 2\ndef c=(3,\n  4)\n";
        Open(uri, text);
        var hints = Hints(await NextPublished(uri));
        Assert.Equal(2, hints.Count);

        var actions = (await CodeActionsIn(uri, new Range(0, 2, 0, 2))).Where(action => action.Kind == CodeActionKind.QuickFix).ToList();
        Assert.Equal(["Format these lines", "Format the document"], actions.Select(action => action.Title));

        var these = actions[0];
        Assert.True(these.IsPreferred);
        Assert.Equal(FormattingHints.Code, Assert.Single(these.Diagnostics!).Code?.String);
        Assert.Equal("def a = { 1 }\ndef b = 2\ndef c=(3,\n  4)\n", Applied(text, these.Edit!.Changes![uri]));

        var whole = actions[1];
        Assert.Equal(2, whole.Diagnostics!.Count());
        Assert.Equal("def a = { 1 }\ndef b = 2\ndef c = 3, 4\n", Applied(text, whole.Edit!.Changes![uri]));

        // No formatting fix where the lines are formatted.
        Assert.DoesNotContain(await CodeActionsIn(uri, new Range(1, 2, 1, 2)), action => action.Kind == CodeActionKind.QuickFix);
    }

    [Fact]
    public async Task A_hints_fix_keeps_the_documents_CRLF_line_endings()
    {
        await WithHints();
        var uri = DocumentUri.Parse("untitled:Untitled-hints-crlf");
        Open(uri, "def a = 1\r\ndef b=(2,\r\n  3)\r\ndef c = 4\r\n");
        Assert.Single(Hints(await NextPublished(uri)));

        var fix = (await CodeActionsIn(uri, new Range(1, 0, 1, 0))).First(action => action.Title == "Format these lines");
        var edit = Assert.Single(fix.Edit!.Changes![uri]);
        Assert.Equal((new Range(1, 0, 3, 0), "def b = 2, 3\r\n"), (edit.Range, edit.NewText));
    }

    [Fact]
    public async Task A_hint_reaching_the_end_of_a_document_with_no_final_line_break_formats_to_its_end()
    {
        await WithHints();
        var uri = DocumentUri.Parse("untitled:Untitled-hints-end");
        const string text = "def a = 1\ndef b={2}";
        Open(uri, text);
        Assert.Equal(new Range(1, 0, 1, 9), Assert.Single(Hints(await NextPublished(uri))).Range);

        var fix = (await CodeActionsIn(uri, new Range(1, 0, 1, 0))).First(action => action.Title == "Format these lines");
        Assert.Equal("def a = 1\ndef b = { 2 }\n", Applied(text, fix.Edit!.Changes![uri]));
    }

    [Fact]
    public async Task With_hints_off_there_are_none_and_no_fixes()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-hints-off");
        Open(uri, "def a={1}\n");

        Assert.Empty(Hints(await NextPublished(uri)));
        Assert.DoesNotContain(await CodeActionsIn(uri, new Range(0, 2, 0, 2)), action => action.Kind == CodeActionKind.QuickFix);
    }
}
