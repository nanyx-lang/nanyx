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
/// its <c>::linter</c> and <c>::lint</c> directives configure, or none if it has none, as <c>nanyx run</c> does for a script.
/// </param>
/// <param name="StandardLibrarySource">
/// Where the standard library's source is written for the editor to open; under the user's local application data
/// when <c>null</c>.
/// </param>
/// <param name="LocalTypeHints">Whether to show inlay hints for the inferred types of local defs.</param>
/// <param name="ParameterTypeHints">Whether to show inlay hints for the inferred types of lambda parameters.</param>
/// <param name="PatternVariableTypeHints">Whether to show inlay hints for the inferred types of pattern variables.</param>
/// <param name="CompileDelay">
/// How long a changed document must go unchanged before it is compiled (see <see cref="CompileScheduler"/>);
/// <see cref="DefaultCompileDelay"/> when <c>null</c>.
/// </param>
/// <param name="CompileStarted">Called as each compile of a document starts. For tests, which count them.</param>
/// <param name="Timings">
/// Whether each compile's log line, which says how long it took, also shows how long each module's check took, as a
/// tree of the modules each one imported (<c>nanyx lsp --timings</c>; the VS Code extension passes it in development
/// mode).
/// </param>
/// <param name="Packages">
/// The packages a script's directives are read against: its packages and the index the newest version of one is looked
/// up in. The user's (<see cref="SeededPackages.user"/>) when <c>null</c>. For tests, which give an index of their own.
/// </param>
/// <param name="FormattingHints">
/// Whether to show where a document differs from its formatted text, as Hint diagnostics with quick fixes (see
/// <see cref="Nanyx.LanguageServer.FormattingHints"/>).
/// </param>
/// <param name="LintRunner">
/// What runs the lint builds of the linter packages a document's manifest names, which the server doesn't reference:
/// <c>nanyx lsp</c> gives the CLI's. When <c>null</c>, only the linters built into the compiler run.
/// </param>
public sealed record ServerSettings(
    TypeNotation TypeNotation,
    TopLevelTypeHints TopLevelTypeHints = TopLevelTypeHints.Lens,
    LinterOptions? Linters = null,
    StandardLibrarySource? StandardLibrarySource = null,
    bool LocalTypeHints = true,
    bool ParameterTypeHints = true,
    bool PatternVariableTypeHints = true,
    TimeSpan? CompileDelay = null,
    Action<OmniSharp.Extensions.LanguageServer.Protocol.DocumentUri>? CompileStarted = null,
    bool Timings = false,
    bool FormattingHints = true,
    Func<PackageStore>? Packages = null,
    LintRunner? LintRunner = null)
{
    /// <summary>The packages a script's directives are read against (see <see cref="Packages"/>).</summary>
    public PackageStore PackageStore() => Packages?.Invoke() ?? SeededPackages.user();

    public static ServerSettings Default { get; } = new(TypeNotation.Unicode);

    /// <summary>How long a changed document waits to be compiled, unless <see cref="CompileDelay"/> says otherwise.</summary>
    public static readonly TimeSpan DefaultCompileDelay = TimeSpan.FromMilliseconds(150);

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
    /// its <paramref name="directives"/> (which give none in a package). A linter package's lint build is built once, and
    /// kept, so a keystroke doesn't build it again (see <c>LintBuilds</c>). Without <see cref="LintRunner"/>, only the
    /// linters built into the compiler run, and a manifest's linter packages aren't read.
    /// </summary>
    public LinterOptions LinterOptionsFor(string? path, ScriptDirectives directives) =>
        Linters
        ?? (directives.Linters is null ? null : LintRunner is null ? directives.Linters.Value : Script.linterOptions(MakeLinter(LintRunner), directives))
        ?? (path is null ? null : LintRunner is null ? PackageModule.builtInLintersFor(path)?.Value : PackageModule.lintersFor(PackageStore(), MakeLinter(LintRunner), path)?.Value)
        ?? NyxCompiler.Linters.none;

    /// <summary>Makes the linter a linter package's lint build runs with <paramref name="runner"/>, or why it can't be.</summary>
    private Microsoft.FSharp.Core.FSharpFunc<LinterPackage, Microsoft.FSharp.Core.FSharpResult<Linter, string>> MakeLinter(LintRunner runner) =>
        Microsoft.FSharp.Core.FuncConvert.FromFunc((LinterPackage linter) => LintBuilds.make(PackageStore(), runner, linter));
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
