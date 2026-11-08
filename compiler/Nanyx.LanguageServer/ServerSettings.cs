using NyxCompiler;

namespace Nanyx.LanguageServer;

/// <summary>Choices fixed when the server starts, set by <c>nanyx lsp</c>'s options.</summary>
/// <param name="TypeVarNames">How hover names the type variables the compiler made up.</param>
/// <param name="TopLevelTypeHints">Where to show the inferred types of top-level defs that do not write theirs.</param>
/// <param name="Analyzers">
/// Which analyzers run on each document and how serious their diagnostics are. When <c>null</c>, a document in a package
/// gets the analyzers its <c>.nyxpack</c> manifest configures, as <c>nanyx build</c> does, and any other document those
/// its <c>#:analyzer</c> directives configure, or every analyzer at its rules' default severities if it has none, as
/// <c>nanyx run</c> does for a script.
/// </param>
/// <param name="StandardLibrarySource">
/// Where the standard library's source is written for the editor to open; under the user's local application data
/// when <c>null</c>.
/// </param>
public sealed record ServerSettings(
    TypeVarNames TypeVarNames,
    TopLevelTypeHints TopLevelTypeHints = TopLevelTypeHints.Lens,
    AnalyzerOptions? Analyzers = null,
    StandardLibrarySource? StandardLibrarySource = null)
{
    public static ServerSettings Default { get; } = new(TypeVarNames.Greek);

    /// <summary>
    /// The analyzer options the document at <paramref name="path"/> (<c>null</c> if not on disk) is compiled with, given
    /// its <paramref name="directives"/> (which give none in a package).
    /// </summary>
    public AnalyzerOptions AnalyzerOptionsFor(string? path, ScriptDirectives directives) =>
        Analyzers
        ?? directives.Analyzers?.Value
        ?? (path is null ? null : PackageModule.analyzersFor(path)?.Value)
        ?? NyxCompiler.Analyzers.defaultOptions;
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
