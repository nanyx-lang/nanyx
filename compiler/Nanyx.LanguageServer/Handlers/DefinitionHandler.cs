using NyxCompiler;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nanyx.LanguageServer.Handlers;

/// <summary>
/// Goes to where the name under the cursor is declared: a value, parameter, pattern variable or import, in this
/// document, in a source file it imports, or in the standard library (see <see cref="StandardLibrarySource"/>). On
/// a declaration's own name it goes to that declaration. Nothing is found in a document that did not type-check.
/// </summary>
public sealed class DefinitionHandler : DefinitionHandlerBase
{
    private readonly DocumentStore _documents;
    private readonly StandardLibrarySource _standardLibrary;

    public DefinitionHandler(DocumentStore documents, StandardLibrarySource standardLibrary)
    {
        _documents = documents;
        _standardLibrary = standardLibrary;
    }

    public override Task<LocationOrLocationLinks?> Handle(DefinitionParams request, CancellationToken cancellationToken) =>
        Task.FromResult(Compute(request));

    private LocationOrLocationLinks? Compute(DefinitionParams request)
    {
        var document = _documents.TryGet(request.TextDocument.Uri);
        if (document?.Compiled is not { } compiled)
        {
            return null;
        }

        var symbol = Definition.symbolAt(document.FileName, compiled, document.Positions.ToOffset(request.Position));
        if (symbol is null)
        {
            return null;
        }

        var location = SourceLocations.Of(request.TextDocument.Uri, document, symbol.Value.Declaration, _standardLibrary);
        return location is null ? null : new LocationOrLocationLinks(location);
    }

    protected override DefinitionRegistrationOptions CreateRegistrationOptions(
        DefinitionCapability capability,
        ClientCapabilities clientCapabilities) =>
        new() { DocumentSelector = TextDocumentSyncHandler.Selector };
}
