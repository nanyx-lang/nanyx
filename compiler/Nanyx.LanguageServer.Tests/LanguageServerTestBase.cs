using System.IO.Pipelines;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using Nanyx.LanguageServer.Handlers;
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
/// same wire protocol an editor speaks. Each test class derives from it, and all of them are in one collection,
/// so their tests run one at a time, as they did when they were one class.
/// </summary>
public abstract class LanguageServerTestBase : IAsyncLifetime
{
    /// <summary>The collection every language server test class is in.</summary>
    public const string Collection = "Language server";

    protected static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    protected readonly CancellationTokenSource _cancellation = new(Timeout);
    protected readonly List<PublishDiagnosticsParams> _published = [];
    protected readonly SemaphoreSlim _publishedSignal = new(0);

    protected Task _serverTask = Task.CompletedTask;
    protected LanguageClient _client = null!;

    /// <summary>The test's own folder for its files, made by the first <see cref="TempPath"/>, deleted after the test.</summary>
    private string? _tempDirectory;

    public Task InitializeAsync() => StartAsync(ServerSettings.Default);

    /// <summary>
    /// A path in a folder of the test's own, so tests in overlapping runs never share a file. The folders the
    /// path names are made; the file is not.
    /// </summary>
    protected string TempPath(params string[] segments)
    {
        _tempDirectory ??= Path.Combine(Path.GetTempPath(), "nanyx-lsp-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine([_tempDirectory, .. segments]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    /// <summary>Replaces the server started for the test with one started with other settings.</summary>
    protected async Task RestartWithAsync(ServerSettings settings)
    {
        await StopAsync();
        await StartAsync(settings);
    }

    protected async Task StartAsync(ServerSettings settings)
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
        await StopAsync();
        if (_tempDirectory is not null && Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }

    private async Task StopAsync()
    {
        // Shutdown() sends the shutdown request followed by the exit notification.
        await _client.Shutdown();
        await _serverTask;
        _client.Dispose();
    }

    protected async Task<List<CompletionItem>> CompletionsAt(DocumentUri uri, int line, int character) =>
        (await _client.TextDocument.RequestCompletion(
            new CompletionParams { TextDocument = new TextDocumentIdentifier(uri), Position = new Position(line, character) },
            _cancellation.Token)).ToList();

    protected async Task<List<CodeLens>> CodeLenses(DocumentUri uri) =>
        (await _client.TextDocument.RequestCodeLens(
            new CodeLensParams { TextDocument = new TextDocumentIdentifier(uri) },
            _cancellation.Token))?.ToList() ?? [];

    protected async Task<List<InlayHint>> InlayHintsIn(DocumentUri uri, Range range) =>
        (await _client.TextDocument.RequestInlayHints(
            new InlayHintParams { TextDocument = new TextDocumentIdentifier(uri), Range = range },
            _cancellation.Token))?.ToList() ?? [];

    /// <summary>The definitions found at a position. The client reads the server's "none" (null) as no locations.</summary>
    protected async Task<List<LocationOrLocationLink>> DefinitionsAt(DocumentUri uri, int line, int character) =>
        (await _client.TextDocument.RequestDefinition(
            new DefinitionParams { TextDocument = new TextDocumentIdentifier(uri), Position = new Position(line, character) },
            _cancellation.Token))?.ToList() ?? [];

    protected async Task<List<CodeAction>> CodeActionsIn(DocumentUri uri, Range range) =>
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

    protected async Task<List<DocumentSymbol>> DocumentSymbols(DocumentUri uri) =>
        (await _client.TextDocument.RequestDocumentSymbol(
            new DocumentSymbolParams { TextDocument = new TextDocumentIdentifier(uri) },
            _cancellation.Token))?
        .Select(symbol => symbol.DocumentSymbol!)
        .ToList() ?? [];

    protected async Task<List<Location>> ReferencesAt(DocumentUri uri, int line, int character, bool includeDeclaration) =>
        (await _client.TextDocument.RequestReferences(
            new ReferenceParams
            {
                TextDocument = new TextDocumentIdentifier(uri),
                Position = new Position(line, character),
                Context = new ReferenceContext { IncludeDeclaration = includeDeclaration }
            },
            _cancellation.Token))?.ToList() ?? [];

    protected async Task<Location?> SingleDefinitionAt(DocumentUri uri, int line, int character) =>
        Assert.Single(await DefinitionsAt(uri, line, character)).Location;

    protected Task<Hover?> HoverAt(DocumentUri uri, int line, int character) =>
        _client.TextDocument.RequestHover(
            new HoverParams { TextDocument = new TextDocumentIdentifier(uri), Position = new Position(line, character) },
            _cancellation.Token);

    protected void Open(DocumentUri uri, string text) =>
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

    protected void Change(DocumentUri uri, int version, string text) =>
        _client.DidChangeTextDocument(new DidChangeTextDocumentParams
        {
            TextDocument = new OptionalVersionedTextDocumentIdentifier { Uri = uri, Version = version },
            ContentChanges = new Container<TextDocumentContentChangeEvent>(new TextDocumentContentChangeEvent { Text = text })
        });

    protected async Task<PublishDiagnosticsParams> NextPublished(DocumentUri uri)
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


    protected async Task<InfoViewResult> InfoViewAt(DocumentUri uri, int line, int character)
    {
        var result = await _client
            .SendRequest(InfoViewHandler.Method, new InfoViewParams
            {
                TextDocument = new TextDocumentIdentifier(uri),
                Position = new Position(line, character)
            })
            .Returning<InfoViewResult?>(_cancellation.Token);
        Assert.NotNull(result);
        Assert.Equal("cursor", result.Scopes[0].Kind);
        Assert.Equal(["expression", "messages", "call"], result.Scopes[0].Sections.Select(section => section.Id));
        Assert.Equal("module", result.Scopes[^1].Kind);
        return result;
    }

    /// <summary>The items of the section <paramref name="id"/> of the innermost scope of kind <paramref name="kind"/>, none if it leaves the section out.</summary>
    protected static IReadOnlyList<InfoViewItem> Section(InfoViewResult view, string kind, string id) =>
        view.Scopes.First(scope => scope.Kind == kind).Sections.SingleOrDefault(section => section.Id == id)?.Items ?? [];
}
