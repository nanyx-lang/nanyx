using MediatR;
using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using InfoView = NyxCompiler.InfoView;
using SymbolKind = NyxCompiler.SymbolKind;

namespace Nanyx.LanguageServer.Handlers;

/// <summary>The parameters of <c>nanyx/infoView</c>: the document and the position to describe.</summary>
[Parallel, Method(InfoViewHandler.Method, Direction.ClientToServer)]
public sealed record InfoViewParams : IRequest<InfoViewResult?>
{
    public required TextDocumentIdentifier TextDocument { get; init; }
    public required Position Position { get; init; }
}

/// <summary>What <c>nanyx/infoView</c> returns: its sections, always all of them and in this order.</summary>
public sealed record InfoViewResult
{
    public required IReadOnlyList<InfoViewSection> Sections { get; init; }
}

/// <summary>
/// A section of the info view. <see cref="Id"/> is one of <c>expression</c>, <c>inScope</c>, <c>topLevel</c>,
/// <c>contexts</c> and <c>messages</c>; <see cref="Collapsed"/> says whether a client shows it closed at first.
/// </summary>
public sealed record InfoViewSection
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public bool Collapsed { get; init; }
    public required IReadOnlyList<InfoViewItem> Items { get; init; }
}

/// <summary>
/// An item of a section: its text (<c>name: type</c>, a context, a message), what kind of thing it is or where it
/// comes from (<c>parameter</c>, <c>use</c>, <c>error NYX0101</c>), and where it is, for a link.
/// </summary>
public sealed record InfoViewItem
{
    public required string Text { get; init; }
    public string? Detail { get; init; }
    public Location? Location { get; init; }
}

/// <summary>
/// <c>nanyx/infoView</c>: what the compiler knows at a position, for the info view panel. The expression there with
/// its type, as hover shows it, and on a use of a generic def the def's declared signature and how the use fills in
/// its type variables (see <see cref="ExpressionItems"/>); the locals, parameters and pattern variables in scope, nearest first; the top-level defs
/// and imports in scope; the contexts in scope, each with where it comes from (a def's signature, a <c>use</c>, a def
/// that was inferred to require it, or the runtime); and the diagnostics on the position's line. Read from the
/// document's last typed compile, as hover is (see <see cref="OpenDocument.LastGood"/>); the diagnostics are this
/// text's own. Each item links to what it describes: a name to its declaration, a context to what provides it.
/// </summary>
public sealed class InfoViewHandler : IJsonRpcRequestHandler<InfoViewParams, InfoViewResult?>
{
    public const string Method = "nanyx/infoView";

    private readonly DocumentStore _documents;
    private readonly ServerSettings _settings;
    private readonly StandardLibrarySource _standardLibrary;

    public InfoViewHandler(DocumentStore documents, ServerSettings settings, StandardLibrarySource standardLibrary)
    {
        _documents = documents;
        _settings = settings;
        _standardLibrary = standardLibrary;
    }

    public Task<InfoViewResult?> Handle(InfoViewParams request, CancellationToken cancellationToken) =>
        Task.FromResult(Compute(request));

