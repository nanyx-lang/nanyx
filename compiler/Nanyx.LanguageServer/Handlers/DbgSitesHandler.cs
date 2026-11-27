using MediatR;
using Microsoft.FSharp.Collections;
using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using DbgSites = NyxCompiler.DbgSites;

namespace Nanyx.LanguageServer.Handlers;

/// <summary>
/// The parameters of <c>nanyx/dbgSites</c>: the document, its text <see cref="Before"/> as it was when a run started,
/// and the places of the <c>dbg</c>s the run reached, as the run names them.
/// </summary>
[Parallel, Method(DbgSitesHandler.Method, Direction.ClientToServer)]
public sealed record DbgSitesParams : IRequest<IReadOnlyList<DbgPlace?>?>
{
    public required TextDocumentIdentifier TextDocument { get; init; }
    public required string Before { get; init; }
    public required IReadOnlyList<DbgPlace> Places { get; init; }
}

/// <summary>Where a <c>dbg</c> is: the line and column of its name, counted from 1, as a run names a hit's place.</summary>
public sealed record DbgPlace
{
    public required int Line { get; init; }
    public required int Column { get; init; }
}

/// <summary>
/// <c>nanyx/dbgSites</c>: where each of a run's <c>dbg</c> places is in the document's text now, for the extension to
/// keep each value on its <c>dbg</c>'s line while the lines around it change (see <c>DbgSites.moved</c>). Each is
/// <c>null</c> where its <c>dbg</c> can't be found; the whole result is <c>null</c> where the document isn't open or
/// either text doesn't parse, when the extension keeps the places it has.
/// </summary>
public sealed class DbgSitesHandler : IJsonRpcRequestHandler<DbgSitesParams, IReadOnlyList<DbgPlace?>?>
{
    public const string Method = "nanyx/dbgSites";

    private readonly DocumentStore _documents;

    public DbgSitesHandler(DocumentStore documents)
    {
        _documents = documents;
    }

    public Task<IReadOnlyList<DbgPlace?>?> Handle(DbgSitesParams request, CancellationToken cancellationToken)
    {
        if (_documents.TryGet(request.TextDocument.Uri) is not { } document)
        {
            return Task.FromResult<IReadOnlyList<DbgPlace?>?>(null);
        }

        var places = ListModule.OfSeq(request.Places.Select(place => Tuple.Create(place.Line, place.Column)));
        var moved = DbgSites.moved(request.Before, document.Text, places);
        return Task.FromResult<IReadOnlyList<DbgPlace?>?>(
            moved?.Value
                .Select(place => place is null ? null : new DbgPlace { Line = place.Value.Item1, Column = place.Value.Item2 })
                .ToList());
    }
}
