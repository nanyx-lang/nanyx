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
        // The local has no type written, so writing it is offered too.
        var remove = Assert.Single(await CodeActionsIn(uri, new Range(2, 8, 2, 8)), action => action.Kind == CodeActionKind.QuickFix);
        Assert.Equal(("Remove `unused`", CodeActionKind.QuickFix, true), (remove.Title, remove.Kind, remove.IsPreferred));
        Assert.Equal("default/unusedBindings", Assert.Single(remove.Diagnostics!).Code?.String);
        Assert.Equal("::linters.default\nexport def f: () -> int = {\n  2\n}\nexport def g: int -> int = { x -> 1 }\n", Applied(text, Assert.Single(remove.Edit!.Changes![uri])));

        // An unused parameter is renamed to `_`.
        var rename = Assert.Single(await CodeActionsIn(uri, new Range(5, 29, 5, 30)), action => action.Kind == CodeActionKind.QuickFix);
        Assert.Equal("Rename `x` to `_`", rename.Title);
        Assert.Equal("::linters.default\nexport def f: () -> int = {\n  def unused = 1\n  2\n}\nexport def g: int -> int = { _ -> 1 }\n", Applied(text, Assert.Single(rename.Edit!.Changes![uri])));

        // Nothing where no diagnostic is.
        Assert.DoesNotContain(await CodeActionsIn(uri, new Range(3, 2, 3, 3)), action => action.Kind == CodeActionKind.QuickFix);
    }

    [Fact]
    public async Task An_unknown_name_the_standard_library_exports_offers_its_import()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-12");
        const string text = "def s = max(1, 2)\n";

        Open(uri, text);
        Assert.Single((await NextPublished(uri)).Diagnostics!);

        var import = Assert.Single(await CodeActionsIn(uri, new Range(0, 10, 0, 10)));
        Assert.Equal(("Add `import nanyx/math`", CodeActionKind.QuickFix, true), (import.Title, import.Kind, import.IsPreferred));
        Assert.Equal("NYX0102", Assert.Single(import.Diagnostics!).Code?.String);
        Assert.Equal(
            "import nanyx/math\n\ndef s = max(1, 2)\n",
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

    [Fact]
    public void Server_advertises_rewrites()
    {
        Assert.Contains(
            CodeActionKind.RefactorRewrite,
            _client.ServerSettings.Capabilities.CodeActionProvider?.Value?.CodeActionKinds ?? []);
    }

    [Fact]
    public async Task A_def_without_a_type_offers_to_write_the_inferred_one()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-write-type");
        const string text = "def add = { x, y -> x + y }\n";

        Open(uri, text);
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var write = Assert.Single(await CodeActionsIn(uri, new Range(0, 5, 0, 5)), action => action.Kind == CodeActionKind.RefactorRewrite);
        Assert.Equal("Write the inferred type of `add`", write.Title);
        Assert.Equal("def add: [Add(a)] a, a -> a = { x, y -> x + y }\n", Applied(text, Assert.Single(write.Edit!.Changes![uri])));
    }

    [Fact]
    public async Task A_pattern_variable_offers_writing_its_type_disabled_with_the_reason()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-write-type-refused");
        const string text = "def first = { o -> match o\n  | #some(n) -> n\n  | #none -> 0 }\n";

        Open(uri, text);
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var write = Assert.Single(await CodeActionsIn(uri, new Range(1, 10, 1, 10)), action => action.Kind == CodeActionKind.RefactorRewrite);
        Assert.Equal("A pattern variable's type can't be written", write.Disabled?.Reason);
        Assert.Null(write.Edit);
    }

    [Fact]
    public async Task A_selected_expression_offers_to_introduce_a_local()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-introduce-local");
        const string text = "def area = { (s: int) ->\n  def side = s * 2 + 1\n  side * side\n}\n";

        Open(uri, text);
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var extract = Assert.Single(await CodeActionsIn(uri, new Range(1, 13, 1, 18)), action => action.Title.StartsWith("Introduce local `"));
        Assert.Equal("Introduce local `result`", extract.Title);
        Assert.Equal(
            "def area = { (s: int) ->\n  def result = s * 2\n  def side = result + 1\n  side * side\n}\n",
            Applied(text, Assert.Single(extract.Edit!.Changes![uri])));

        // Then the new name is renamed.
        Assert.Equal("nanyx.renameAt", extract.Command?.Name);
        Assert.Equal([uri.ToString(), "1", "6"], extract.Command!.Arguments!.Select(argument => argument.ToString()));
    }

    [Fact]
    public async Task A_def_s_name_offers_to_inline_it()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-inline");
        const string text = "def area = { (s: int) ->\n  def side = s * 2\n  side + 1\n}\n";

        Open(uri, text);
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var inline = Assert.Single(await CodeActionsIn(uri, new Range(1, 7, 1, 7)), action => action.Kind == CodeActionKind.RefactorInline);
        Assert.Equal("Inline `side`", inline.Title);
        Assert.Equal("def area = { (s: int) ->\n  s * 2 + 1\n}\n", Applied(text, Assert.Single(inline.Edit!.Changes![uri])));
    }

    [Fact]
    public async Task A_call_offers_to_convert_to_a_pipe()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-call-to-pipe");
        const string text = "def tidy = { (s: string) -> string.trim(s) }\n";

        Open(uri, text);
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var convert = Assert.Single(await CodeActionsIn(uri, new Range(0, 36, 0, 36)), action => action.Title == "Convert to pipe");
        Assert.Equal(CodeActionKind.RefactorRewrite, convert.Kind);
        Assert.Equal("def tidy = { (s: string) -> s \\trim }\n", Applied(text, Assert.Single(convert.Edit!.Changes![uri])));
    }

    [Fact]
    public async Task A_selected_expression_offers_to_introduce_a_function()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-introduce-function");
        const string text = "def area = { (s: int) ->\n  def side = s * 2 + 1\n  side * side\n}\n";

        Open(uri, text);
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var actions = await CodeActionsIn(uri, new Range(1, 13, 1, 18));
        var extract = Assert.Single(actions, action => action.Title == "Introduce function `extracted`");
        Assert.Equal(CodeActionKind.RefactorExtract, extract.Kind);
        Assert.Equal(
            "def area = { (s: int) ->\n  def side = extracted(s) + 1\n  side * side\n}\n\ndef extracted: int -> int = { s ->\n  s * 2\n}\n",
            Applied(text, Assert.Single(extract.Edit!.Changes![uri])));
        Assert.Equal([uri.ToString(), "5", "4"], extract.Command!.Arguments!.Select(argument => argument.ToString()));
        Assert.Contains(actions, action => action.Title == "Introduce local function `extracted`");
    }
}