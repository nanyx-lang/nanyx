using Nanyx.LanguageServer.Handlers;
using Xunit;

namespace Nanyx.LanguageServer.Tests;

/// <summary><c>nanyx/libraryModules</c> and <c>nanyx/libraryModule</c>: the standard library's modules and what each exports.</summary>
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
        Assert.Equal(193, trim.Location.Range.Start.Line);
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

    [Fact]
    public async Task A_name_that_is_no_module_finds_nothing()
    {
        Assert.Null(await Exports("nanyx/missing"));
        Assert.Null(await Exports("text"));
    }
}
