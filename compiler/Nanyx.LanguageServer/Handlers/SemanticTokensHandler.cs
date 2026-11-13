using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using NominalNames = NyxCompiler.NominalNames;

namespace Nanyx.LanguageServer.Handlers;

/// <summary>
/// Marks the names of nominal types, so that the editor can show them apart from aliases of a shape, whose names look
/// the same: each name of a protected or private type (its declaration, a type that names it, its constructor, a pattern
/// that takes it apart, the type's part of an attached definition's name) is a <c>type</c> token with the
/// <see cref="Nominal"/> modifier, and a private one has <see cref="Private"/> too. Every other token is left to the
/// extension's TextMate grammar. The names come from the document's last compile that was typed (see
/// <see cref="NominalNames.inModule"/>); none is marked in a top-level item edited since.
/// </summary>
public sealed class SemanticTokensHandler : SemanticTokensHandlerBase
{
    /// <summary>The modifier of a nominal type's name, protected or private.</summary>
    public static readonly SemanticTokenModifier Nominal = new("nominal");

    /// <summary>The modifier of a private type's name, beside <see cref="Nominal"/>.</summary>
    public static readonly SemanticTokenModifier Private = new("private");

    private readonly DocumentStore _documents;

    public SemanticTokensHandler(DocumentStore documents)
    {
        _documents = documents;
    }

    protected override Task Tokenize(
        SemanticTokensBuilder builder,
        ITextDocumentIdentifierParams identifier,
        CancellationToken cancellationToken)
    {
        var document = _documents.TryGet(identifier.TextDocument.Uri);
        if (document?.LastGood is not { Compiled: { } compiled } lastGood)
        {
            return Task.CompletedTask;
        }

        foreach (var name in NominalNames.inModule(lastGood.FileName, compiled))
        {
            if (document.FromLastGood(name.Span.Start, name.Span.End) is { } span)
            {
                var modifiers = name.IsPrivate ? new[] { Nominal, Private } : [Nominal];
                builder.Push(document.Positions.ToRange(span.Start, span.End), SemanticTokenType.Type, modifiers);
            }
        }

        return Task.CompletedTask;
    }

    protected override Task<SemanticTokensDocument> GetSemanticTokensDocument(
        ITextDocumentIdentifierParams @params,
        CancellationToken cancellationToken) =>
        Task.FromResult(new SemanticTokensDocument(RegistrationOptions.Legend));

    protected override SemanticTokensRegistrationOptions CreateRegistrationOptions(
        SemanticTokensCapability capability,
        ClientCapabilities clientCapabilities) =>
        new()
        {
            DocumentSelector = TextDocumentSyncHandler.Selector,
            Legend = new SemanticTokensLegend
            {
                TokenTypes = new Container<SemanticTokenType>(SemanticTokenType.Type),
                TokenModifiers = new Container<SemanticTokenModifier>(Nominal, Private)
            },
            Full = new SemanticTokensCapabilityRequestFull { Delta = false },
            Range = true
        };
}
