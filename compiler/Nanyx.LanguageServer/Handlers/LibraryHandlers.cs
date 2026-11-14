using MediatR;
using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using LibraryCatalog = NyxCompiler.LibraryCatalog;

namespace Nanyx.LanguageServer.Handlers;

/// <summary>The parameters of <c>nanyx/libraryModules</c>: none.</summary>
[Parallel, Method(LibraryModulesHandler.Method, Direction.ClientToServer)]
public sealed record LibraryModulesParams : IRequest<IReadOnlyList<LibraryModuleItem>>;

/// <summary>A module of the standard library: its import path (<c>nanyx/text</c>) and its doc comment, as Markdown.</summary>
public sealed record LibraryModuleItem
{
    public required string Name { get; init; }
    public string? Documentation { get; init; }
}

/// <summary>The parameters of <c>nanyx/libraryModule</c>: the module's import path, as <c>nanyx/text</c>.</summary>
[Parallel, Method(LibraryModuleHandler.Method, Direction.ClientToServer)]
public sealed record LibraryModuleParams : IRequest<IReadOnlyList<LibraryExportItem>?>
{
    public required string Name { get; init; }
}

/// <summary>
/// A name a standard-library module exports. <see cref="Kind"/> is one of <c>function</c>, <c>value</c>, <c>type</c>,
/// <c>context</c> and <c>attached</c>, a def attached to the type <see cref="AttachedTo"/> (<c>string.trim</c> to
/// <c>string</c>). <see cref="Signature"/> is its declaration as hover writes it, <see cref="Documentation"/> its doc
/// comment as Markdown, <see cref="Import"/> the import that brings it in (none for the prelude's), and
/// <see cref="Location"/> where it is declared, in the copy of the library's source <see cref="StandardLibrarySource"/>
/// writes.
/// </summary>
public sealed record LibraryExportItem
{
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public string? AttachedTo { get; init; }
    public required string Signature { get; init; }
    public string? Documentation { get; init; }
    public string? Import { get; init; }
    public Location? Location { get; init; }
}

/// <summary>
/// <c>nanyx/libraryModules</c>: every module of the standard library, the prelude first, with its doc comment, for the
/// extension's library browser (see <see cref="LibraryCatalog"/>).
/// </summary>
public sealed class LibraryModulesHandler : IJsonRpcRequestHandler<LibraryModulesParams, IReadOnlyList<LibraryModuleItem>>
{
    public const string Method = "nanyx/libraryModules";

    private readonly StandardLibrarySource _standardLibrary;

    public LibraryModulesHandler(StandardLibrarySource standardLibrary)
    {
        _standardLibrary = standardLibrary;
    }

    public Task<IReadOnlyList<LibraryModuleItem>> Handle(LibraryModulesParams request, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<LibraryModuleItem>>(
            LibraryCatalog.modules(LibraryHandlers.LinkTo(_standardLibrary))
                .Select(module => new LibraryModuleItem { Name = module.Name, Documentation = module.Doc?.Value })
                .ToList());
}

/// <summary>
/// <c>nanyx/libraryModule</c>: what one module of the standard library exports, in the order its files declare them,
/// without the names it reserves (see <see cref="LibraryExportItem"/>); <c>null</c> when no module has the name.
/// </summary>
public sealed class LibraryModuleHandler : IJsonRpcRequestHandler<LibraryModuleParams, IReadOnlyList<LibraryExportItem>?>
{
    public const string Method = "nanyx/libraryModule";

    private readonly StandardLibrarySource _standardLibrary;

    public LibraryModuleHandler(StandardLibrarySource standardLibrary)
    {
        _standardLibrary = standardLibrary;
    }

    public Task<IReadOnlyList<LibraryExportItem>?> Handle(LibraryModuleParams request, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<LibraryExportItem>?>(
            LibraryCatalog.exports(LibraryHandlers.LinkTo(_standardLibrary), request.Name)?.Value
                .Select(export => new LibraryExportItem
                {
                    Name = export.Name,
                    Kind = KindOf(export.Kind),
                    AttachedTo = export.Kind is LibraryCatalog.ExportKind.Attached attached ? attached.attachedTo : null,
                    Signature = export.Signature,
                    Documentation = export.Doc?.Value,
                    Import = export.Import?.Value,
                    Location = SourceLocations.InFile(export.Declaration, _standardLibrary)
                })
                .ToList());

    private static string KindOf(LibraryCatalog.ExportKind kind) =>
        kind.Tag switch
        {
            LibraryCatalog.ExportKind.Tags.Function => "function",
            LibraryCatalog.ExportKind.Tags.Value => "value",
            LibraryCatalog.ExportKind.Tags.Type => "type",
            LibraryCatalog.ExportKind.Tags.Context => "context",
            _ => "attached"
        };
}

internal static class LibraryHandlers
{
    /// <summary>The target of a link in a doc comment of the library: its place in the copy of the library's source.</summary>
    public static Func<Parser.Program.SourceSpan, string> LinkTo(StandardLibrarySource standardLibrary) =>
        span => SourceLocations.LinkTo(span, standardLibrary)!;
}
