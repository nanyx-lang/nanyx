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

/// <summary>Publishing diagnostics: when, where and which.</summary>
[Collection(LanguageServerTestBase.Collection)]
public sealed class DiagnosticsTests : LanguageServerTestBase
{
    [Fact]
    public void Server_advertises_full_document_sync()
    {
        var sync = _client.ServerSettings.Capabilities.TextDocumentSync;

        Assert.NotNull(sync);
        Assert.Equal(TextDocumentSyncKind.Full, sync.Options?.Change);
        Assert.True(sync.Options?.OpenClose);
    }

    [Fact]
    public async Task Type_error_is_published_with_its_range_on_open()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("type-error.nyx"));

        Open(uri, "import nanyx/console\n\nprintln(missing)\n");

        var diagnostics = await NextPublished(uri);
        var diagnostic = Assert.Single(diagnostics.Diagnostics!);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("nanyx", diagnostic.Source);
        Assert.Equal("NYX0101", diagnostic.Code?.String);
        Assert.Equal(new Uri("https://nanyx.dev/docs/standard-tools/errors#nyx0101"), diagnostic.CodeDescription?.Href);
        Assert.Contains("missing", diagnostic.Message.String);
        Assert.Equal(new Range(2, 8, 2, 15), diagnostic.Range);
    }

    [Fact]
    public void A_compile_s_diagnostics_are_converted_once()
    {
        // The info view and code actions read them on every move of the cursor.
        var uri = DocumentUri.FromFileSystemPath(TempPath("converted-once.nyx"));
        const string text = "import nanyx/console\n\nprintln(missing)\n";
        var document = new OpenDocument(text, "", NyxCompiler.Compiler.compile(text), null);

        var first = document.PublishedDiagnostics(uri);

        Assert.Equal("NYX0101", Assert.Single(first).Code?.String);
        Assert.Same(first, document.PublishedDiagnostics(uri));
    }

    [Fact]
    public async Task Linter_diagnostics_are_published_at_their_configured_severity()
    {
        // A test linter that reports every top-level def, at a warning by default.
        var everyDef = new Rule("everyDef", "Every def", Severity.Warning, "");
        var linter = new Linter(
            "test",
            ListModule.OfSeq([everyDef]),
            FuncConvert.FromFunc<SemanticModel, FSharpList<Report>>(model => ListModule.OfSeq(
                model.Syntax
                    .Where(item => item.Kind.IsDef)
                    .Select(item => new Report(
                        "everyDef", item.Span, "a def",
                        FSharpList<Tuple<Parser.Program.SourceSpan, string>>.Empty, FSharpList<Fix>.Empty)))));
        var uri = DocumentUri.FromFileSystemPath(TempPath("analyzed.nyx"));

        async Task<Diagnostic> PublishedWith(FSharpMap<string, FSharpOption<Severity>> severities)
        {
            await RestartWithAsync(ServerSettings.Default with
            {
                Linters = new LinterOptions(ListModule.OfSeq([linter]), severities)
            });
            Open(uri, "def one = 1\n");
            return Assert.Single((await NextPublished(uri)).Diagnostics!);
        }

        var byDefault = await PublishedWith(MapModule.Empty<string, FSharpOption<Severity>>());
        Assert.Equal(DiagnosticSeverity.Warning, byDefault.Severity);
        Assert.Equal("test/everyDef", byDefault.Code?.String);
        Assert.Equal("a def", byDefault.Message.String);
        Assert.Equal(new Range(0, 0, 0, 11), byDefault.Range);

        var asInfo = await PublishedWith(MapModule.OfSeq([Tuple.Create("test/everyDef", FSharpOption<Severity>.Some(Severity.Info))]));
        Assert.Equal(DiagnosticSeverity.Information, asInfo.Severity);
    }

    [Fact]
    public async Task Type_mismatch_is_published_over_the_whole_expression()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("mismatch.nyx"));

        // The `a` in the string is where searching the message's names used to put it.
        Open(uri, "def label = \"a\"\ndef a = 1\ndef b = a + \"x\"\n");

        var diagnostic = Assert.Single((await NextPublished(uri)).Diagnostics!);
        Assert.Equal("NYX0201", diagnostic.Code?.String);
        Assert.Equal(new Range(2, 8, 2, 15), diagnostic.Range);
    }

    [Fact]
    public async Task Related_locations_are_published_as_related_information()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("private.nyx"));

        Open(uri, "module inner =\n  def secret = 1\n  export def open = secret + 1\ndef value = inner.secret\n");

        var diagnostic = Assert.Single((await NextPublished(uri)).Diagnostics!);
        Assert.Equal(new Range(3, 12, 3, 24), diagnostic.Range);
        var related = Assert.Single(diagnostic.RelatedInformation!);
        Assert.Equal(uri, related.Location.Uri);
        Assert.Equal(new Range(1, 6, 1, 12), related.Location.Range);
    }

    [Fact]
    public async Task Diagnostic_in_an_imported_file_is_published_on_that_file()
    {
        var path = TempPath("mod.nyx");
        File.WriteAllText(path, "module mod\nexport def value = missing\n");
        var imported = DocumentUri.FromFileSystemPath(path);
        var uri = DocumentUri.FromFileSystemPath(TempPath("main.nyx"));

        Open(uri, "module main\nimport mod\ndef copy = value\n");

        var inImported = Assert.Single((await NextPublished(imported)).Diagnostics!);
        Assert.Equal("NYX0101", inImported.Code?.String);
        Assert.Equal(new Range(1, 19, 1, 26), inImported.Range);

        // The import that brings the file in says so, and links to the error.
        var atImport = Assert.Single((await NextPublished(uri)).Diagnostics!);
        Assert.Equal("'mod.nyx' has errors", atImport.Message.String);
        Assert.Equal(DiagnosticSeverity.Error, atImport.Severity);
        Assert.Equal(new Range(1, 7, 1, 10), atImport.Range);
        var related = Assert.Single(atImport.RelatedInformation!);
        Assert.Equal(imported, related.Location.Uri);
        Assert.Equal(new Range(1, 19, 1, 26), related.Location.Range);
    }

    [Fact]
    public async Task Diagnostic_in_a_file_imported_through_another_is_shown_at_the_import_that_brings_it_in()
    {
        File.WriteAllText(TempPath("mod.nyx"), "module mod\nimport sub\nexport def value = 1\n");
        var path = TempPath("sub.nyx");
        File.WriteAllText(path, "module sub\nexport def other = missing\n");
        var deeper = DocumentUri.FromFileSystemPath(path);
        var uri = DocumentUri.FromFileSystemPath(TempPath("main.nyx"));

        Open(uri, "module main\nimport mod\ndef copy = value\n");

        Assert.Equal(new Range(1, 19, 1, 26), Assert.Single((await NextPublished(deeper)).Diagnostics!).Range);
        var atImport = Assert.Single((await NextPublished(uri)).Diagnostics!);
        Assert.Equal("'sub.nyx' has errors", atImport.Message.String);
        Assert.Equal(new Range(1, 7, 1, 10), atImport.Range);
    }

    [Fact]
    public async Task Fixing_an_imported_file_clears_its_diagnostics_when_the_importer_is_compiled_again()
    {
        var path = TempPath("mod.nyx");
        File.WriteAllText(path, "module mod\nexport def value = missing\n");
        var imported = DocumentUri.FromFileSystemPath(path);
        var uri = DocumentUri.FromFileSystemPath(TempPath("main.nyx"));
        const string text = "module main\nimport mod\ndef copy = value\n";

        Open(uri, text);
        Assert.NotEmpty((await NextPublished(imported)).Diagnostics!);
        Assert.NotEmpty((await NextPublished(uri)).Diagnostics!);

        File.WriteAllText(path, "module mod\nexport def value = 1\n");
        Change(uri, version: 2, text + "def more = copy\n");

        Assert.Empty((await NextPublished(imported)).Diagnostics!);
        Assert.Empty((await NextPublished(uri)).Diagnostics!);
    }

    [Fact]
    public async Task An_open_imported_file_shows_only_its_own_compile_s_diagnostics()
    {
        var path = TempPath("mod.nyx");
        File.WriteAllText(path, "module mod\nexport def value = missing\n");
        var imported = DocumentUri.FromFileSystemPath(path);
        var uri = DocumentUri.FromFileSystemPath(TempPath("main.nyx"));
        var marker = DocumentUri.FromFileSystemPath(TempPath("marker.nyx"));

        Open(imported, "module mod\nexport def value = missing\n");
        Assert.Equal("NYX0101", Assert.Single((await NextPublished(imported)).Diagnostics!).Code?.String);
        Open(uri, "module main\nimport mod\ndef copy = value\n");
        Assert.NotEmpty((await NextPublished(uri)).Diagnostics!);
        _client.DidCloseTextDocument(new DidCloseTextDocumentParams { TextDocument = new TextDocumentIdentifier(uri) });
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        // Diagnostics arrive in the order they are sent, so once the marker's have, any for the open file would have.
        Open(marker, "def one = 1\n");
        await NextPublished(marker);
        lock (_published)
        {
            Assert.DoesNotContain(_published, published => published.Uri == imported);
        }
    }

    [Fact]
    public async Task Closing_an_imported_file_shows_what_its_importer_found_in_it()
    {
        var path = TempPath("mod.nyx");
        File.WriteAllText(path, "module mod\nexport def value = missing\n");
        var imported = DocumentUri.FromFileSystemPath(path);
        var uri = DocumentUri.FromFileSystemPath(TempPath("main.nyx"));

        Open(uri, "module main\nimport mod\ndef copy = value\n");
        Assert.NotEmpty((await NextPublished(imported)).Diagnostics!);
        Open(imported, "module mod\nexport def value = 1\n");
        Assert.Empty((await NextPublished(imported)).Diagnostics!);

        _client.DidCloseTextDocument(new DidCloseTextDocumentParams { TextDocument = new TextDocumentIdentifier(imported) });

        Assert.Equal(new Range(1, 19, 1, 26), Assert.Single((await NextPublished(imported)).Diagnostics!).Range);
    }

    [Fact]
    public async Task Diagnostics_are_cleared_when_the_change_fixes_the_error()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("fixed.nyx"));

        Open(uri, "import nanyx/console\n\nprintln(missing)\n");
        var withError = await NextPublished(uri);
        Assert.NotEmpty(withError.Diagnostics!);

        Change(uri, version: 2, "import nanyx/console\n\nprintln(\"found\")\n");
        var fixedResult = await NextPublished(uri);
        Assert.Empty(fixedResult.Diagnostics!);
        Assert.Equal(2, fixedResult.Version);
    }

    [Fact]
    public async Task Parse_error_yields_one_diagnostic_instead_of_nothing()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("parse-error.nyx"));

        Open(uri, "def = \n");

        var diagnostics = await NextPublished(uri);
        var diagnostic = Assert.Single(diagnostics.Diagnostics!);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.True(diagnostic.Range.Start.Line >= 0);
    }

    [Fact]
    public async Task Standard_library_imports_resolve_for_an_untitled_document()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-1");

        Open(uri, "import nanyx/text\n\ndef shout = string.uppercase(\"hi\")\n");

        var diagnostics = await NextPublished(uri);
        Assert.Empty(diagnostics.Diagnostics!);
    }

    [Theory]
    [InlineData("text.nyx")]
    [InlineData("prelude.nyx")]
    [InlineData("prelude-lists.nyx")]
    public async Task A_standard_library_file_is_checked_as_the_library_module_it_is(string fileName)
    {
        // Its calls to the compiler's intrinsics check, and the prelude's files do not clash with the prelude.
        var uri = DocumentUri.FromFileSystemPath(TempPath("nanyx", fileName));

        Open(uri, NyxCompiler.StandardLibrary.tryLoadFile("nanyx/" + fileName).Value);

        Assert.Empty((await NextPublished(uri)).Diagnostics!);
    }

    [Fact]
    public async Task An_error_in_a_file_of_a_module_split_across_files_is_published_on_that_file()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("nanyx", "prelude-lists.nyx"));
        var text = NyxCompiler.StandardLibrary.tryLoadFile("nanyx/prelude-lists.nyx").Value.ReplaceLineEndings("\n");
        var line = text.Split('\n').Length - 1;

        Open(uri, text + "def broken = missingName\n");

        var diagnostic = Assert.Single((await NextPublished(uri)).Diagnostics!);
        Assert.Equal("NYX0101", diagnostic.Code?.String);
        Assert.Equal(new Range(line, 13, line, 24), diagnostic.Range);
    }

    [Fact]
    public async Task Closing_a_document_clears_its_diagnostics()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("closed.nyx"));

        Open(uri, "def = \n");
        Assert.NotEmpty((await NextPublished(uri)).Diagnostics!);

        _client.DidCloseTextDocument(new DidCloseTextDocumentParams
        {
            TextDocument = new TextDocumentIdentifier(uri)
        });

        Assert.Empty((await NextPublished(uri)).Diagnostics!);
    }

    [Fact]
    public async Task A_script_is_linted_as_its_directives_say()
    {
        var uri = DocumentUri.FromFileSystemPath(TempPath("directives.nyx"));

        Open(uri, "::linters.default.unusedBindings = \"error\"\n::linters.nope\nexport def f: () -> int = {\n  def unused = 1\n  2\n}\n");

        var diagnostics = (await NextPublished(uri)).Diagnostics!.ToList();
        Assert.Equal(["NYX0009", "default/unusedBindings"], diagnostics.Select(d => d.Code?.String));
        // Only the compiler's own codes link to the error reference.
        Assert.NotNull(diagnostics[0].CodeDescription);
        Assert.Null(diagnostics[1].CodeDescription);
        Assert.Equal(new Range(1, 0, 1, 14), diagnostics[0].Range);
        Assert.Equal(DiagnosticSeverity.Error, diagnostics[1].Severity);
    }
}
