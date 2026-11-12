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

/// <summary>
/// Drives the server in-process through a real LSP client over a pair of pipes, so what is tested is the
/// same wire protocol an editor speaks.
/// </summary>
public sealed class LanguageServerTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly CancellationTokenSource _cancellation = new(Timeout);
    private readonly List<PublishDiagnosticsParams> _published = [];
    private readonly SemaphoreSlim _publishedSignal = new(0);

    private Task _serverTask = Task.CompletedTask;
    private LanguageClient _client = null!;

    public Task InitializeAsync() => StartAsync(ServerSettings.Default);

    /// <summary>Replaces the server started for the test with one started with other settings.</summary>
    private async Task RestartWithAsync(ServerSettings settings)
    {
        await DisposeAsync();
        await StartAsync(settings);
    }

    private async Task StartAsync(ServerSettings settings)
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        _serverTask = NanyxLanguageServer.RunAsync(
            clientToServer.Reader.AsStream(),
            serverToClient.Writer.AsStream(),
            settings,
            _cancellation.Token);

        _client = LanguageClient.PreInit(options => options
            .WithInput(serverToClient.Reader)
            .WithOutput(clientToServer.Writer)
            .OnPublishDiagnostics(diagnostics =>
            {
                lock (_published)
                {
                    _published.Add(diagnostics);
                }

                _publishedSignal.Release();
            }));

        await _client.Initialize(_cancellation.Token);

        // Starting a server is slow on a busy CI runner, so the test's own requests get the whole timeout after it.
        _cancellation.CancelAfter(Timeout);
    }

    public async Task DisposeAsync()
    {
        // Shutdown() sends the shutdown request followed by the exit notification.
        await _client.Shutdown();
        await _serverTask;
        _client.Dispose();
    }

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
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "type-error.nyx"));

        Open(uri, "import nanyx/console\n\nprintln(missing)\n");

        var diagnostics = await NextPublished(uri);
        var diagnostic = Assert.Single(diagnostics.Diagnostics!);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("nanyx", diagnostic.Source);
        Assert.Equal("NYX0101", diagnostic.Code?.String);
        Assert.Contains("missing", diagnostic.Message.String);
        Assert.Equal(new Range(2, 8, 2, 15), diagnostic.Range);
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
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "analyzed.nyx"));

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
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "mismatch.nyx"));

        // The `a` in the string is where searching the message's names used to put it.
        Open(uri, "def label = \"a\"\ndef a = 1\ndef b = a + \"x\"\n");

        var diagnostic = Assert.Single((await NextPublished(uri)).Diagnostics!);
        Assert.Equal("NYX0201", diagnostic.Code?.String);
        Assert.Equal(new Range(2, 8, 2, 15), diagnostic.Range);
    }

    [Fact]
    public async Task Related_locations_are_published_as_related_information()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "private.nyx"));

        Open(uri, "module inner =\n  def secret = 1\n  export def open = secret + 1\ndef value = inner.secret\n");

        var diagnostic = Assert.Single((await NextPublished(uri)).Diagnostics!);
        Assert.Equal(new Range(3, 12, 3, 24), diagnostic.Range);
        var related = Assert.Single(diagnostic.RelatedInformation!);
        Assert.Equal(uri, related.Location.Uri);
        Assert.Equal(new Range(1, 6, 1, 12), related.Location.Range);
    }

    [Fact]
    public async Task Diagnostic_in_an_imported_file_links_to_its_place_there()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var imported = Path.Combine(directory, "mod.nyx");
            File.WriteAllText(imported, "module mod\nexport def value = missing\n");
            var uri = DocumentUri.FromFileSystemPath(Path.Combine(directory, "main.nyx"));

            Open(uri, "module main\nimport mod\ndef copy = value\n");

            var diagnostic = Assert.Single((await NextPublished(uri)).Diagnostics!);
            Assert.Equal("NYX0101", diagnostic.Code?.String);
            Assert.Contains("mod.nyx", diagnostic.Message.String);
            Assert.Equal(new Range(0, 0, 0, 0), diagnostic.Range);
            var related = Assert.Single(diagnostic.RelatedInformation!);
            Assert.Equal(DocumentUri.FromFileSystemPath(imported), related.Location.Uri);
            Assert.Equal(new Range(1, 19, 1, 26), related.Location.Range);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Diagnostics_are_cleared_when_the_change_fixes_the_error()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "fixed.nyx"));

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
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "parse-error.nyx"));

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

    [Fact]
    public async Task Closing_a_document_clears_its_diagnostics()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "closed.nyx"));

        Open(uri, "def = \n");
        Assert.NotEmpty((await NextPublished(uri)).Diagnostics!);

        _client.DidCloseTextDocument(new DidCloseTextDocumentParams
        {
            TextDocument = new TextDocumentIdentifier(uri)
        });

        Assert.Empty((await NextPublished(uri)).Diagnostics!);
    }

    [Fact]
    public void Server_advertises_hover()
    {
        Assert.NotNull(_client.ServerSettings.Capabilities.HoverProvider);
    }

    [Fact]
    public async Task Hover_shows_the_inferred_type_of_the_expression_under_the_pointer()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "hover.nyx"));

        // CRLF line endings: positions are still counted per line, as the editor counts them.
        Open(uri, "def id = { x -> x }\r\ndef total = id(40) + 2\r\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        // The literal `40`, innermost inside the call and the addition.
        var literal = await HoverAt(uri, 1, 16);
        Assert.NotNull(literal);
        Assert.Equal("```nanyx\nint\n```", literal.Contents.MarkupContent?.Value);
        Assert.Equal(MarkupKind.Markdown, literal.Contents.MarkupContent?.Kind);
        Assert.Equal(new Range(1, 15, 1, 17), literal.Range);

        // The polymorphic `id`, instantiated at this call.
        var callee = await HoverAt(uri, 1, 13);
        Assert.Equal("```nanyx\ndef id: int -> int\n```", callee?.Contents.MarkupContent?.Value);
        Assert.Equal(new Range(1, 12, 1, 14), callee?.Range);

        // The lambda in its own definition, with its type variable named for reading.
        var lambda = await HoverAt(uri, 0, 9);
        Assert.Equal("```nanyx\nα -> α\n```", lambda?.Contents.MarkupContent?.Value);
    }

    [Fact]
    public async Task Hover_on_a_declarations_name_shows_its_name_and_type()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "hover-declaration.nyx"));

        Open(uri, "def id = { x -> x }\ndef total = id(40) + 2\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var id = await HoverAt(uri, 0, 5);
        Assert.Equal("```nanyx\ndef id: α -> α\n```", id?.Contents.MarkupContent?.Value);
        Assert.Equal(new Range(0, 4, 0, 6), id?.Range);

        var total = await HoverAt(uri, 1, 6);
        Assert.Equal("```nanyx\ndef total: int\n```", total?.Contents.MarkupContent?.Value);

        var parameter = await HoverAt(uri, 0, 11);
        Assert.Equal("```nanyx\nx: α\n```", parameter?.Contents.MarkupContent?.Value);
        Assert.Equal(new Range(0, 11, 0, 12), parameter?.Range);
    }

    [Fact]
    public async Task Hover_shows_what_a_hole_in_a_type_was_inferred_to_be()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "hover-hole.nyx"));

        Open(uri, "def xs: list(_) = [1, 2]\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var name = await HoverAt(uri, 0, 5);
        Assert.Equal("```nanyx\ndef xs: list(int)\n```", name?.Contents.MarkupContent?.Value);

        var hole = await HoverAt(uri, 0, 13);
        Assert.Equal("```nanyx\nint\n```", hole?.Contents.MarkupContent?.Value);
        Assert.Equal(new Range(0, 13, 0, 14), hole?.Range);
    }

    [Fact]
    public async Task Hover_and_completion_show_a_names_doc_comment_with_its_links()
    {
        var path = Path.Combine(Path.GetTempPath(), "hover-doc.nyx");
        var uri = DocumentUri.FromFileSystemPath(path);

        Open(uri, """
            --| One more than `n`.
              See [`dec`], and [`nowhere`].
            def inc = { n -> n + 1 }
            def dec = { n -> n - 1 }
            def two = inc(1)
            """);
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var expected = $"""
            One more than `n`.
            See [`dec`]({uri}#L4,5), and `nowhere`.
            """.ReplaceLineEndings("\n");
        var hover = await HoverAt(uri, 4, 11);
        Assert.Equal($"```nanyx\ndef inc: int -> int\n```\n\n---\n\n{expected}", hover?.Contents.MarkupContent?.Value);

        var item = (await CompletionsAt(uri, 4, 10)).Single(completion => completion.Label == "inc");
        Assert.Equal(expected, item.Documentation?.MarkupContent?.Value);
        Assert.Null((await CompletionsAt(uri, 4, 10)).Single(completion => completion.Label == "dec").Documentation);

        // A link goes to the declaration of the name it links to.
        var definition = await SingleDefinitionAt(uri, 1, 9);
        Assert.Equal(new Range(3, 4, 3, 7), definition?.Range);
    }

    [Fact]
    public async Task Hover_on_a_defs_name_shows_its_declaration_with_its_contexts_then_its_doc_comment()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "hover-def.nyx"));

        Open(uri, """
            --| Takes one order.
            def takeOrder: [Console] string, list(string) -> () = { customer, order -> println(customer) }
            @entry
            def main = {
              takeOrder("pat", [])
            }
            """);
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var expected = "```nanyx\ndef takeOrder: [Console] string, list(string) -> ()\n```\n\n---\n\nTakes one order.";
        // At its definition, and where it is called.
        Assert.Equal(expected, (await HoverAt(uri, 1, 6))?.Contents.MarkupContent?.Value);
        Assert.Equal(expected, (await HoverAt(uri, 4, 4))?.Contents.MarkupContent?.Value);
        // A parameter shows its name and type.
        Assert.Equal("```nanyx\ncustomer: string\n```", (await HoverAt(uri, 1, 58))?.Contents.MarkupContent?.Value);
    }
    [Fact]
    public async Task Hover_outside_any_expression_returns_nothing()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "hover-nothing.nyx"));

        Open(uri, "def answer = 42\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        Assert.Null(await HoverAt(uri, 0, 1));
    }

    [Fact]
    public async Task Hover_in_a_document_with_type_errors_reads_its_own_types()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "hover-error.nyx"));

        Open(uri, "def answer = missing + 1\n");
        Assert.NotEmpty((await NextPublished(uri)).Diagnostics!);

        // What did check has its type; what did not has the type `???`.
        Assert.Equal("```nanyx\nint\n```", (await HoverAt(uri, 0, 23))?.Contents.MarkupContent?.Value);
        Assert.Equal("```nanyx\n???\n```", (await HoverAt(uri, 0, 15))?.Contents.MarkupContent?.Value);
    }

    [Fact]
    public async Task Hover_in_a_document_that_has_never_parsed_returns_nothing()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "hover-unparsed.nyx"));

        Open(uri, "def answer = (1 +\n");
        Assert.NotEmpty((await NextPublished(uri)).Diagnostics!);

        Assert.Null(await HoverAt(uri, 0, 14));
    }

    [Fact]
    public void Server_advertises_definition()
    {
        Assert.NotNull(_client.ServerSettings.Capabilities.DefinitionProvider);
    }

    [Fact]
    public async Task Definition_of_locals_parameters_and_later_top_level_values_is_in_the_document()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "definition.nyx"));

        Open(uri, "def scale = { factor ->\r\n  def offset = 1\r\n  factor * 2 + offset + later\r\n}\r\ndef later = 3\r\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        // A parameter.
        Assert.Equal(new Location { Uri = uri, Range = new Range(0, 14, 0, 20) }, await SingleDefinitionAt(uri, 2, 3));
        // A def in the block.
        Assert.Equal(new Location { Uri = uri, Range = new Range(1, 6, 1, 12) }, await SingleDefinitionAt(uri, 2, 16));
        // A top-level def written after its use.
        Assert.Equal(new Location { Uri = uri, Range = new Range(4, 4, 4, 9) }, await SingleDefinitionAt(uri, 2, 26));
    }

    [Fact]
    public async Task Definition_of_a_pattern_variable_is_where_the_pattern_binds_it()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "definition-pattern.nyx"));

        Open(uri, "def rest = { items -> match items\n  | [_, ...tail] -> tail\n  | _ -> items }\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        Assert.Equal(new Location { Uri = uri, Range = new Range(1, 11, 1, 15) }, await SingleDefinitionAt(uri, 1, 21));
    }

    [Fact]
    public async Task Definition_on_a_declaration_is_the_declaration_itself()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "definition-self.nyx"));

        Open(uri, "def scale = { factor -> factor * 2 }\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        Assert.Equal(new Location { Uri = uri, Range = new Range(0, 4, 0, 9) }, await SingleDefinitionAt(uri, 0, 6));
        Assert.Equal(new Location { Uri = uri, Range = new Range(0, 14, 0, 20) }, await SingleDefinitionAt(uri, 0, 14));
    }

    [Fact]
    public async Task Definition_of_what_is_not_a_name_returns_nothing()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "definition-nothing.nyx"));

        Open(uri, "def id = { x -> x }\ndef total = id(40) + 2\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        // The literal `40`, inside a call whose function name is a reference.
        Assert.Empty(await DefinitionsAt(uri, 1, 16));
        // The `+`.
        Assert.Empty(await DefinitionsAt(uri, 1, 19));
    }

    [Fact]
    public async Task Definition_of_a_type_name_is_the_type_definition()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-3");

        Open(uri, "type Point = (x: int, y: int)\ndef origin: Point = (x = 0, y = 0)\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        Assert.Equal(new Location { Uri = uri, Range = new Range(0, 5, 0, 10) }, await SingleDefinitionAt(uri, 1, 14));
    }

    [Fact]
    public async Task Definition_of_a_context_member_is_its_declaration_in_the_context()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-4");

        Open(uri, "context Logger = (log: string -> ())\ndef greet: [Logger] string -> () = { name -> log(name) }\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        Assert.Equal(new Location { Uri = uri, Range = new Range(0, 18, 0, 21) }, await SingleDefinitionAt(uri, 1, 46));
    }

    [Fact]
    public async Task Hover_on_a_type_name_shows_the_type_definition_even_inside_a_block()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-9");

        Open(uri, "type Point = (x: int, y: int)\ndef local = {\n  def p: Point = (x = 1, y = 2)\n  p.x\n}\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var hover = await _client.TextDocument.RequestHover(
            new HoverParams { TextDocument = new TextDocumentIdentifier(uri), Position = new Position(2, 10) },
            _cancellation.Token);

        Assert.NotNull(hover);
        Assert.Equal("```nanyx\ntype Point = (x: int, y: int)\n```", hover.Contents.MarkupContent?.Value);
        Assert.Equal(new Range(2, 9, 2, 14), hover.Range);
    }

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
        const string text = "::linters.default\nexport def f = {\n  def unused = 1\n  2\n}\nexport def g = { x -> 1 }\n";

        // The official linter runs only where something turns it on: here, a directive.
        Open(uri, text);
        var published = (await NextPublished(uri)).Diagnostics!;
        Assert.Equal(2, published.Count());

        // An unused local is removed, line and all.
        var remove = Assert.Single(await CodeActionsIn(uri, new Range(2, 8, 2, 8)));
        Assert.Equal(("Remove `unused`", CodeActionKind.QuickFix, true), (remove.Title, remove.Kind, remove.IsPreferred));
        Assert.Equal("default/unusedBindings", Assert.Single(remove.Diagnostics!).Code?.String);
        Assert.Equal("::linters.default\nexport def f = {\n  2\n}\nexport def g = { x -> 1 }\n", Applied(text, Assert.Single(remove.Edit!.Changes![uri])));

        // An unused parameter is renamed to `_`.
        var rename = Assert.Single(await CodeActionsIn(uri, new Range(5, 17, 5, 18)));
        Assert.Equal("Rename `x` to `_`", rename.Title);
        Assert.Equal("::linters.default\nexport def f = {\n  def unused = 1\n  2\n}\nexport def g = { _ -> 1 }\n", Applied(text, Assert.Single(rename.Edit!.Changes![uri])));

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
        const string text = "::linters.default\nimport (\n  nanyx/math as m\n  nanyx/text as t\n)\nexport def x = m.abs(3)\n";

        Open(uri, text);
        var published = Assert.Single((await NextPublished(uri)).Diagnostics!);
        Assert.Equal(("default/unusedImports", new Range(3, 2, 3, 17)), (published.Code?.String, published.Range));

        var remove = Assert.Single(await CodeActionsIn(uri, new Range(3, 4, 3, 4)));
        Assert.Equal(("Remove the import of `t`", CodeActionKind.QuickFix), (remove.Title, remove.Kind));
        Assert.Equal(
            "::linters.default\nimport (\n  nanyx/math as m\n)\nexport def x = m.abs(3)\n",
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

    [Fact]
    public void Server_advertises_document_symbols()
    {
        Assert.NotNull(_client.ServerSettings.Capabilities.DocumentSymbolProvider);
    }

    [Fact]
    public async Task Document_symbols_are_the_declarations_with_those_inside_them()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-6");

        Open(uri, "type Point = (x: int, y: int)\ndef area = { s ->\n  def side = s\n  side * side\n}\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var symbols = await DocumentSymbols(uri);
        Assert.Equal(["Point", "area"], symbols.Select(symbol => symbol.Name));

        var point = symbols[0];
        Assert.Equal(SymbolKind.Struct, point.Kind);
        Assert.Equal(new Range(0, 0, 0, 29), point.Range);
        Assert.Equal(new Range(0, 5, 0, 10), point.SelectionRange);
        Assert.Equal(["x", "y"], point.Children!.Select(child => child.Name));
        Assert.All(point.Children!, child => Assert.Equal(SymbolKind.Field, child.Kind));

        var area = symbols[1];
        Assert.Equal(SymbolKind.Function, area.Kind);
        Assert.Equal(new Range(1, 0, 4, 1), area.Range);
        var side = Assert.Single(area.Children!);
        Assert.Equal(("side", SymbolKind.Variable, new Range(2, 6, 2, 10)), (side.Name, side.Kind, side.SelectionRange));
    }

    [Fact]
    public async Task Document_symbols_of_text_that_does_not_parse_are_the_last_good_ones_not_edited_since()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-7");

        Open(uri, "def one = 1\ndef two = 2\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        // The second def no longer parses, and is left out; the first, not edited, keeps its symbol.
        Change(uri, 2, "def one = 1\ndef two = (\n");
        Assert.NotEmpty((await NextPublished(uri)).Diagnostics!);

        var symbol = Assert.Single(await DocumentSymbols(uri));
        Assert.Equal(("one", new Range(0, 4, 0, 7)), (symbol.Name, symbol.SelectionRange));
    }

    [Fact]
    public void Server_advertises_references()
    {
        Assert.NotNull(_client.ServerSettings.Capabilities.ReferencesProvider);
    }

    [Fact]
    public async Task References_are_the_names_that_refer_to_a_declaration_and_the_declaration_when_asked()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-5");

        Open(uri, "def twice = { n -> n * 2 }\ndef four = twice(2)\ndef eight = twice(four)\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var calls = new[] { new Range(1, 11, 1, 16), new Range(2, 12, 2, 17) };
        // From a use, without the declaration.
        Assert.Equal(calls, (await ReferencesAt(uri, 2, 13, includeDeclaration: false)).Select(location => location.Range));
        // From the declaration, with it, which comes first.
        Assert.Equal(
            calls.Prepend(new Range(0, 4, 0, 9)),
            (await ReferencesAt(uri, 0, 5, includeDeclaration: true)).Select(location => location.Range));
        Assert.All(await ReferencesAt(uri, 0, 5, includeDeclaration: true), location => Assert.Equal(uri, location.Uri));
        // A parameter.
        Assert.Equal(
            [new Range(0, 19, 0, 20)],
            (await ReferencesAt(uri, 0, 14, includeDeclaration: false)).Select(location => location.Range));
    }

    [Fact]
    public async Task Definition_of_a_qualified_import_is_in_the_imported_file()
    {
        var directory = Directory.CreateTempSubdirectory("nanyx-definition-");
        try
        {
            var modulePath = Path.Combine(directory.FullName, "mod.nyx");
            File.WriteAllText(modulePath, "module mod\nexport def foo = 1\n");
            var uri = DocumentUri.FromFileSystemPath(Path.Combine(directory.FullName, "main.nyx"));

            Open(uri, "module main\nimport mod as m\ndef value = m.foo\n");
            Assert.Empty((await NextPublished(uri)).Diagnostics!);

            Assert.Equal(
                new Location { Uri = DocumentUri.FromFileSystemPath(modulePath), Range = new Range(1, 11, 1, 14) },
                await SingleDefinitionAt(uri, 2, 15));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Definition_in_the_standard_library_is_in_a_read_only_copy_of_its_source()
    {
        var root = Directory.CreateTempSubdirectory("nanyx-stdlib-");
        try
        {
            await RestartWithAsync(ServerSettings.Default with
            {
                StandardLibrarySource = new StandardLibrarySource(root.FullName)
            });
            var uri = DocumentUri.Parse("untitled:Untitled-2");

            Open(uri, "import nanyx/text as t\ndef shout = t.string.uppercase(\"hi\")\ndef n = int.fromString(\"1\")\n");
            Assert.Empty((await NextPublished(uri)).Diagnostics!);

            // A qualified import of nanyx/text, and a definition the prelude brings into scope.
            AssertDefinitionInStandardLibrary(await SingleDefinitionAt(uri, 1, 22), "text", "uppercase");
            AssertDefinitionInStandardLibrary(await SingleDefinitionAt(uri, 2, 13), "prelude", "fromString");
        }
        finally
        {
            foreach (var file in root.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                file.Attributes = FileAttributes.Normal;
            }

            root.Delete(recursive: true);
        }

        void AssertDefinitionInStandardLibrary(Location? location, string moduleName, string name)
        {
            Assert.NotNull(location);
            var path = Path.Combine(root.FullName, "nanyx", moduleName + ".nyx");
            Assert.Equal(DocumentUri.FromFileSystemPath(path), location.Uri);
            Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));

            var source = File.ReadAllText(path);
            Assert.Equal(NyxCompiler.StandardLibrary.tryLoadSource(moduleName).Value, source);

            var line = source.Replace("\r\n", "\n").Split('\n')[location.Range.Start.Line];
            Assert.EndsWith(name, line[location.Range.Start.Character..location.Range.End.Character]);
        }
    }

    [Fact]
    public void Server_advertises_code_lenses_and_inlay_hints()
    {
        Assert.NotNull(_client.ServerSettings.Capabilities.CodeLensProvider);
        Assert.NotNull(_client.ServerSettings.Capabilities.InlayHintProvider);
    }

    [Fact]
    public async Task Code_lenses_show_the_types_of_unannotated_top_level_defs()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "lenses.nyx"));

        Open(uri, "def id = { x -> x }\r\ndef answer: int = 42\r\ndef (p, q) = (1, \"s\")\r\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var lenses = (await _client.TextDocument.RequestCodeLens(
            new CodeLensParams { TextDocument = new TextDocumentIdentifier(uri) },
            _cancellation.Token))?.ToList() ?? [];

        // The annotated `answer` has none; the two names of one destructuring def each say which they are for.
        Assert.Equal(
            [
                (new Range(0, 4, 0, 6), "α -> α"),
                (new Range(2, 5, 2, 6), "p: int"),
                (new Range(2, 8, 2, 9), "q: string")
            ],
            lenses.Select(lens => (lens.Range, lens.Command?.Title)));
    }

    [Fact]
    public async Task Inlay_hints_show_the_types_of_unannotated_locals_and_parameters()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "inlay-hints.nyx"));

        Open(uri, "def scale = { factor ->\r\n  def offset = 1\r\n  factor * 2 + offset\r\n}\r\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var everywhere = await InlayHintsIn(uri, new Range(0, 0, 4, 0));
        Assert.Equal(
            [(new Position(0, 20), ": int"), (new Position(1, 12), ": int")],
            everywhere.Select(hint => (hint.Position, hint.Label.String)));
        Assert.All(everywhere, hint => Assert.Equal(InlayHintKind.Type, hint.Kind));

        // Only the hints in the range asked for.
        var secondLine = await InlayHintsIn(uri, new Range(1, 0, 1, 16));
        Assert.Equal([new Position(1, 12)], secondLine.Select(hint => hint.Position));
    }

    [Fact]
    public async Task Top_level_inlay_hints_show_top_level_defs_types_as_inlay_hints_instead_of_lenses()
    {
        await RestartWithAsync(ServerSettings.Default with { TopLevelTypeHints = TopLevelTypeHints.Inlay });
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "top-level-hints.nyx"));

        Open(uri, "def id = { x -> x }\r\ndef answer: int = 42\r\ndef (p, q) = (1, \"s\")\r\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var lenses = await _client.TextDocument.RequestCodeLens(
            new CodeLensParams { TextDocument = new TextDocumentIdentifier(uri) },
            _cancellation.Token);
        Assert.Empty(lenses ?? []);

        // Each name of the destructuring def has its own hint, so none needs to say which name it is for.
        Assert.Equal(
            [
                (new Position(0, 6), ": α -> α"),
                (new Position(0, 12), ": α"),
                (new Position(2, 6), ": int"),
                (new Position(2, 9), ": string")
            ],
            (await InlayHintsIn(uri, new Range(0, 0, 3, 0))).Select(hint => (hint.Position, hint.Label.String)));
    }

    [Fact]
    public async Task Top_level_inlay_hint_of_a_def_that_writes_only_its_contexts_follows_them()
    {
        await RestartWithAsync(ServerSettings.Default with { TopLevelTypeHints = TopLevelTypeHints.Inlay });
        var uri = DocumentUri.Parse("untitled:Untitled-10");

        Open(uri, "context Log = (log: string -> ())\ndef greet: [Log] = { name -> log(name) }\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        // `def greet: [Log] string -> () = { name: string -> ...`, the type completing the annotation.
        Assert.Equal(
            [
                (new Position(1, 16), "string -> ()", true),
                (new Position(1, 25), ": string", false)
            ],
            (await InlayHintsIn(uri, new Range(1, 0, 2, 0))).Select(hint => (hint.Position, hint.Label.String, hint.PaddingLeft)));
    }

    [Fact]
    public async Task Top_level_type_hints_off_shows_no_lenses_and_only_the_locals_inlay_hints()
    {
        await RestartWithAsync(ServerSettings.Default with { TopLevelTypeHints = TopLevelTypeHints.Off });
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "top-level-hints-off.nyx"));

        Open(uri, "def id = { x -> x }\r\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var lenses = await _client.TextDocument.RequestCodeLens(
            new CodeLensParams { TextDocument = new TextDocumentIdentifier(uri) },
            _cancellation.Token);
        Assert.Empty(lenses ?? []);
        Assert.Equal(
            [(new Position(0, 12), ": α")],
            (await InlayHintsIn(uri, new Range(0, 0, 1, 0))).Select(hint => (hint.Position, hint.Label.String)));
    }

    [Theory]
    [InlineData(false, true, true, new[] { "scale", "factor", "rest", "items", "tail" })]
    [InlineData(true, false, true, new[] { "scale", "offset", "rest", "tail" })]
    [InlineData(true, true, false, new[] { "scale", "factor", "offset", "rest", "items" })]
    // The top-level defs' hints are there whatever the other settings say.
    [InlineData(false, false, false, new[] { "scale", "rest" })]
    public async Task Inlay_hints_for_each_kind_of_declaration_can_be_turned_off(
        bool locals, bool parameters, bool patternVariables, string[] shown)
    {
        await RestartWithAsync(ServerSettings.Default with
        {
            TopLevelTypeHints = TopLevelTypeHints.Inlay,
            LocalTypeHints = locals,
            ParameterTypeHints = parameters,
            PatternVariableTypeHints = patternVariables
        });
        var uri = DocumentUri.Parse("untitled:Untitled-11");
        var source =
            "def scale = { factor ->\n  def offset = 1\n  factor * 2 + offset\n}\n" +
            "def rest = { items -> match items\n  | [_, ...tail] -> tail\n  | _ -> items }\n";
        var lines = source.Split('\n');

        Open(uri, source);
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        Assert.Equal(
            shown.Select(PositionAfter),
            (await InlayHintsIn(uri, new Range(0, 0, lines.Length, 0))).Select(hint => hint.Position));

        Position PositionAfter(string name)
        {
            var line = Array.FindIndex(lines, text => text.Contains(name));
            return new Position(line, lines[line].IndexOf(name, StringComparison.Ordinal) + name.Length);
        }
    }

    [Fact]
    public async Task Lenses_and_inlay_hints_in_a_document_with_type_errors_read_its_own_types()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "hints-error.nyx"));

        Open(uri, "def scale = { factor -> factor + missing }\ndef broken = { x -> nope }\n");
        Assert.NotEmpty((await NextPublished(uri)).Diagnostics!);

        // `scale` checks around the unknown name; `broken`'s result is what failed, `???`.
        Assert.Equal(
            [(new Range(0, 4, 0, 9), "int -> int"), (new Range(1, 4, 1, 10), "α -> ???")],
            (await CodeLenses(uri)).Select(lens => (lens.Range, lens.Command?.Title)));
        Assert.Equal(
            [(new Position(0, 20), ": int"), (new Position(1, 16), ": α")],
            (await InlayHintsIn(uri, new Range(0, 0, 2, 0))).Select(hint => (hint.Position, hint.Label.String)));
    }

    [Fact]
    public async Task Lenses_and_inlay_hints_in_a_document_that_has_never_parsed_are_empty()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "hints-unparsed.nyx"));

        Open(uri, "def scale = { factor -> factor + }\n");
        Assert.NotEmpty((await NextPublished(uri)).Diagnostics!);

        var lenses = await _client.TextDocument.RequestCodeLens(
            new CodeLensParams { TextDocument = new TextDocumentIdentifier(uri) },
            _cancellation.Token);
        Assert.Empty(lenses ?? []);
        Assert.Empty(await InlayHintsIn(uri, new Range(0, 0, 1, 0)));
    }

    [Fact]
    public async Task Lenses_and_inlay_hints_stay_on_unchanged_defs_while_the_file_does_not_type_check()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "hints-last-good.nyx"));

        Open(uri, "def id = { x -> x }\r\ndef scale = { factor ->\r\n  def offset = 1\r\n  factor * 2 + offset\r\n}\r\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        // A broken line inserted between the two defs: both keep their hints, the second moved down a line.
        Change(uri, version: 2,
            "def id = { x -> x }\r\ndef broken = missing +\r\ndef scale = { factor ->\r\n  def offset = 1\r\n  factor * 2 + offset\r\n}\r\n");
        Assert.NotEmpty((await NextPublished(uri)).Diagnostics!);

        Assert.Equal(
            [(new Range(0, 4, 0, 6), "α -> α"), (new Range(2, 4, 2, 9), "int -> int")],
            (await CodeLenses(uri)).Select(lens => (lens.Range, lens.Command?.Title)));
        Assert.Equal(
            [(new Position(0, 12), ": α"), (new Position(2, 20), ": int"), (new Position(3, 12), ": int")],
            (await InlayHintsIn(uri, new Range(0, 0, 6, 0))).Select(hint => (hint.Position, hint.Label.String)));
    }

    [Fact]
    public async Task Hover_reads_the_last_good_compile_except_in_a_def_edited_since()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "hover-last-good.nyx"));

        Open(uri, "def id = { x -> x }\ndef total = id(40) + 2\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        // A text that does not parse has no types of its own.
        Change(uri, version: 2, "def id = { x -> x }\ndef total = id(40) +\n");
        Assert.NotEmpty((await NextPublished(uri)).Diagnostics!);

        // The untouched def still has its types; the edited one has none, rather than ones that may be stale.
        var lambda = await HoverAt(uri, 0, 9);
        Assert.Equal("```nanyx\nα -> α\n```", lambda?.Contents.MarkupContent?.Value);
        Assert.Null(await HoverAt(uri, 1, 16));
        Assert.Equal([new Range(0, 4, 0, 6)], (await CodeLenses(uri)).Select(lens => lens.Range));
    }

    [Fact]
    public void Server_advertises_completion()
    {
        Assert.NotNull(_client.ServerSettings.Capabilities.CompletionProvider);
    }

    [Fact]
    public async Task Completion_offers_the_names_in_scope_with_their_types_nearest_first()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "completion.nyx"));

        Open(uri, "def scale = { factor ->\r\n  def offset = 1\r\n  factor * 2 + offset\r\n}\r\ndef later = 3\r\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        var items = await CompletionsAt(uri, 2, 2);
        var names = items.Where(item => item.Kind != CompletionItemKind.Keyword).OrderBy(item => item.SortText).ToList();
        Assert.Equal(
            [
                ("factor", CompletionItemKind.Variable, "int"),
                ("offset", CompletionItemKind.Variable, "int"),
                ("later", CompletionItemKind.Variable, "int"),
                ("scale", CompletionItemKind.Function, "int -> int")
            ],
            names.Where(item => item.SortText![0] != '2').Select(item => (item.Label, item.Kind, item.Detail)));
        // The prelude's names come after the file's own, and keywords last.
        Assert.Contains(names, item => item.Label == "proc" && item.SortText![0] == '2');
        Assert.Contains(items, item => item.Label == "match" && item.Kind == CompletionItemKind.Keyword);

        // Outside the lambda, its parameter and locals are not in scope.
        Assert.DoesNotContain(await CompletionsAt(uri, 4, 12), item => item.Label is "factor" or "offset");
    }

    [Fact]
    public async Task Completion_reads_the_text_with_type_errors_and_the_last_typed_one_while_it_does_not_parse()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "completion-last-good.nyx"));

        Open(uri, "def scale = { factor ->\n  def offset = 1\n  factor * 2 + offset\n}\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        // Typing a name that does not exist yet, inside the lambda: a type error, so the text is typed all the same.
        Change(uri, version: 2, "def scale = { factor ->\n  def offset = 1\n  factor * 2 + off\n}\n");
        Assert.NotEmpty((await NextPublished(uri)).Diagnostics!);
        var inLambda = await CompletionsAt(uri, 2, 18);
        Assert.Contains(inLambda, item => item.Label == "offset" && item.Detail == "int");
        Assert.Contains(inLambda, item => item.Label == "factor");

        // An unclosed bracket at the end of the file: a parse error, so the last typed text is read.
        Change(uri, version: 3, "def scale = { factor ->\n  def offset = 1\n  factor * 2 + off\n}\ndef later = (sc");
        Assert.NotEmpty((await NextPublished(uri)).Diagnostics!);
        var atEnd = await CompletionsAt(uri, 4, 15);
        Assert.Contains(atEnd, item => item.Label == "scale" && item.Detail == "int -> int");
        Assert.DoesNotContain(atEnd, item => item.Label == "factor");
    }

    [Fact]
    public async Task Completion_offers_nothing_in_a_comment_or_a_string_and_only_members_after_a_dot()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "completion-nothing.nyx"));

        Open(uri, "def a = 1 -- about a\ndef b = \"text {a} more\"\ndef p = (x = 1, y = 2)\ndef c = p.x\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        Assert.Empty(await CompletionsAt(uri, 0, 19));
        Assert.Empty(await CompletionsAt(uri, 1, 11));
        Assert.Contains(await CompletionsAt(uri, 1, 15), item => item.Label == "a");
        Assert.Equal(["x", "y"], (await CompletionsAt(uri, 3, 11)).Select(item => item.Label));
    }

    [Fact]
    public void Completion_is_triggered_by_a_dot_a_pipe_and_the_start_of_an_import_paths_next_part()
    {
        Assert.Equal([".", "\\", "/", "\""], _client.ServerSettings.Capabilities.CompletionProvider?.TriggerCharacters ?? []);
    }

    [Fact]
    public async Task Completion_after_a_pipe_just_typed_offers_what_is_attached_to_the_values_type_first()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-5");

        Open(uri, "import nanyx/text\ndef s = \"abc\"\ndef double = { n -> n * 2 }\ndef r = s\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        // The `\` was just typed, so the text does not parse.
        Change(uri, version: 2, "import nanyx/text\ndef s = \"abc\"\ndef double = { n -> n * 2 }\ndef r = s \\\n");
        Assert.NotEmpty((await NextPublished(uri)).Diagnostics!);

        var items = (await CompletionsAt(uri, 3, 11)).OrderBy(item => item.SortText).ToList();
        Assert.Contains(items, item => item.Label == "startsWith" && item.Kind == CompletionItemKind.Function && item.SortText![0] == '0');
        Assert.Contains(items, item => item.Label == "double" && item.Detail == "int -> int" && item.SortText![0] == '1');
        Assert.DoesNotContain(items, item => item.Kind == CompletionItemKind.Keyword);
        Assert.True(
            items.FindIndex(item => item.Label == "startsWith") < items.FindIndex(item => item.Label == "double"));
    }

    [Fact]
    public async Task Completion_after_a_dot_just_typed_offers_a_records_fields()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "completion-fields.nyx"));

        Open(uri, "def p = (x = 1, name = \"a\")\ndef q = p.x\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        // The dot was just typed, so the text does not parse.
        Change(uri, version: 2, "def p = (x = 1, name = \"a\")\ndef q = p.\n");
        Assert.NotEmpty((await NextPublished(uri)).Diagnostics!);

        Assert.Equal(
            [("name", CompletionItemKind.Field, "string"), ("x", CompletionItemKind.Field, "int")],
            (await CompletionsAt(uri, 1, 10)).Select(item => (item.Label, item.Kind, item.Detail)));
    }

    [Fact]
    public async Task Completion_after_a_dot_on_a_string_offers_its_length_and_nothing_attached()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-6");

        Open(uri, "import nanyx/text\ndef s = \"abc\"\ndef n = s.length\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        Assert.Equal(
            [("length", CompletionItemKind.Property, "int")],
            (await CompletionsAt(uri, 2, 10)).Select(item => (item.Label, item.Kind, item.Detail)));
    }

    [Fact]
    public async Task Completion_after_an_imports_alias_offers_its_exports()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-3");

        Open(uri, "import nanyx/text as t\ndef shout = t.string.uppercase(\"hi\")\n");
        Assert.Empty((await NextPublished(uri)).Diagnostics!);

        Assert.Contains(await CompletionsAt(uri, 1, 14), item => item.Label == "string" && item.Kind == CompletionItemKind.Module);
        Assert.Contains(await CompletionsAt(uri, 1, 21), item => item.Label == "uppercase" && item.Kind == CompletionItemKind.Function);
    }

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

    [Fact]
    public async Task A_script_is_linted_as_its_directives_say()
    {
        var uri = DocumentUri.FromFileSystemPath(Path.Combine(Path.GetTempPath(), "directives.nyx"));

        Open(uri, "::linters.default.unusedBindings = \"error\"\n::linters.nope\nexport def f = {\n  def unused = 1\n  2\n}\n");

        var diagnostics = (await NextPublished(uri)).Diagnostics!.ToList();
        Assert.Equal(["NYX0009", "default/unusedBindings"], diagnostics.Select(d => d.Code?.String));
        Assert.Equal(new Range(1, 0, 1, 14), diagnostics[0].Range);
        Assert.Equal(DiagnosticSeverity.Error, diagnostics[1].Severity);
    }

    private async Task<List<TextEdit>> FormattingEdits(DocumentUri uri) =>
        (await _client.TextDocument.RequestDocumentFormatting(
            new DocumentFormattingParams
            {
                TextDocument = new TextDocumentIdentifier(uri),
                Options = new FormattingOptions { TabSize = 4, InsertSpaces = false }
            },
            _cancellation.Token))?.ToList() ?? [];

    [Fact]
    public async Task Completion_in_an_import_offers_the_standard_librarys_modules()
    {
        var uri = DocumentUri.Parse("untitled:Untitled-4");

        Open(uri, "import \n");
        await NextPublished(uri);
        var package = Assert.Single(await CompletionsAt(uri, 0, 7));
        Assert.Equal(("nanyx", CompletionItemKind.Module, "nanyx/"), (package.Label, package.Kind, package.TextEdit?.TextEdit?.NewText));
        // The rest of the path is asked for at once.
        Assert.Equal("editor.action.triggerSuggest", package.Command?.Name);

        Change(uri, version: 2, "import nanyx/co\n");
        await NextPublished(uri);
        var modules = await CompletionsAt(uri, 0, 15);
        Assert.Equal(["console", "math", "result", "text"], modules.Select(item => item.Label));
        var console = modules[0];
        Assert.Equal(new Range(0, 13, 0, 15), console.TextEdit?.TextEdit?.Range);
        Assert.Equal("how a program talks to the terminal.", console.Documentation?.String);
    }

    [Fact]
    public async Task Completion_in_a_quoted_import_offers_the_files_and_folders_beside_the_document()
    {
        var directory = Directory.CreateTempSubdirectory("nanyx-completion-");
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "util.nyx"), "module util\n");
            File.WriteAllText(Path.Combine(directory.FullName, "notes.txt"), "");
            Directory.CreateDirectory(Path.Combine(directory.FullName, "shared"));
            File.WriteAllText(Path.Combine(directory.FullName, "shared", "text.nyx"), "module text\n");
            var uri = DocumentUri.FromFileSystemPath(Path.Combine(directory.FullName, "main.nyx"));

            Open(uri, "import \"\n");
            await NextPublished(uri);
            // Not the document itself, nor a file that is not Nanyx source.
            Assert.Equal(
                [("shared", CompletionItemKind.Folder, "shared/"), ("util.nyx", CompletionItemKind.File, "util.nyx")],
                (await CompletionsAt(uri, 0, 8)).Select(item => (item.Label, item.Kind, item.TextEdit?.TextEdit?.NewText)));

            Change(uri, version: 2, "import \"shared/t\n");
            await NextPublished(uri);
            var inFolder = Assert.Single(await CompletionsAt(uri, 0, 16));
            Assert.Equal(("text.nyx", new Range(0, 15, 0, 16)), (inFolder.Label, inFolder.TextEdit?.TextEdit?.Range));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private async Task<List<CompletionItem>> CompletionsAt(DocumentUri uri, int line, int character) =>
        (await _client.TextDocument.RequestCompletion(
            new CompletionParams { TextDocument = new TextDocumentIdentifier(uri), Position = new Position(line, character) },
            _cancellation.Token)).ToList();

    private async Task<List<CodeLens>> CodeLenses(DocumentUri uri) =>
        (await _client.TextDocument.RequestCodeLens(
            new CodeLensParams { TextDocument = new TextDocumentIdentifier(uri) },
            _cancellation.Token))?.ToList() ?? [];

    private async Task<List<InlayHint>> InlayHintsIn(DocumentUri uri, Range range) =>
        (await _client.TextDocument.RequestInlayHints(
            new InlayHintParams { TextDocument = new TextDocumentIdentifier(uri), Range = range },
            _cancellation.Token))?.ToList() ?? [];

    /// <summary>The definitions found at a position. The client reads the server's "none" (null) as no locations.</summary>
    private async Task<List<LocationOrLocationLink>> DefinitionsAt(DocumentUri uri, int line, int character) =>
        (await _client.TextDocument.RequestDefinition(
            new DefinitionParams { TextDocument = new TextDocumentIdentifier(uri), Position = new Position(line, character) },
            _cancellation.Token))?.ToList() ?? [];

    private async Task<List<CodeAction>> CodeActionsIn(DocumentUri uri, Range range) =>
        (await _client.TextDocument.RequestCodeAction(
            new CodeActionParams
            {
                TextDocument = new TextDocumentIdentifier(uri),
                Range = range,
                Context = new CodeActionContext { Diagnostics = new Container<Diagnostic>() }
            },
            _cancellation.Token))?
        .Select(item => item.CodeAction!)
        .ToList() ?? [];

    private async Task<List<DocumentSymbol>> DocumentSymbols(DocumentUri uri) =>
        (await _client.TextDocument.RequestDocumentSymbol(
            new DocumentSymbolParams { TextDocument = new TextDocumentIdentifier(uri) },
            _cancellation.Token))?
        .Select(symbol => symbol.DocumentSymbol!)
        .ToList() ?? [];

    private async Task<List<Location>> ReferencesAt(DocumentUri uri, int line, int character, bool includeDeclaration) =>
        (await _client.TextDocument.RequestReferences(
            new ReferenceParams
            {
                TextDocument = new TextDocumentIdentifier(uri),
                Position = new Position(line, character),
                Context = new ReferenceContext { IncludeDeclaration = includeDeclaration }
            },
            _cancellation.Token))?.ToList() ?? [];

    private async Task<Location?> SingleDefinitionAt(DocumentUri uri, int line, int character) =>
        Assert.Single(await DefinitionsAt(uri, line, character)).Location;

    private Task<Hover?> HoverAt(DocumentUri uri, int line, int character) =>
        _client.TextDocument.RequestHover(
            new HoverParams { TextDocument = new TextDocumentIdentifier(uri), Position = new Position(line, character) },
            _cancellation.Token);

    private void Open(DocumentUri uri, string text) =>
        _client.DidOpenTextDocument(new DidOpenTextDocumentParams
        {
            TextDocument = new TextDocumentItem
            {
                Uri = uri,
                LanguageId = "nanyx",
                Version = 1,
                Text = text
            }
        });

    private void Change(DocumentUri uri, int version, string text) =>
        _client.DidChangeTextDocument(new DidChangeTextDocumentParams
        {
            TextDocument = new OptionalVersionedTextDocumentIdentifier { Uri = uri, Version = version },
            ContentChanges = new Container<TextDocumentContentChangeEvent>(new TextDocumentContentChangeEvent { Text = text })
        });

    private async Task<PublishDiagnosticsParams> NextPublished(DocumentUri uri)
    {
        while (true)
        {
            await _publishedSignal.WaitAsync(_cancellation.Token);

            lock (_published)
            {
                var index = _published.FindIndex(p => p.Uri == uri);
                if (index >= 0)
                {
                    var found = _published[index];
                    _published.RemoveAt(index);
                    return found;
                }
            }
        }
    }
}
