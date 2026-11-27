using System.IO.Pipelines;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using Nanyx.LanguageServer.Handlers;
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

/// <summary>
/// Drives the server in-process through a real LSP client over a pair of pipes, so what is tested is the
/// same wire protocol an editor speaks. Each test class derives from it. Every test starts a server of its own with
/// its own folder, so test classes run in parallel; only the ones that time the server are kept apart (see
/// <see cref="SerialCollection"/>).
/// </summary>
public abstract class LanguageServerTestBase : IAsyncLifetime
{
    /// <summary>
    /// The collection of the test classes that time the server, such as a compile's debounce. It runs on its own after
    /// the others, so the load of tests running in parallel can't stretch the pauses it measures.
    /// </summary>
    public const string SerialCollection = "Language server timing";

    protected static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gives the test's requests <see cref="Timeout"/> from now and <paramref name="work"/> besides: for a request that
    /// checks something slow, given what checking it outside the server takes, measured just before, so that load on
    /// the machine, which slows both alike, can't make the test time out.
    /// </summary>
    protected void AllowFor(TimeSpan work) => _cancellation.CancelAfter(Timeout + work);

    /// <summary>
    /// The standard library checked, as the first compile in the process does. On a slow CI runner that took most of a
    /// test's timeout, so whichever test compiled first timed out. The process keeps what it checked, so it is done
    /// once, before any test's timeout starts.
    /// </summary>
    private static readonly Lazy<Task> StandardLibraryChecked =
        new(() => Task.Run(() => NyxCompiler.Compiler.compile("def warmUp = 1")));

    /// <summary>Cancels the test's requests; its timeout starts when the server does (see <see cref="StartAsync"/>).</summary>
    protected readonly CancellationTokenSource _cancellation = new();
    protected readonly List<PublishDiagnosticsParams> _published = [];
    protected readonly SemaphoreSlim _publishedSignal = new(0);

    protected Task _serverTask = Task.CompletedTask;
    protected LanguageClient _client = null!;

    /// <summary>Whether the last <see cref="StartAsync"/> failed, so that stopping doesn't wait on a server that isn't there.</summary>
    private bool _startFailed;

    /// <summary>The test's own folder for its files, made by the first <see cref="TempPath"/>, deleted after the test.</summary>
    private string? _tempDirectory;

    /// <summary>
    /// The settings the tests start the server with: the defaults, without formatting hints, so that a test's diagnostics
    /// are the compiler's alone however its code is laid out. <c>FormattingHintTests</c> turns them on.
    /// </summary>
    protected static readonly ServerSettings TestSettings = ServerSettings.Default with { FormattingHints = false };

    public Task InitializeAsync() => StartAsync(TestSettings);

