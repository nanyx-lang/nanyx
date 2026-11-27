using MediatR;
using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using LiveMode = NyxCompiler.LiveMode;

namespace Nanyx.LanguageServer.Handlers;

/// <summary>The parameters of <c>nanyx/liveMode</c>: the document.</summary>
[Parallel, Method(LiveModeHandler.Method, Direction.ClientToServer)]
public sealed record LiveModeParams : IRequest<LiveModeResult>
{
    public required TextDocumentIdentifier TextDocument { get; init; }
}

/// <summary>
/// What live mode knows of a document: its entry point's kind, <c>statements</c> for a script's or
/// <c>entryDefinition</c> for a def marked <c>@entry</c>, or <c>null</c> for none; the contexts that entry point
/// requires; and whether live mode is offered on its own, which it is when they are among <c>Console</c>,
/// <c>Async</c> and <c>FileSystem</c>.
/// </summary>
public sealed record LiveModeResult
{
    public string? Entry { get; init; }
    public required IReadOnlyList<string> Contexts { get; init; }
    public required bool Offered { get; init; }
}

/// <summary>
/// <c>nanyx/liveMode</c>: the document's entry point and the contexts it requires, from its last compile that was typed
/// (see <c>LiveMode.entryOf</c>), for the extension's live mode, which re-runs the file on every save (#2055).
/// </summary>
public sealed class LiveModeHandler : IJsonRpcRequestHandler<LiveModeParams, LiveModeResult>
{
    public const string Method = "nanyx/liveMode";

    private static readonly LiveModeResult None = new() { Contexts = [], Offered = false };

    private readonly DocumentStore _documents;

    public LiveModeHandler(DocumentStore documents)
    {
        _documents = documents;
    }

    public Task<LiveModeResult> Handle(LiveModeParams request, CancellationToken cancellationToken)
    {
        if (_documents.TryGet(request.TextDocument.Uri)?.LastGood?.Compiled?.Typed?.Value is not { } typed
            || LiveMode.entryOf(typed)?.Value is not { } entry)
        {
            return Task.FromResult(None);
        }

        return Task.FromResult(new LiveModeResult
        {
            Entry = entry.Kind.IsStatements ? "statements" : "entryDefinition",
            Contexts = entry.Contexts.ToList(),
            Offered = LiveMode.offered(entry)
        });
    }
}
