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

/// <summary>Formatting a document.</summary>
[Collection(LanguageServerTestBase.Collection)]
public sealed class FormattingTests : LanguageServerTestBase
{
    [Fact]
    public void Server_advertises_document_formatting()
    {
        Assert.NotNull(_client.ServerSettings.Capabilities.DocumentFormattingProvider);
    }

    [Fact]
    public async Task Formatting_a_document_replaces_all_of_it_with_the_formatted_text()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-format");
        Open(uri, "def add={a,b->a+b}\n\ndef two = add(1,1)");
        await NextPublished(uri);

        var edit = Assert.Single(await FormattingEdits(uri));
        Assert.Equal(new Range(0, 0, 2, 18), edit.Range);
        Assert.Equal("def add = { a, b -> a + b }\n\ndef two = add(1, 1)\n", edit.NewText);
    }

    [Fact]
    public async Task Formatting_keeps_CRLF_line_endings()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-format-crlf");
        Open(uri, "def a={1}\r\ndef b = 2\r\n");
        await NextPublished(uri);

        var edit = Assert.Single(await FormattingEdits(uri));
        Assert.Equal(new Range(0, 0, 2, 0), edit.Range);
        Assert.Equal("def a = { 1 }\r\ndef b = 2\r\n", edit.NewText);
    }

    [Fact]
    public async Task Formatting_a_formatted_document_changes_nothing()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-formatted");
        Open(uri, "def add = { a, b -> a + b }\n");
        await NextPublished(uri);

        Assert.Empty(await FormattingEdits(uri));
    }

    [Fact]
    public async Task A_document_that_does_not_parse_is_not_formatted()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-unparsed");
        Open(uri, "def add={a,b->a+b}\ndef = = =\n");
        await NextPublished(uri);

        Assert.Empty(await FormattingEdits(uri));
    }

    [Fact]
    public async Task Formatting_a_script_keeps_its_directives_and_uses_its_formatter_directives()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-format-directives");
        Open(uri, "::formatter.indentSize = 4\ndef x =\n  def a = 1\n  a\n");
        await NextPublished(uri);

        var edit = Assert.Single(await FormattingEdits(uri));
        Assert.Equal("::formatter.indentSize = 4\ndef x =\n    def a = 1\n    a\n", edit.NewText);
    }

    private async Task<List<TextEdit>> FormattingEdits(DocumentUri uri) =>
        (await _client.TextDocument.RequestDocumentFormatting(
            new DocumentFormattingParams
            {
                TextDocument = new TextDocumentIdentifier(uri),
                Options = new FormattingOptions { TabSize = 4, InsertSpaces = false }
            },
            _cancellation.Token))?.ToList() ?? [];
}
