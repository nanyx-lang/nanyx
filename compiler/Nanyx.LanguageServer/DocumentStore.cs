using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NyxCompiler;
using OmniSharp.Extensions.LanguageServer.Protocol;

namespace Nanyx.LanguageServer;

/// <summary>An open document: its text and the result of compiling that text.</summary>
/// <param name="Text">The editor's buffer, which is usually ahead of the saved file.</param>
/// <param name="FileName">
/// The file the compiler's spans name for this document: its full path, or empty for a document not on disk.
/// </param>
/// <param name="Compiled">
/// What the compiler made of <paramref name="Text"/>; <c>null</c> if the compiler threw, or if it has not compiled the
/// text yet (see <see cref="CompileScheduler"/>).
/// </param>
/// <param name="CompilerFailure">The exception the compiler threw, if it did.</param>
/// <param name="PreviousGood">
/// The last earlier text of this document that was typed whole, if this one was not: see <see cref="LastGood"/>.
/// </param>
public sealed record OpenDocument(
    string Text,
    string FileName,
    CompileResult? Compiled,
    Exception? CompilerFailure,
    OpenDocument? PreviousGood = null)
{
    private EditedRegion? _editsSinceLastGood;
    private Lazy<Outline.OutlineItem[]?>? _outline;

    private Lazy<ConvertedDiagnostics>? _converted;
    private Lazy<IReadOnlyList<FormattingHint>>? _formattingHints;
    private Lazy<Nanyx.Formatter.Formatter.Options>? _refactoringOptions;

    /// <summary>Converts positions in <see cref="Text"/>, built once per change rather than on every request.</summary>
    public SourcePositions Positions { get; } = new(Text);

    /// <summary>
    /// The text the compile read of each other source file it needed, by full path: the files the document imports,
    /// directly or through other modules, and the other files of a module split across files. An open file's is its
    /// editor's text. A change to one of them compiles this document again (see <see cref="CompileScheduler"/>). A text
    /// not compiled yet keeps those of the compile before it.
    /// </summary>
    public IReadOnlyDictionary<string, string> Sources { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// What <see cref="Text"/> declares, as a tree (see <c>NyxCompiler.Outline</c>), or <c>null</c> if it does not
    /// parse. It needs only a parse, so a text that does not type-check has one. Parsed once, when first asked for.
    /// </summary>
    public Outline.OutlineItem[]? Outline =>
        (_outline ??= new Lazy<Outline.OutlineItem[]?>(() => NyxCompiler.Outline.ofSource(FileName, Text)?.Value.ToArray())).Value;


    /// <summary>
    /// The LSP diagnostics of this text's compile, for the document at <paramref name="uri"/> and for the files it
    /// imports (see <see cref="DiagnosticsPublisher.Convert"/>). Converted once, when first asked for, so that the info
    /// view and code actions, asked on every move of the cursor, neither convert them again nor read other files from
    /// disk.
    /// </summary>
    public ConvertedDiagnostics ConvertedDiagnostics(DocumentUri uri) =>
        (_converted ??= new Lazy<ConvertedDiagnostics>(() => DiagnosticsPublisher.Convert(uri, this))).Value;

    /// <summary>
    /// Where this text differs from its formatted text (see <see cref="Nanyx.LanguageServer.FormattingHints"/>), worked
    /// out once, when first asked for, by publishing its diagnostics or by a request for code actions.
    /// </summary>
    public IReadOnlyList<FormattingHint> FormattingHints(DocumentUri uri, ILogger logger) =>
        (_formattingHints ??= new Lazy<IReadOnlyList<FormattingHint>>(() => Nanyx.LanguageServer.FormattingHints.Of(uri, this, logger))).Value;

    /// <summary>
    /// The formatter's settings refactorings lay out what they write with (see <see cref="Handlers.FormattingHandler.SettingsFor"/>),
    /// or the defaults where they can't be read. Read once, when first asked for, so that code actions, asked on every
    /// move of the cursor, do not read the package's manifest each time.
    /// </summary>
    public Nanyx.Formatter.Formatter.Options RefactoringOptions(DocumentUri uri, ILogger logger) =>
        (_refactoringOptions ??= new Lazy<Nanyx.Formatter.Formatter.Options>(() =>
            Handlers.FormattingHandler.SettingsFor(this, uri, logger) ?? Nanyx.Formatter.Formatter.OptionsModule.defaults)).Value;

    /// <summary>The LSP diagnostics of this text's compile published on the document itself, at <paramref name="uri"/>.</summary>
    public IReadOnlyList<OmniSharp.Extensions.LanguageServer.Protocol.Models.Diagnostic> PublishedDiagnostics(DocumentUri uri) =>
        ConvertedDiagnostics(uri).Own;

    /// <summary>
    /// Reads the doc comments of the names in <see cref="Compiled"/>, for a request at <paramref name="offset"/>,
    /// with each link in them made a link to where the name it names is declared (see
    /// <see cref="SourceLocations.LinkTo"/>). The document must have been typed. Its own doc comments are those the
    /// compile found in <see cref="Text"/>.
    /// </summary>
    public Documentation.Reader DocReader(DocumentUri uri, int offset, StandardLibrarySource standardLibrary) =>
        new(FileName, Compiled!.DocComments.Value, Compiled, offset, span => SourceLocations.LinkTo(uri, this, span, standardLibrary)!);

    /// <summary>
    /// Whether <see cref="Compiled"/> has the types and symbols editor features read for the whole text. A text with
    /// type or name errors has them: the type checker types what did check, and gives what did not the type
    /// <c>???</c>. A text whose imports do not check has none, and one with syntax errors has them only for the items
    /// that parsed (see <see cref="PartlyTyped"/>).
    /// </summary>
    public bool Typed => PartlyTyped && !Compiler.hasSyntaxErrors(Compiled!);

    /// <summary>
    /// Whether <see cref="Compiled"/> has types and symbols, if only for the items of a text with syntax errors that
    /// parsed: the parser leaves out an item with a mistake in it and reads on.
    /// </summary>
    public bool PartlyTyped => Compiled is { Syntax: not null, Typed: not null };

    /// <summary>
    /// The text of this document that hover, type hints and completion read: this one if it was typed, or else the
    /// last one that was, since while the user is typing the text often does not parse, and the item being typed is
    /// left out of this one's compile. With no text typed before, this one if it was <see cref="PartlyTyped"/>, so a
    /// file opened with a mistake in it has the types of the items around the mistake. <c>null</c> if no text of it
    /// has been typed. Diagnostics always come from this text's own compile.
    /// </summary>
    public OpenDocument? LastGood => Typed ? this : PreviousGood ?? (PartlyTyped ? this : null);

    /// <summary>
    /// The offset in <see cref="LastGood"/>'s text of the character at <paramref name="offset"/> in this one, or
    /// <c>null</c> if there is no last good text, or the character is in a top-level item edited since: what that
    /// compile says about the item may no longer be true.
    /// </summary>
    public int? ToLastGood(int offset)
    {
        if (LastGood is not { } lastGood)
        {
            return null;
        }

        if (ReferenceEquals(lastGood, this))
        {
            return offset;
        }

        var edits = EditsSinceLastGood(lastGood);
        return edits.ToBefore(offset) is { } before && !InEditedItem(lastGood, before, before, edits) ? before : null;
    }

    /// <summary>
    /// The offsets in this text of a span in <see cref="LastGood"/>'s, or <c>null</c> if the span, or the top-level
    /// item it is in, has been edited since.
    /// </summary>
    public (int Start, int End)? FromLastGood(int start, int end)
    {
        if (LastGood is not { } lastGood)
        {
            return null;
        }

        if (ReferenceEquals(lastGood, this))
        {
            return (start, end);
        }

        var edits = EditsSinceLastGood(lastGood);
        return InEditedItem(lastGood, start, end, edits) ? null : edits.ToAfter(start, end);
    }

    /// <summary>
    /// The offset in <see cref="LastGood"/>'s text nearest to <paramref name="offset"/> in this one, for asking
    /// which names are in scope there rather than what anything's type is: an offset in text edited since maps to
    /// where the edit starts, which is in the same scope unless the edit opened or closed one. <c>null</c> if no
    /// text of the document has been typed.
    /// </summary>
    public int? NearestInLastGood(int offset)
    {
        if (LastGood is not { } lastGood)
        {
            return null;
        }

        if (ReferenceEquals(lastGood, this))
        {
            return offset;
        }

        var edits = EditsSinceLastGood(lastGood);
        return edits.ToBefore(offset) ?? edits.Start;
    }

    private EditedRegion EditsSinceLastGood(OpenDocument lastGood) =>
        _editsSinceLastGood ??= EditedRegion.Between(lastGood.Positions.NormalizedText, Positions.NormalizedText);

    /// <summary>Whether the top-level item of <paramref name="lastGood"/> around a span has been edited since.</summary>
    private static bool InEditedItem(OpenDocument lastGood, int start, int end, EditedRegion edits) =>
        lastGood.Compiled!.Syntax.Value.Any(item =>
            item.Span.Start >= 0
            && item.Span.Start <= start
            && end <= item.Span.End
            && edits.Touches(item.Span.Start, item.Span.End));
}

/// <summary>
/// Every document the client has open, keyed by URI, with the last compile of its text and the last one that was
/// typed. A document is compiled when it is opened, and after a change once the user pauses (see
/// <see cref="CompileScheduler"/>); diagnostics, hover and type hints all read that result.
/// </summary>
public sealed class DocumentStore
{
    /// <summary>Compares the paths of files as the file system does: ignoring case on Windows.</summary>
    internal static readonly StringComparer FilePaths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly ConcurrentDictionary<DocumentUri, OpenDocument> _documents = new();
    private readonly ILogger<DocumentStore> _logger;
    private readonly ServerSettings _settings;

    public DocumentStore(ILogger<DocumentStore> logger, ServerSettings settings)
    {
        _logger = logger;
        _settings = settings;
    }

    /// <summary>
    /// Records a document's new text before it is compiled, so that requests about it read positions in this text,
    /// and what they need from a compile from the last text that was typed (see <see cref="OpenDocument.LastGood"/>).
    /// </summary>
    public void Edit(DocumentUri uri, string text) =>
        Record(uri, new OpenDocument(text, FileNameOf(uri), null, null) { Sources = TryGet(uri)?.Sources ?? new Dictionary<string, string>() });

    /// <summary>Records a compiled text of a document, as its latest.</summary>
    public OpenDocument Record(DocumentUri uri, OpenDocument document)
    {
        if (!document.Typed && TryGet(uri)?.LastGood is { Typed: true } lastGood)
        {
            document = document with { PreviousGood = lastGood };
        }

        _documents[uri] = document;
        return document;
    }

    /// <summary>Forgets a document the client has closed.</summary>
    public void Remove(DocumentUri uri) => _documents.TryRemove(uri, out _);

    /// <summary>An open document, or <c>null</c> if the client has not opened it.</summary>
    public OpenDocument? TryGet(DocumentUri uri) => _documents.TryGetValue(uri, out var document) ? document : null;

    /// <summary>
    /// The open documents whose last compile read the file at the full path <paramref name="file"/> (see
    /// <see cref="OpenDocument.Sources"/>): those that import it, directly or through other modules.
    /// </summary>
    public IReadOnlyList<DocumentUri> Importers(string file) =>
        _documents.Where(entry => entry.Value.Sources.Keys.Contains(file, FilePaths)).Select(entry => entry.Key).ToList();

    /// <summary>
    /// The open document of the file at the full path <paramref name="file"/>, with the URI the client opened it by,
    /// or <c>null</c> if none is open.
    /// </summary>
    public (DocumentUri Uri, OpenDocument Document)? TryGetFile(string file) =>
        file.Length != 0 && _documents.FirstOrDefault(entry => FilePaths.Equals(entry.Value.FileName, file)) is { Value: not null } entry
            ? (entry.Key, entry.Value)
            : null;

    /// <summary>
    /// Compiles every source file of the package the file at the full path <paramref name="file"/> is a source file
    /// of, as <c>nanyx build</c> checks them, with the editor's text of each open one (see
    /// <c>Package.compilePackageReading</c>), or with the texts <paramref name="edited"/> gives for some of them: what
    /// find references and rename search. Each file comes with its full path and the text compiled. <c>null</c> for a
    /// file in no package, and if the compiler throws.
    /// </summary>
    public IReadOnlyList<NyxCompiler.Refactorings.Rename.ModuleFile>? CompilePackage(string file, IReadOnlyDictionary<string, string>? edited = null)
    {
        if (file.Length == 0)
        {
            return null;
        }

        try
        {
            var read = new Dictionary<string, string>(FilePaths);
            var stopwatch = Stopwatch.StartNew();
            var results = PackageModule.compilePackageReading(
                SeededPackages.user(),
                Microsoft.FSharp.Core.FuncConvert.FromFunc((string other) =>
                    edited is not null && edited.TryGetValue(other, out var text) ? read[other] = text : ReadFile(other, read)),
                file);
            if (results is null)
            {
                return null;
            }

            _logger.LogInformation("Compiled the package of {File} ({Count} files) in {Seconds:0.000}s", file, results.Value.Length, stopwatch.Elapsed.TotalSeconds);
            return results.Value
                .Select(result => new NyxCompiler.Refactorings.Rename.ModuleFile(Path.GetFullPath(result.Item1), read[result.Item1], result.Item2))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The compiler failed on the package of {File}", file);
            return null;
        }
    }

    /// <summary>
    /// The package of the file at <paramref name="file"/> compiled again with <paramref name="texts"/>, each a full path
    /// and its new text: the compile of each of them, in the order they are given. <c>null</c> if the compiler fails, or
    /// one of them is not a source file of the package.
    /// </summary>
    public IReadOnlyList<NyxCompiler.CompileResult>? RecompilePackage(string file, IReadOnlyList<(string Path, string Text)> texts)
    {
        if (CompilePackage(file, texts.ToDictionary(entry => entry.Path, entry => entry.Text, FilePaths)) is not { } compiled)
        {
            return null;
        }

        var byPath = compiled.ToDictionary(entry => entry.Path, entry => entry.Result, FilePaths);
        return texts.All(entry => byPath.ContainsKey(entry.Path)) ? texts.Select(entry => byPath[entry.Path]).ToList() : null;
    }

    /// <summary>The file the compiler's spans name for a document: its full path, or empty if it is not on disk.</summary>
    internal static string FileNameOf(DocumentUri uri) =>
        uri.Scheme == "file" ? Path.GetFullPath(uri.GetFileSystemPath()) : string.Empty;

    /// <summary>
    /// The text of another source file a compile needs, by its full path: the editor's, if the file is open, so that
    /// diagnostics follow what is on screen, or else the file's on disk. It is recorded in <paramref name="read"/>.
    /// </summary>
    private string ReadFile(string file, Dictionary<string, string> read)
    {
        var text = TryGetFile(file)?.Document.Text ?? File.ReadAllText(file);
        read[file] = text;
        return text;
    }

    /// <summary>
    /// Compiles a text of a document, without recording it, and logs how long it took, with each module's time when
    /// <see cref="ServerSettings.Timings"/> asks for them.
    /// </summary>
    public OpenDocument Compile(DocumentUri uri, string text)
    {
        _settings.CompileStarted?.Invoke(uri);
        // A file on disk gets its imports resolved through its package's modules if it is a source file of one, or
        // else relative to itself, and through the packages its ::install lines name; anything else (an untitled
        // buffer, say) can still import the standard library. A file of the standard library is checked as the
        // library module it is, as a build of the library checks it. The compiler names a file by its full path.
        var path = uri.Scheme == "file" ? FileNameOf(uri) : null;
        var read = new Dictionary<string, string>(FilePaths);
        if (path is not null && path.EndsWith(ManifestModule.extension, StringComparison.OrdinalIgnoreCase))
        {
            // A package's manifest is data, not a module: it is checked, not compiled.
            return new OpenDocument(text, path, PackageModule.compileManifestFrom(SeededPackages.user(), path, text), null) { Sources = read };
        }

        try
        {
            var store = _settings.PackageStore();
            // The index is left out of a compile, which runs as the text is typed: the fix that writes a package's
            // newest version is made when it is asked for (see CodeActionHandler).
            var directives = Script.readFromWithoutIndex(store, path ?? string.Empty, text);
            var linters = _settings.LinterOptionsFor(path, directives);
            var stopwatch = Stopwatch.StartNew();
            var timings = new CompileTimings();
            var result = path is null
                ? Compiler.compileReporting(timings, linters, text)
                : Compiler.compileStandardLibraryFileReporting(timings, linters, path, text)?.Value
                    ?? Script.compileSourceReading(store, timings, Microsoft.FSharp.Core.FuncConvert.FromFunc((string file) => ReadFile(file, read)), directives, linters, path, text);
            LogCompiled(uri, stopwatch.Elapsed, timings);
            return new OpenDocument(text, path ?? string.Empty, Script.withDirectives(directives, result), null) { Sources = read };
        }
        catch (Exception ex)
        {
            // A compiler bug must not take the server down with it: it is reported as a diagnostic instead.
            _logger.LogError(ex, "The compiler failed on {Uri}", uri);
            return new OpenDocument(text, path ?? string.Empty, null, ex) { Sources = read };
        }
    }

    /// <summary>
    /// Logs that a document compiled in <paramref name="elapsed"/>: one line, for the editor's output channel to show
    /// how long each compile takes, with the modules' times under it when the server was started with
    /// <c>--timings</c>.
    /// </summary>
    private void LogCompiled(DocumentUri uri, TimeSpan elapsed, CompileTimings timings)
    {
        if (_settings.Timings)
        {
            var tree = ModuleCheckModule.tree(Microsoft.FSharp.Core.FuncConvert.FromFunc((string name) => name), timings.Checks);
            _logger.LogInformation("Compiled {Uri} in {Seconds:0.000}s\n{Tree}", uri, elapsed.TotalSeconds, tree);
        }
        else
        {
            _logger.LogInformation("Compiled {Uri} in {Seconds:0.000}s", uri, elapsed.TotalSeconds);
        }
    }
}
