using NyxCompiler;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

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

        var location = LocationOf(request.TextDocument.Uri, document, symbol.Value.Declaration);
        return location is null ? null : new LocationOrLocationLinks(location);
    }

    private Location? LocationOf(DocumentUri uri, OpenDocument document, Parser.Program.SourceSpan span)
    {
        if (span.File == document.FileName)
        {
            return new Location { Uri = uri, Range = document.Positions.ToRange(span.Start, span.End) };
        }

        // Another source file. A standard-library module is named by its import path instead ("nanyx/text.nyx"),
        // and is opened from the copy of its source on disk.
        var path = Path.IsPathRooted(span.File) ? span.File : _standardLibrary.TryGetPath(span.File);
        if (path is null)
        {
            return null;
        }

        // The declaration is a name, so it is on one line, and its range follows from where it starts.
        return new Location
        {
            Uri = DocumentUri.FromFileSystemPath(path),
            Range = new Range(span.StartLine, span.StartColumn, span.StartLine, span.StartColumn + span.End - span.Start)
        };
    }

    protected override DefinitionRegistrationOptions CreateRegistrationOptions(
        DefinitionCapability capability,
        ClientCapabilities clientCapabilities) =>
        new() { DocumentSelector = TextDocumentSyncHandler.Selector };
}
