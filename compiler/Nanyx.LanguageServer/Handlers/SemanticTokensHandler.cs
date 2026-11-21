using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using SemanticTokens = NyxCompiler.SemanticTokens;

namespace Nanyx.LanguageServer.Handlers;

/// <summary>
/// Says what each name in the document is, so that themes colour it as they colour other languages' names: functions,
/// parameters, variables, fields, tags, types, contexts (<c>interface</c>), modules (<c>namespace</c>), operators that
/// call a definition and workflow keywords, with the standard <c>declaration</c>, <c>readonly</c> and
/// <c>defaultLibrary</c> modifiers, and three of the server's own: <see cref="Mutable"/> for a cell, and
/// <see cref="Nominal"/> and <see cref="Private"/> for the names of protected and private types, so that the editor can
/// show them apart from aliases of a shape, whose names look the same. A name that names nothing in scope has the
/// server's own <see cref="Unresolved"/> type, which the extension colours as its theme colours errors. Everything else
/// is left to the extension's TextMate grammar. The tokens come from the document's last compile that was
/// typed (see <c>NyxCompiler.SemanticTokens.inModule</c>); none is given in a top-level item edited since.
/// </summary>
public sealed class SemanticTokensHandler : SemanticTokensHandlerBase
{
    /// <summary>The type of a name that names nothing in scope: an unknown value, function, member, type, context or module.</summary>
    public static readonly SemanticTokenType Unresolved = new("unresolved");

    /// <summary>The modifier of a cell: a local made with <c>mut</c>, or a parameter that takes one.</summary>
    public static readonly SemanticTokenModifier Mutable = new("mutable");

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

        foreach (var token in SemanticTokens.inModule(lastGood.FileName, lastGood.Text, compiled))
        {
            if (document.FromLastGood(token.Span.Start, token.Span.End) is { } span)
            {
                builder.Push(
                    document.Positions.ToRange(span.Start, span.End),
                    TypeOf(token.Type),
                    token.Modifiers.Select(ModifierOf));
            }
        }

        return Task.CompletedTask;
    }

    private static SemanticTokenType TypeOf(SemanticTokens.TokenType type) => type.Tag switch
    {
        SemanticTokens.TokenType.Tags.Function => SemanticTokenType.Function,
        SemanticTokens.TokenType.Tags.Parameter => SemanticTokenType.Parameter,
        SemanticTokens.TokenType.Tags.Variable => SemanticTokenType.Variable,
        SemanticTokens.TokenType.Tags.Property => SemanticTokenType.Property,
        SemanticTokens.TokenType.Tags.EnumMember => SemanticTokenType.EnumMember,
        SemanticTokens.TokenType.Tags.Type => SemanticTokenType.Type,
        SemanticTokens.TokenType.Tags.Interface => SemanticTokenType.Interface,
        SemanticTokens.TokenType.Tags.Namespace => SemanticTokenType.Namespace,
        SemanticTokens.TokenType.Tags.Operator => SemanticTokenType.Operator,
        SemanticTokens.TokenType.Tags.Unresolved => Unresolved,
        _ => SemanticTokenType.Keyword
    };

    private static SemanticTokenModifier ModifierOf(SemanticTokens.TokenModifier modifier) => modifier.Tag switch
    {
        SemanticTokens.TokenModifier.Tags.Declaration => SemanticTokenModifier.Declaration,
        SemanticTokens.TokenModifier.Tags.Readonly => SemanticTokenModifier.Readonly,
        SemanticTokens.TokenModifier.Tags.DefaultLibrary => SemanticTokenModifier.DefaultLibrary,
        SemanticTokens.TokenModifier.Tags.Mutable => Mutable,
        SemanticTokens.TokenModifier.Tags.Nominal => Nominal,
        _ => Private
    };

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
                TokenTypes = new Container<SemanticTokenType>(
                    SemanticTokenType.Function,
                    SemanticTokenType.Parameter,
                    SemanticTokenType.Variable,
                    SemanticTokenType.Property,
                    SemanticTokenType.EnumMember,
                    SemanticTokenType.Type,
                    SemanticTokenType.Interface,
                    SemanticTokenType.Namespace,
                    SemanticTokenType.Operator,
                    SemanticTokenType.Keyword,
                    Unresolved),
                TokenModifiers = new Container<SemanticTokenModifier>(
                    SemanticTokenModifier.Declaration,
                    SemanticTokenModifier.Readonly,
                    SemanticTokenModifier.DefaultLibrary,
                    Mutable,
                    Nominal,
                    Private)
            },
            Full = new SemanticTokensCapabilityRequestFull { Delta = false },
            Range = true
        };
}
