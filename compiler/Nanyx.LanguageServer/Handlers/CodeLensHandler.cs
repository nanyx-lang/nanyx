using NyxCompiler;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nanyx.LanguageServer.Handlers;

/// <summary>
/// Shows the inferred type above each top-level def that does not write its own, read from the document's last
/// compile that type-checked, so the lenses stay while the user is typing and the code does not move. Where one def
/// declares several names (a destructuring def), each lens says which name it is for. Nothing is shown for a def
/// edited since that compile, nor unless <see cref="ServerSettings.TopLevelTypeHints"/> is
/// <see cref="TopLevelTypeHints.Lens"/>.
/// </summary>
public sealed class CodeLensHandler : CodeLensHandlerBase
{
    private readonly DocumentStore _documents;
    private readonly ServerSettings _settings;

    public CodeLensHandler(DocumentStore documents, ServerSettings settings)
    {
        _documents = documents;
        _settings = settings;
    }

    public override Task<CodeLensContainer?> Handle(CodeLensParams request, CancellationToken cancellationToken) =>
        Task.FromResult(Compute(request));

    // Every lens is sent with its command, so there is nothing left to resolve.
    public override Task<CodeLens> Handle(CodeLens request, CancellationToken cancellationToken) =>
        Task.FromResult(request);

    private CodeLensContainer? Compute(CodeLensParams request)
    {
        var document = _documents.TryGet(request.TextDocument.Uri);
        if (_settings.TopLevelTypeHints != TopLevelTypeHints.Lens || document?.LastGood?.Compiled is not { } compiled)
        {
            return null;
        }

        var lenses = TypeHints.typeLenses(_settings.TypeVarNames, document.FileName, compiled)
            .Select(lens => (lens, span: document.FromLastGood(lens.Span.Start, lens.Span.End)))
            .Where(mapped => mapped.span is not null)
            .Select(mapped => (mapped.lens, span: mapped.span!.Value))
            .ToList();
        var sharedLines = lenses
            .GroupBy(mapped => mapped.lens.Span.StartLine)
            .Where(line => line.Count() > 1)
            .Select(line => line.Key)
            .ToHashSet();

        return new CodeLensContainer(lenses.Select(mapped => new CodeLens
        {
            Range = document.Positions.ToRange(mapped.span.Start, mapped.span.End),
            // A command with no name is a label: VS Code shows its title and does nothing when it is clicked.
            Command = new Command
            {
                Name = "",
                Title = sharedLines.Contains(mapped.lens.Span.StartLine)
                    ? $"{mapped.lens.Name}: {mapped.lens.Type}"
                    : mapped.lens.Type
            }
        }));
    }

    protected override CodeLensRegistrationOptions CreateRegistrationOptions(
        CodeLensCapability capability,
        ClientCapabilities clientCapabilities) =>
        new() { DocumentSelector = TextDocumentSyncHandler.Selector, ResolveProvider = false };
}
