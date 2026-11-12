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

/// <summary>Code actions: the fixes of the diagnostics in a range.</summary>
[Collection(LanguageServerTestBase.Collection)]
public sealed class CodeActionTests : LanguageServerTestBase
{
    [Fact]
    public void Server_advertises_quick_fixes()
    {
        Assert.Contains(
            CodeActionKind.QuickFix,
            _client.ServerSettings.Capabilities.CodeActionProvider?.Value?.CodeActionKinds ?? []);
    }

    [Fact]
    public async Task Code_actions_are_the_fixes_of_the_diagnostics_in_the_range()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-8");
        const string text = "::linters.default\nexport def f: () -> int = {\n  def unused = 1\n  2\n}\nexport def g: int -> int = { x -> 1 }\n";

        // The official linter runs only where something turns it on: here, a directive.
        Open(uri, text);
        var published = (await NextPublished(uri)).Diagnostics!;
        Assert.Equal(2, published.Count());

        // An unused local is removed, line and all.
        var remove = Assert.Single(await CodeActionsIn(uri, new Range(2, 8, 2, 8)));
        Assert.Equal(("Remove `unused`", CodeActionKind.QuickFix, true), (remove.Title, remove.Kind, remove.IsPreferred));
        Assert.Equal("default/unusedBindings", Assert.Single(remove.Diagnostics!).Code?.String);
        Assert.Equal("::linters.default\nexport def f: () -> int = {\n  2\n}\nexport def g: int -> int = { x -> 1 }\n", Applied(text, Assert.Single(remove.Edit!.Changes![uri])));

        // An unused parameter is renamed to `_`.
        var rename = Assert.Single(await CodeActionsIn(uri, new Range(5, 29, 5, 30)));
        Assert.Equal("Rename `x` to `_`", rename.Title);
        Assert.Equal("::linters.default\nexport def f: () -> int = {\n  def unused = 1\n  2\n}\nexport def g: int -> int = { _ -> 1 }\n", Applied(text, Assert.Single(rename.Edit!.Changes![uri])));

        // Nothing where no diagnostic is.
        Assert.Empty(await CodeActionsIn(uri, new Range(3, 2, 3, 3)));
    }

    [Fact]
    public async Task An_unknown_name_the_standard_library_exports_offers_its_import()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-12");
        const string text = "import nanyx/console\n\ndef s = string.trim(\" a \")\n";

        Open(uri, text);
        Assert.Single((await NextPublished(uri)).Diagnostics!);

        var import = Assert.Single(await CodeActionsIn(uri, new Range(2, 10, 2, 10)));
        Assert.Equal(("Add `import nanyx/text`", CodeActionKind.QuickFix, true), (import.Title, import.Kind, import.IsPreferred));
        Assert.Equal("NYX0102", Assert.Single(import.Diagnostics!).Code?.String);
        Assert.Equal(
            "import nanyx/console\nimport nanyx/text\n\ndef s = string.trim(\" a \")\n",
            Applied(text, Assert.Single(import.Edit!.Changes![uri])));
    }

    [Fact]
    public async Task An_unused_import_has_a_quick_fix_that_removes_it()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-13");
        const string text = "::linters.default\nimport (\n  nanyx/math as m\n  nanyx/text as t\n)\nexport def x: int = m.abs(3)\n";

        Open(uri, text);
        var published = Assert.Single((await NextPublished(uri)).Diagnostics!);
        Assert.Equal(("default/unusedImports", new Range(3, 2, 3, 17)), (published.Code?.String, published.Range));

        var remove = Assert.Single(await CodeActionsIn(uri, new Range(3, 4, 3, 4)));
        Assert.Equal(("Remove the import of `t`", CodeActionKind.QuickFix), (remove.Title, remove.Kind));
        Assert.Equal(
            "::linters.default\nimport (\n  nanyx/math as m\n)\nexport def x: int = m.abs(3)\n",
            Applied(text, Assert.Single(remove.Edit!.Changes![uri])));

        // The import that is used has none.
        Assert.Empty(await CodeActionsIn(uri, new Range(2, 4, 2, 4)));
    }

    /// <summary><paramref name="text"/> with <paramref name="edit"/> made to it.</summary>
    private static string Applied(string text, TextEdit edit)
    {
        var positions = new SourcePositions(text);
        var start = positions.ToOffset(edit.Range.Start);
        var end = positions.ToOffset(edit.Range.End);
        return text[..start] + edit.NewText + text[end..];
    }
}
