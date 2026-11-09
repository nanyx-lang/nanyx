using NyxCompiler;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nanyx.LanguageServer.Handlers;

/// <summary>
/// Finds the names that refer to what the name under the cursor declares or refers to, as go-to-definition finds
/// it: a value, parameter, pattern variable, import, type or context member. Only the document's own compile is
/// searched, not the files that import it. With <c>includeDeclaration</c>, the declaration comes first, wherever
/// it is (see <see cref="SourceLocations"/>). Nothing is found in a document that does not parse.
/// </summary>
public sealed class ReferencesHandler : ReferencesHandlerBase
{
    private readonly DocumentStore _documents;
    private readonly StandardLibrarySource _standardLibrary;

    public ReferencesHandler(DocumentStore documents, StandardLibrarySource standardLibrary)
    {
        _documents = documents;
        _standardLibrary = standardLibrary;
    }

    public override Task<LocationContainer?> Handle(ReferenceParams request, CancellationToken cancellationToken) =>
        Task.FromResult(Compute(request));

    private LocationContainer? Compute(ReferenceParams request)
    {
        var uri = request.TextDocument.Uri;
        var document = _documents.TryGet(uri);
        if (document?.Compiled is not { } compiled)
        {
            return null;
        }

        var symbol = Definition.symbolAt(document.FileName, compiled, document.Positions.ToOffset(request.Position));
        if (symbol is null)
        {
            return null;
        }

        var locations = new List<Location>();
        if (request.Context.IncludeDeclaration
            && SourceLocations.Of(uri, document, symbol.Value.Declaration, _standardLibrary) is { } declaration)
        {
            locations.Add(declaration);
        }

        locations.AddRange(
            Definition.referencesTo(compiled, symbol.Value)
                .Select(span => new Location { Uri = uri, Range = document.Positions.ToRange(span.Start, span.End) }));

        return new LocationContainer(locations);
    }

    protected override ReferenceRegistrationOptions CreateRegistrationOptions(
        ReferenceCapability capability,
        ClientCapabilities clientCapabilities) =>
        new() { DocumentSelector = TextDocumentSyncHandler.Selector };
}
