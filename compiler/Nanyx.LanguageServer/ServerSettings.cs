using NyxCompiler;

namespace Nanyx.LanguageServer;

/// <summary>Choices fixed when the server starts, set by <c>nanyx lsp</c>'s options.</summary>
/// <param name="TypeNotation">
/// The characters hovers, hints and completions write types in: Unicode (<c>α</c>, <c>…</c>) or ASCII only.
/// </param>
/// <param name="TopLevelTypeHints">Where to show the inferred types of top-level defs that do not write theirs.</param>
/// <param name="Linters">
/// Which linters run on each document and how serious their diagnostics are. When <c>null</c>, a document in a package
/// gets the linters its <c>.nyxp</c> manifest configures, as <c>nanyx build</c> does, and any other document those
/// its <c>::linters</c> directives configure, or none if it has none, as <c>nanyx run</c> does for a script.
/// </param>
/// <param name="StandardLibrarySource">
/// Where the standard library's source is written for the editor to open; under the user's local application data
/// when <c>null</c>.
/// </param>
/// <param name="LocalTypeHints">Whether to show inlay hints for the inferred types of local defs.</param>
/// <param name="ParameterTypeHints">Whether to show inlay hints for the inferred types of lambda parameters.</param>
/// <param name="PatternVariableTypeHints">Whether to show inlay hints for the inferred types of pattern variables.</param>
public sealed record ServerSettings(
    TypeNotation TypeNotation,
    TopLevelTypeHints TopLevelTypeHints = TopLevelTypeHints.Lens,
    LinterOptions? Linters = null,
    StandardLibrarySource? StandardLibrarySource = null,
    bool LocalTypeHints = true,
    bool ParameterTypeHints = true,
    bool PatternVariableTypeHints = true)
{
    public static ServerSettings Default { get; } = new(TypeNotation.Unicode);

    /// <summary>Whether an inlay hint is shown for a declaration of <paramref name="kind"/>.</summary>
    public bool ShowsInlayHintFor(SymbolKind kind) =>
        kind.Tag switch
        {
            SymbolKind.Tags.TopLevelValue => TopLevelTypeHints == TopLevelTypeHints.Inlay,
            SymbolKind.Tags.LocalValue => LocalTypeHints,
            SymbolKind.Tags.Parameter => ParameterTypeHints,
            SymbolKind.Tags.PatternVariable => PatternVariableTypeHints,
            _ => true
        };

    /// <summary>
    /// The linter options the document at <paramref name="path"/> (<c>null</c> if not on disk) is compiled with, given
    /// its <paramref name="directives"/> (which give none in a package).
    /// </summary>
    public LinterOptions LinterOptionsFor(string? path, ScriptDirectives directives) =>
        Linters
        ?? directives.Linters?.Value
        ?? (path is null ? null : PackageModule.lintersFor(path)?.Value)
        ?? NyxCompiler.Linters.none;
}

/// <summary>Where the inferred type of a top-level def that does not write its own is shown.</summary>
public enum TopLevelTypeHints
{
    /// <summary>In a lens above the def.</summary>
    Lens,

    /// <summary>In an inlay hint after the def's name, as for locals and parameters.</summary>
    Inlay,

    /// <summary>Nowhere (hover still shows it).</summary>
    Off
}