    /// <summary>
    /// A path in a folder of the test's own, so tests in overlapping runs never share a file. The folders the
    /// path names are made; the file is not.
    /// </summary>
    protected string TempPath(params string[] segments)
    {
        _tempDirectory ??= Path.Combine(Path.GetTempPath(), "nanyx-lsp-test-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>Replaces the server started for the test with one started with other settings, whose client also has what <paramref name="configureClient"/> gives it.</summary>
    protected async Task RestartWithClientAsync(ServerSettings settings, Action<LanguageClientOptions> configureClient)
    {
        await StopAsync();
        await StartAsync(settings, configureClient: configureClient);
    }

    /// <summary>Replaces the server started for the test with one whose client has opened <paramref name="folder"/> as its workspace.</summary>
    protected async Task RestartInFolderAsync(string folder)
    {
        await StopAsync();
        await StartAsync(TestSettings, folder);
    }

    /// <summary>Starts a server, with a client that also has what <paramref name="configureClient"/> gives it.</summary>
    protected async Task StartAsync(ServerSettings settings, string? workspaceFolder = null, Action<LanguageClientOptions>? configureClient = null)
    {
        var sinceStart = System.Diagnostics.Stopwatch.StartNew();
        await StandardLibraryChecked.Value;
        var sinceWarmUp = System.Diagnostics.Stopwatch.StartNew();
        _cancellation.CancelAfter(Timeout);

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        _serverTask = NanyxLanguageServer.RunAsync(
            clientToServer.Reader.AsStream(),
            serverToClient.Writer.AsStream(),
            settings,
            _cancellation.Token);

        _client = LanguageClient.PreInit(options =>
        {
            if (workspaceFolder is not null)
            {
                options.WithWorkspaceFolder(DocumentUri.FromFileSystemPath(workspaceFolder), "workspace");
            }

            // As VS Code does, the client can rename files in a workspace edit.
            options
                .WithCapability(new WorkspaceEditCapability
                {
                    DocumentChanges = true,
                    ResourceOperations = new Container<ResourceOperationKind>(ResourceOperationKind.Rename)
                })
                .WithInput(serverToClient.Reader)
                .WithOutput(clientToServer.Writer)
                .OnPublishDiagnostics(diagnostics =>
                {
                    lock (_published)
                    {
                        _published.Add(diagnostics);
                    }

                    _publishedSignal.Release();
                });
            configureClient?.Invoke(options);
        });

        // When the timeout fires and when the server's task ends, for a failed start to say which came first (#3004).
        TimeSpan? timedOut = null;
        TimeSpan? serverEnded = null;
        using var timing = _cancellation.Token.Register(() => timedOut = sinceWarmUp.Elapsed);
        var serverTask = _serverTask;
        _ = serverTask.ContinueWith(_ => serverEnded = sinceWarmUp.Elapsed, TaskScheduler.Default);
        try
        {
            await _client.Initialize(_cancellation.Token);
        }
        catch (Exception ex)
        {
            _startFailed = true;
            throw new InvalidOperationException(
                WhyNotStarted(timedOut, serverEnded, serverTask, sinceStart.Elapsed, sinceWarmUp.Elapsed), ex);
        }

        // Starting a server is slow on a busy CI runner, so the test's own requests get the whole timeout after it.
        _cancellation.CancelAfter(Timeout);
    }

    /// <summary>
    /// Why a server didn't start: whether the test's timeout fired first or the server's task ended first, and how, with how
    /// long the start took since <see cref="StartAsync"/> began and since the warm-up, and how busy the thread pool was.
    /// </summary>
    private static string WhyNotStarted(TimeSpan? timedOut, TimeSpan? serverEnded, Task serverTask, TimeSpan sinceStart, TimeSpan sinceWarmUp)
    {
        static string At(TimeSpan? time) => time is { } elapsed ? $"{elapsed.TotalMilliseconds:F0} ms after the warm-up" : "not yet";
        var server = serverTask.Status switch
        {
            TaskStatus.Faulted => $"faulted ({serverTask.Exception?.GetBaseException()})",
            TaskStatus.Canceled => "was cancelled",
            TaskStatus.RanToCompletion => "completed",
            _ => "was still running",
        };
        var first = (timedOut, serverEnded) switch
        {
            ({ } timeout, { } ended) => ended < timeout ? "the server's task ended first" : "the timeout came first",
            ({ }, null) => "the timeout came first",
            (null, { }) => "the server's task ended first",
            _ => "neither the timeout nor the server's task had ended",
        };
        return $"The server didn't start: {first}. The timeout: {At(timedOut)}. The server's task {server}, ended: {At(serverEnded)}. " +
            $"Started {sinceStart.TotalMilliseconds:F0} ms ago, {sinceWarmUp.TotalMilliseconds:F0} ms after the warm-up. " +
            $"Thread pool: {ThreadPool.ThreadCount} threads, {ThreadPool.PendingWorkItemCount} work items waiting.";
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
        if (_startFailed)
        {
            // A server that didn't start may not answer a shutdown, and waiting for it, or for its task, would hide why
            // it didn't start behind what the wait threw.
            _client.Dispose();
            return;
        }

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

    protected async Task<List<CodeAction>> CodeActionsIn(DocumentUri uri, Range range, IEnumerable<CodeActionKind>? only = null) =>
        (await _client.TextDocument.RequestCodeAction(
            new CodeActionParams
            {
                TextDocument = new TextDocumentIdentifier(uri),
                Range = range,
                Context = new CodeActionContext { Diagnostics = new Container<Diagnostic>(), Only = only is null ? null : new Container<CodeActionKind>(only) }
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

    /// <summary><paramref name="text"/> with <paramref name="edit"/> made to it.</summary>
    protected static string Applied(string text, TextEdit edit) => Applied(text, [edit]);

    /// <summary><paramref name="text"/> with <paramref name="edits"/>, which do not overlap, made to it.</summary>
    protected static string Applied(string text, IEnumerable<TextEdit> edits)
    {
        var positions = new SourcePositions(text);
        return edits
            .Select(edit => (Start: positions.ToOffset(edit.Range.Start), End: positions.ToOffset(edit.Range.End), edit.NewText))
            .OrderByDescending(edit => edit.Start)
            .Aggregate(text, (applied, edit) => applied[..edit.Start] + edit.NewText + applied[edit.End..]);
    }

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

    /// <summary>
    /// The next diagnostics published on <paramref name="uri"/>, waiting for them if they have not arrived. Those
    /// published on other documents meanwhile are kept for their own call.
    /// </summary>
    protected async Task<PublishDiagnosticsParams> NextPublished(DocumentUri uri)
    {
        while (true)
        {
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

            await _publishedSignal.WaitAsync(_cancellation.Token);
        }
    }


    protected async Task<InfoViewResult> InfoViewAt(DocumentUri uri, int line, int character, int? width = null, Range? selection = null)
    {
        var result = await _client
            .SendRequest(InfoViewHandler.Method, new InfoViewParams
            {
                TextDocument = new TextDocumentIdentifier(uri),
                Position = new Position(line, character),
                Selection = selection,
                Width = width
            })
            .Returning<InfoViewResult?>(_cancellation.Token);
        Assert.NotNull(result);
        // The line's messages, if it has any, come first, and nowhere else.
        Assert.DoesNotContain(result.Scopes.Skip(1), scope => scope.Kind == "messages");
        Assert.Equal("module", result.Scopes[^1].Kind);
        return result;
    }

    /// <summary>The items of the section <paramref name="id"/> of the innermost scope of kind <paramref name="kind"/>, none if it leaves the section out.</summary>
    protected static IReadOnlyList<InfoViewItem> Section(InfoViewResult view, string kind, string id) =>
        view.Scopes.First(scope => scope.Kind == kind).Sections.SingleOrDefault(section => section.Id == id)?.Items ?? [];
}

/// <summary>Runs the classes in <see cref="LanguageServerTestBase.SerialCollection"/> on their own, after the others.</summary>
[CollectionDefinition(LanguageServerTestBase.SerialCollection, DisableParallelization = true)]
public sealed class SerialCollectionDefinition;
