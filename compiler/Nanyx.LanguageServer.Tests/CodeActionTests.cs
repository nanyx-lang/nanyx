using System.IO.Pipelines;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using OmniSharp.Extensions.LanguageServer.Client;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
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
        const string text = "::linter nanyx.lint\nexport def f: () -> int = {\n  def unused = 1\n  2\n}\nexport def g: int -> int = { x -> 1 }\n";

        // The official linter runs only where something turns it on: here, a directive.
        Open(uri, text);
        var published = (await NextPublished(uri)).Diagnostics!;
        Assert.Equal(2, published.Count());

        // An unused local is removed, line and all.
        // The local has no type written, so writing it is offered too.
        var remove = Assert.Single(await CodeActionsIn(uri, new Range(2, 8, 2, 8)), action => action.Kind == CodeActionKind.QuickFix);
        Assert.Equal(("Remove `unused`", CodeActionKind.QuickFix, true), (remove.Title, remove.Kind, remove.IsPreferred));
        Assert.Equal("default/unusedBindings", Assert.Single(remove.Diagnostics!).Code?.String);
        Assert.Equal("::linter nanyx.lint\nexport def f: () -> int = {\n  2\n}\nexport def g: int -> int = { x -> 1 }\n", Applied(text, Assert.Single(remove.Edit!.Changes![uri])));

        // An unused parameter is renamed to `_`.
        var rename = Assert.Single(await CodeActionsIn(uri, new Range(5, 29, 5, 30)), action => action.Kind == CodeActionKind.QuickFix);
        Assert.Equal("Rename `x` to `_`", rename.Title);
        Assert.Equal("::linter nanyx.lint\nexport def f: () -> int = {\n  def unused = 1\n  2\n}\nexport def g: int -> int = { _ -> 1 }\n", Applied(text, Assert.Single(rename.Edit!.Changes![uri])));

        // Nothing where no diagnostic is.
        Assert.DoesNotContain(await CodeActionsIn(uri, new Range(3, 2, 3, 3)), action => action.Kind == CodeActionKind.QuickFix);
    }

    [Fact]
    public async Task A_fix_after_an_import_whose_file_has_errors_is_its_own_diagnostics()
    {
        // The import's errors are published as one diagnostic at the import, so the published diagnostics are fewer
        // than the compiler's, and the fix after it is still its own diagnostic's (#3048).
        File.WriteAllText(TempPath("mod.nyx"), "module mod\nexport def value = missing + other\n");
        var uri = DocumentUri.FromFileSystemPath(TempPath("main.nyx"));
        const string text = "module main\nimport mod\ndef s = max(1, 2)\n";

        Open(uri, text);
        var published = (await NextPublished(uri)).Diagnostics!;
        Assert.Equal(["'mod.nyx' has errors", "NYX0102"], published.Select(d => d.Code?.String ?? d.Message.String));

        var import = Assert.Single(await CodeActionsIn(uri, new Range(2, 8, 2, 8)), action => action.Kind == CodeActionKind.QuickFix);
        Assert.Equal("Add `import nanyx/math`", import.Title);
        var diagnostic = Assert.Single(import.Diagnostics!);
        Assert.Equal(("NYX0102", new Range(2, 8, 2, 11)), (diagnostic.Code?.String, diagnostic.Range));
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
    public async Task The_refactor_menu_lists_an_unknown_names_import_as_a_refactoring()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-import-refactoring");
        const string text = "def s = max(1, 2)\n";

        Open(uri, text);
        Assert.Single((await NextPublished(uri)).Diagnostics!);

        // The quick fix that makes the same edit is not listed: the request asks for refactorings only.
        var import = Assert.Single(await CodeActionsIn(uri, new Range(0, 10, 0, 10), [CodeActionKind.Refactor]));
        Assert.Equal(("Add `import nanyx/math`", CodeActionKind.RefactorRewrite), (import.Title, import.Kind));
        Assert.Equal(
            "import nanyx/math\n\ndef s = max(1, 2)\n",
            Applied(text, Assert.Single(import.Edit!.Changes![uri])));
    }

    [Fact]
    public async Task A_look_alike_character_has_a_quick_fix_that_writes_what_it_looks_like()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-look-alike");
        const string text = "def expected = −7.0\n";

        Open(uri, text);
        Assert.Equal("NYX0037", Assert.Single((await NextPublished(uri)).Diagnostics!).Code?.String);

        var replace = Assert.Single(await CodeActionsIn(uri, new Range(0, 15, 0, 15)));
        Assert.Equal(("Replace '−' with '-'", CodeActionKind.QuickFix), (replace.Title, replace.Kind));
        Assert.Equal("def expected = -7.0\n", Applied(text, Assert.Single(replace.Edit!.Changes![uri])));
    }

    [Fact]
    public async Task A_record_of_a_protected_types_shape_is_made_with_its_one_maker()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-protected-shape");
        const string text = """
            module Points =
              export type Point = protected (x: int, y: int)

              export def Point.at: int, int -> Points.Point = { x, y -> Point(x = x, y = y) }

            import Points

            def p: Point = (x = 3, y = 4)

            """;

        Open(uri, text);
        Assert.Equal("NYX0260", Assert.Single((await NextPublished(uri)).Diagnostics!).Code?.String);

        var make = Assert.Single(await CodeActionsIn(uri, new Range(7, 16, 7, 16)), action => action.Kind == CodeActionKind.QuickFix);
        Assert.Equal(("Make it with `Point.at`", CodeActionKind.QuickFix), (make.Title, make.Kind));
        Assert.EndsWith("def p: Point = Point.at(3, 4)\n", Applied(text, Assert.Single(make.Edit!.Changes![uri])));
    }

    [Fact]
    public async Task A_record_of_a_protected_types_shape_is_not_made_with_a_maker_whose_result_does_not_fit()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-protected-shape-option");
        const string text = """
            module Points =
              export type Point = protected (x: int, y: int)

              export def Point.new: int, int -> #none | #some(Points.Point) = { x, y -> #some(Point(x = x, y = y)) }

            import Points

            def p: Point = (x = 3, y = 4)

            """;

        Open(uri, text);
        Assert.Equal("NYX0260", Assert.Single((await NextPublished(uri)).Diagnostics!).Code?.String);
        Assert.DoesNotContain(await CodeActionsIn(uri, new Range(7, 16, 7, 16)), action => action.Kind == CodeActionKind.QuickFix);
    }

    [Fact]
    public async Task An_unused_import_has_a_quick_fix_that_removes_it()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-13");
        const string text = "::linter nanyx.lint\nimport (\n  nanyx/math as m\n  nanyx/text as t\n)\nexport def x: int = m.abs(3)\n";

        Open(uri, text);
        var published = Assert.Single((await NextPublished(uri)).Diagnostics!);
        Assert.Equal(("default/unusedImports", new Range(3, 2, 3, 17)), (published.Code?.String, published.Range));

        var remove = Assert.Single(await CodeActionsIn(uri, new Range(3, 4, 3, 4)), action => action.Title.StartsWith("Remove"));
        Assert.Equal(("Remove the import of `t`", CodeActionKind.QuickFix), (remove.Title, remove.Kind));
        Assert.Equal(
            "::linter nanyx.lint\nimport (\n  nanyx/math as m\n)\nexport def x: int = m.abs(3)\n",
            Applied(text, Assert.Single(remove.Edit!.Changes![uri])));

        // Organising imports fixes it too.
        var organise = Assert.Single(await CodeActionsIn(uri, new Range(3, 4, 3, 4)), action => action.Title == "Organise imports" && action.Kind == CodeActionKind.QuickFix);
        Assert.Equal("default/unusedImports", Assert.Single(organise.Diagnostics!).Code?.String);
        Assert.Equal(
            "::linter nanyx.lint\nimport nanyx/math as m\nexport def x: int = m.abs(3)\n",
            Applied(text, Assert.Single(organise.Edit!.Changes![uri])));

        // The import that is used has no fix, and organising the imports is a source action.
        Assert.Equal(
            [("Organise imports", CodeActionKind.SourceOrganizeImports)],
            (await CodeActionsIn(uri, new Range(2, 4, 2, 4))).Select(action => (action.Title, action.Kind)));
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
    public async Task A_match_offers_to_convert_to_an_if()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-match-to-if");
        const string text = """
            def pick = { (ok: bool) ->
              match ok
                | true -> 1
                | false -> 2
            }

            """;

        Open(uri, text.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var convert = Assert.Single(await CodeActionsIn(uri, new Range(1, 3, 1, 3)), action => action.Title == "Convert to if");
        Assert.Equal(CodeActionKind.RefactorRewrite, convert.Kind);
        Assert.Equal("def pick = { (ok: bool) ->\n  if ok then 1 else 2\n}\n", Applied(text.ReplaceLineEndings("\n"), Assert.Single(convert.Edit!.Changes![uri])));
    }

    [Fact]
    public async Task A_concatenation_offers_to_convert_to_an_interpolated_string()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-interpolate");
        const string text = "def greet = { (name: string) -> \"Hello, \" ++ name }\n";

        Open(uri, text);
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var convert = Assert.Single(await CodeActionsIn(uri, new Range(0, 43, 0, 43)), action => action.Title == "Convert to interpolated string");
        Assert.Equal(CodeActionKind.RefactorRewrite, convert.Kind);
        Assert.Equal("def greet = { (name: string) -> \"Hello, {name}\" }\n", Applied(text, Assert.Single(convert.Edit!.Changes![uri])));
    }

    [Fact]
    public async Task A_type_s_name_offers_to_implement_a_context()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-implement");
        const string text = "type Point = protected (x: int, y: int)\n";

        Open(uri, text);
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var implement = Assert.Single(await CodeActionsIn(uri, new Range(0, 6, 0, 6)), action => action.Title == "Implement `Ord`");
        Assert.Equal(CodeActionKind.RefactorRewrite, implement.Kind);
        Assert.Equal(
            "type Point = protected (x: int, y: int)\n\ndef Point.compare: Point, Point -> #less | #equal | #greater = { a, b -> ??? }\n",
            Applied(text, Assert.Single(implement.Edit!.Changes![uri])));
    }

    [Fact]
    public async Task A_structural_type_s_name_offers_to_make_it_protected()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-make-protected");
        const string text = "type Email = string\n";

        Open(uri, text);
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var actions = await CodeActionsIn(uri, new Range(0, 6, 0, 6));
        Assert.Contains(actions, action => action.Title == "Make `Email` private");
        var make = Assert.Single(actions, action => action.Title == "Make `Email` protected");
        Assert.Equal(CodeActionKind.RefactorRewrite, make.Kind);
        Assert.Equal(
            "type Email = protected string\n\ndef Email.of: string -> Email = { value ->\n  Email(value)\n}\n",
            Applied(text, Assert.Single(make.Edit!.Changes![uri])));
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

    [Fact]
    public async Task A_request_compiles_nothing_and_a_refactoring_is_compiled_when_it_is_resolved()
    {
        var compiles = 0;
        // As VS Code does, the client resolves a code action's edits when it is picked.
        await RestartWithClientAsync(
            TestSettings with { CompileStarted = _ => Interlocked.Increment(ref compiles) },
            options => options.WithCapability(new CodeActionCapability
            {
                CodeActionLiteralSupport = new CodeActionLiteralSupportOptions
                {
                    CodeActionKind = new CodeActionKindCapabilityOptions { ValueSet = new Container<CodeActionKind>(CodeActionKind.QuickFix, CodeActionKind.RefactorExtract, CodeActionKind.RefactorInline, CodeActionKind.RefactorRewrite, CodeActionKind.SourceOrganizeImports) }
                },
                DataSupport = true,
                ResolveSupport = new CodeActionCapabilityResolveSupportOptions { Properties = new Container<string>("edit") }
            }));
        var uri = DocumentUri.Parse("untitled:Untitled-deferred");
        const string text = """
            def tidy = { (s: string) -> string.trim(s) }

            def area = { (s: int) ->
              def side = s * 2 + 1
              side * side
            }

            """;

        Open(uri, text.ReplaceLineEndings("\n"));
        Assert.Empty((await NextPublished(uri)).Diagnostics!);
        Assert.Equal(1, Volatile.Read(ref compiles));

        // On a call's name, on a def's name, and with a selection: each offered, none compiled.
        var toPipe = Assert.Single(await CodeActionsIn(uri, new Range(0, 36, 0, 36)), action => action.Title == "Convert to pipe");
        var inline = Assert.Single(await CodeActionsIn(uri, new Range(3, 7, 3, 7)), action => action.Title == "Inline `side`");
        var extract = Assert.Single(await CodeActionsIn(uri, new Range(3, 13, 3, 18)), action => action.Title == "Introduce function `extracted`");
        Assert.All(new[] { toPipe, inline, extract }, action => Assert.Null(action.Edit));
        Assert.Equal(1, Volatile.Read(ref compiles));

        // Picked, it is compiled, and has its edits.
        var resolved = await _client.TextDocument.ResolveCodeAction(toPipe, _cancellation.Token);
        Assert.True(Volatile.Read(ref compiles) > 1);
        Assert.StartsWith("def tidy = { (s: string) -> s \\trim }\n", Applied(text.ReplaceLineEndings("\n"), Assert.Single(resolved.Edit!.Changes![uri])));
    }
}