    private InfoViewResult? Compute(InfoViewParams request)
    {
        var uri = request.TextDocument.Uri;
        if (_documents.TryGet(uri) is not { } document)
        {
            return null;
        }

        var offset = document.Positions.ToOffset(request.Position);
        var info = document.LastGood is { Compiled: { } compiled } lastGood && document.NearestInLastGood(offset) is { } nearest
            ? InfoView.at(_settings.TypeNotation, lastGood.FileName, compiled, nearest)
            : null;

        Location? Locate(Parser.Program.SourceSpan span) => LocationOf(uri, document, span);

        // The expression's type is only shown where the text has not been edited since it was typed.
        var expression = info?.Expression?.Value is { } hover && document.ToLastGood(offset) is not null
            ? ExpressionItems(hover, info.Instantiation?.Value, Locate)
            : [];
        var line = request.Position.Line;
        var messages = DiagnosticsPublisher.Compute(uri, document)
            .Where(diagnostic => diagnostic.Range.Start.Line <= line && line <= diagnostic.Range.End.Line)
            .Select(diagnostic => new InfoViewItem
            {
                Text = diagnostic.Message.String ?? "",
                Detail = $"{Describe(diagnostic.Severity)} {diagnostic.Code?.String}".TrimEnd(),
                Location = new Location { Uri = uri, Range = diagnostic.Range }
            })
            .ToList();

        InfoViewItem Name(InfoView.ScopedName name) => new()
        {
            Text = name.Type?.Value is { } type ? $"{name.Name}: {type}" : name.Name,
            Detail = Describe(name.Kind),
            Location = Locate(name.Declaration)
        };

        return new InfoViewResult
        {
            Sections =
            [
                new InfoViewSection { Id = "expression", Title = "Expression", Items = expression },
                new InfoViewSection { Id = "inScope", Title = "In scope", Items = info?.Locals.Select(Name).ToList() ?? [] },
                new InfoViewSection
                {
                    Id = "topLevel",
                    Title = "Top level",
                    Collapsed = true,
                    Items = info?.TopLevel.Select(Name).ToList() ?? []
                },
                new InfoViewSection
                {
                    Id = "contexts",
                    Title = "Contexts",
                    Items = info?.Contexts
                        .Select(context => new InfoViewItem
                        {
                            Text = context.Context,
                            Detail = Describe(context.Source),
                            Location = Locate(context.Span)
                        })
                        .ToList() ?? []
                },
                new InfoViewSection { Id = "messages", Title = "Messages", Items = messages }
            ]
        };
    }

    /// <summary>
    /// The items of the expression section: the expression's type, as hover shows it, linking to the expression. On a
    /// use of a generic def, the def's declared signature (<c>declared</c>) comes first, then the type at this use
    /// (<c>at this use</c>), then what the use fills each of the signature's type variables with
    /// (<c>a = string, b = int</c>), each linking to the def.
    /// </summary>
    private static IReadOnlyList<InfoViewItem> ExpressionItems(
        NyxCompiler.Hover.HoverInfo hover,
        NyxCompiler.Hover.Instantiation? instantiation,
        Func<Parser.Program.SourceSpan, Location?> locate)
    {
        if (instantiation is null)
        {
            return [new InfoViewItem { Text = hover.Type, Location = locate(hover.Span) }];
        }

        var definition = locate(instantiation.Definition);
        var items = new List<InfoViewItem>
        {
            new() { Text = instantiation.Declared, Detail = "declared", Location = definition },
            new() { Text = hover.Type, Detail = "at this use", Location = definition }
        };
        if (!instantiation.Bindings.IsEmpty)
        {
            items.Add(new InfoViewItem
            {
                Text = string.Join(", ", instantiation.Bindings.Select(binding => $"{binding.Item1} = {binding.Item2}")),
                Detail = "type variables",
                Location = definition
            });
        }

        return items;
    }

    /// <summary>
    /// The location of a span of the last typed text: in this document's text, if it has not been edited since, or in
    /// another file (see <see cref="SourceLocations.Of"/>). Null for a made-up span.
    /// </summary>
    private Location? LocationOf(DocumentUri uri, OpenDocument document, Parser.Program.SourceSpan span)
    {
        if (span.Start < 0)
        {
            return null;
        }

        if (span.File == document.LastGood?.FileName)
        {
            return document.FromLastGood(span.Start, span.End) is { } here
                ? new Location { Uri = uri, Range = document.Positions.ToRange(here.Start, here.End) }
                : null;
        }

        return SourceLocations.Of(uri, document, span, _standardLibrary);
    }

    private static string Describe(SymbolKind kind) =>
        kind.Tag switch
        {
            SymbolKind.Tags.Parameter => "parameter",
            SymbolKind.Tags.LocalValue => "local",
            SymbolKind.Tags.PatternVariable => "pattern variable",
            SymbolKind.Tags.ImportedModule => "import",
            _ => "def"
        };

    private static string Describe(InfoView.ContextSource source) =>
        source.Tag switch
        {
            InfoView.ContextSource.Tags.Signature => "signature",
            InfoView.ContextSource.Tags.Use => "use",
            InfoView.ContextSource.Tags.Inferred => "inferred",
            _ => "runtime"
        };

    private static string Describe(DiagnosticSeverity? severity) =>
        severity switch
        {
            DiagnosticSeverity.Warning => "warning",
            DiagnosticSeverity.Information => "info",
            DiagnosticSeverity.Hint => "hint",
            _ => "error"
        };
}
