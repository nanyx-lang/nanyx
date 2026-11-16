using Nanyx.LanguageServer.Handlers;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Nanyx.LanguageServer.Tests;

/// <summary>
/// <c>nanyx/libraryModules</c> and <c>nanyx/libraryModule</c>: the standard library's modules and what each exports; and
/// <c>nanyx/addImport</c>, which imports one.
/// </summary>
public sealed class LibraryRequestTests : LanguageServerTestBase
{
    private async Task<IReadOnlyList<LibraryModuleItem>> Modules() =>
        await _client
            .SendRequest(LibraryModulesHandler.Method, new LibraryModulesParams())
            .Returning<IReadOnlyList<LibraryModuleItem>>(_cancellation.Token);

    private async Task<IReadOnlyList<LibraryExportItem>?> Exports(string module) =>
        await _client
            .SendRequest(LibraryModuleHandler.Method, new LibraryModuleParams { Name = module })
            .Returning<IReadOnlyList<LibraryExportItem>?>(_cancellation.Token);

    [Fact]
    public async Task Every_module_is_listed_the_prelude_first_with_its_doc_comment()
    {
        var modules = await Modules();

        Assert.Equal("nanyx/prelude", modules[0].Name);
        var text = Assert.Single(modules, module => module.Name == "nanyx/text");
        Assert.False(string.IsNullOrWhiteSpace(text.Documentation));
        Assert.Equal(modules.Count, modules.Select(module => module.Name).Distinct().Count());
    }

    [Fact]
    public async Task An_attached_definition_has_its_type_its_signature_its_doc_its_import_and_its_place()
    {
        var exports = await Exports("nanyx/text");

        var trim = Assert.Single(exports!, export => export.Name == "string.trim");
        Assert.Equal("attached", trim.Kind);
        Assert.Equal("string", trim.AttachedTo);
        Assert.Equal("def string.trim: string -> string", trim.Signature);
        Assert.False(string.IsNullOrWhiteSpace(trim.Documentation));
        Assert.Equal("import nanyx/text", trim.Import);
        Assert.NotNull(trim.Location);
        Assert.EndsWith("text.nyx", trim.Location.Uri.Path);
        Assert.Equal(189, trim.Location.Range.Start.Line);
    }

    [Fact]
    public async Task The_prelude_s_exports_need_no_import_and_leave_out_reserved_names()
    {
        var exports = await Exports("nanyx/prelude");

        var eq = Assert.Single(exports!, export => export.Name == "Eq");
        Assert.Equal("context", eq.Kind);
        Assert.Null(eq.Import);
        Assert.Contains(exports!, export => export.Kind == "function");
        Assert.DoesNotContain(exports!, export => export.Name.StartsWith("__", StringComparison.Ordinal));
    }

    private async Task<IReadOnlyList<TextEdit>?> AddImport(DocumentUri uri, string module) =>
        await _client
            .SendRequest(AddImportHandler.Method, new AddImportParams { TextDocument = new TextDocumentIdentifier(uri), Module = module })
            .Returning<IReadOnlyList<TextEdit>?>(_cancellation.Token);

    [Fact]
    public async Task Adding_an_import_puts_it_after_the_last_import_and_not_twice()
    {
        var uri = DocumentUri.From("untitled:imports.nyx");
        Open(uri, "import nanyx/math\n\nprintln(\"hi\")\n");
        await NextPublished(uri);

        var edit = Assert.Single((await AddImport(uri, "nanyx/text"))!);
        Assert.Equal(new Range(1, 0, 1, 0), edit.Range);
        Assert.Equal("import nanyx/text\n", edit.NewText);
        Assert.Empty((await AddImport(uri, "nanyx/math"))!);
    }

    [Fact]
    public async Task Adding_an_import_to_a_document_that_does_not_parse_gives_nothing()
    {
        var uri = DocumentUri.From("untitled:broken.nyx");
        Open(uri, "def = \n");
        await NextPublished(uri);

        Assert.Null(await AddImport(uri, "nanyx/text"));
    }

    [Fact]
    public async Task A_name_that_is_no_module_finds_nothing()
    {
        Assert.Null(await Exports("nanyx/missing"));
        Assert.Null(await Exports("text"));
    }

    private async Task<LibrarySearchResult> Search(string query) =>
        await _client
            .SendRequest(LibrarySearchHandler.Method, new LibrarySearchParams { Query = query })
            .Returning<LibrarySearchResult>(_cancellation.Token);

    [Fact]
    public async Task A_search_by_type_finds_the_export_with_its_import_and_its_place()
    {
        var result = await Search("string -> list(string)");

        Assert.Null(result.Error);
        var lines = Assert.Single(result.Matches, found => found.Name == "string.lines");
        Assert.Equal("import nanyx/text", lines.Import);
        Assert.Empty(lines.Needs);
        Assert.NotNull(lines.Location);
        Assert.EndsWith("text.nyx", lines.Location.Uri.Path);
    }

    [Fact]
    public async Task A_search_lists_the_contexts_a_match_still_needs()
    {
        var result = await Search("a -> string");

        var toString = Assert.Single(result.Matches, found => found.Name == "ToString(a).toString");
        Assert.Equal(["ToString(a)"], toString.Needs);
    }

    [Fact]
    public async Task A_query_that_is_not_a_type_gives_its_error_and_no_matches()
    {
        var result = await Search("bool ->");

        Assert.Empty(result.Matches);
        Assert.StartsWith("`bool ->` is not a type", result.Error);
    }
}
