using Microsoft.Extensions.Logging;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using Newtonsoft.Json.Linq;
using NyxCompiler.Refactorings;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Window;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Nanyx.LanguageServer.Handlers;

/// <summary>
/// Offers the fixes of the diagnostics in the requested range as quick fixes: each fix's edits, applied to the
/// document, with the diagnostic it resolves as published. A diagnostic's first fix is the preferred one. Linters
/// offer fixes (removing an unused local, renaming an unused name to <c>_</c>), and the compiler offers the import
/// an unknown name needs, from another module of the package, a package it depends on, or the standard library
/// (<c>NyxCompiler.ImportFixes</c>), which a request for refactorings alone lists as refactorings too. A fix that would edit another
/// file is not offered. It also offers the refactorings at the range (see <c>NyxCompiler.Refactorings</c>), in a
/// document whose text type-checked: writing the inferred type of the name at its start, or of the <c>_</c>s in an annotation, inlining the def or type alias it names,
/// converting the call or pipe it names to the other, the lambda it is in to its shorthand or back, the match or <c>if</c> whose keyword it is on to the other, the <c>++</c> chain or interpolated string it is in to the other, implementing a context for the type whose definition it names or making that type protected or private, introducing a local or a function for what it selects, and a type
/// alias for a type it selects; and
/// organising the document's imports, which is also a quick fix of an unused import. Each is laid out with the
/// document's formatter settings, and only the kinds a request asks for are computed. One the compiler refuses there is
/// offered disabled, with its reason. One that makes up a name has the <see cref="RenameAtCommand"/> command, which
/// starts renaming it once its edits are made. Where formatting would change the lines in the range, it offers to
/// format them, or the whole document (see <see cref="FormattingHints"/>), even in a text that did not compile.
/// <para>
/// A refactoring that has to compile the document again to make its edits, or to know whether it is refused
/// (<c>Offer.Deferred</c>), is offered without its edits to a client that can resolve them, and compiled only when the
/// user picks it, in <c>codeAction/resolve</c>: a request, sent on every move of the cursor, compiles nothing. One
/// refused then has no edits, and its reason is shown as a message.
/// </para>
/// </summary>
public sealed class CodeActionHandler : CodeActionHandlerBase
{
    private readonly DocumentStore _documents;
    private readonly ServerSettings _settings;
    private readonly ILanguageServerFacade _server;
    private readonly ILogger<CodeActionHandler> _logger;

    /// <summary>Whether the client resolves an action's edits when it is picked (<c>codeAction/resolve</c>).</summary>
    private bool _clientResolvesEdits;

    public CodeActionHandler(DocumentStore documents, ServerSettings settings, ILanguageServerFacade server, ILogger<CodeActionHandler> logger)
    {
        _documents = documents;
        _settings = settings;
        _server = server;
        _logger = logger;
    }

    public override Task<CommandOrCodeActionContainer?> Handle(CodeActionParams request, CancellationToken cancellationToken) =>
        Task.FromResult(Compute(request));

    public override Task<CodeAction> Handle(CodeAction request, CancellationToken cancellationToken) =>
        Task.FromResult(Resolve(request));

    private CommandOrCodeActionContainer? Compute(CodeActionParams request)
    {
        var uri = request.TextDocument.Uri;
        if (_documents.TryGet(uri) is not { } document)
        {
            return null;
        }

        var only = request.Context?.Only?.ToList();
        var quickFixes = Wanted(only, CodeActionKind.QuickFix);
        // Formatting needs only a parse, so its fixes are offered whether or not the text compiled.
        var formatting = quickFixes && _settings.FormattingHints
            ? FormattingActions(uri, document, request.Range)
            : [];
        if (document.Compiled is not { } compiled)
        {
            return new CommandOrCodeActionContainer(formatting.Select(action => new CommandOrCodeAction(action)));
        }

        var start = document.Positions.ToOffset(request.Range.Start);
        var end = document.Positions.ToOffset(request.Range.End);

        // Each published diagnostic with the compiler's it was made from: the one at an import that sums up the errors
        // the import brings in was made from none, and has no fixes of its own (#3048).
        var converted = document.ConvertedDiagnostics(uri);
        var published = converted.Own
            .Zip(converted.Origins)
            .Where(pair => pair.Second is { } origin && FSharpOption<Parser.Program.SourceSpan>.get_IsSome(origin.Span))
            .Select(pair => (Origin: pair.Second!, Span: pair.Second!.Span.Value, Published: pair.First))
            .ToList();
        var actions = published
            .Where(_ => quickFixes)
            .Where(pair => pair.Span.File == document.FileName && Overlaps(pair.Span, start, end))
            .SelectMany(pair => pair.Origin.Fixes.Select((fix, index) => ToAction(uri, document, fix, pair.Published, isPreferred: index == 0)))
            .OfType<CodeAction>()
            .ToList();
        actions.AddRange(NewestVersionActions(uri, document, published.Where(_ => quickFixes).Where(pair => pair.Span.File == document.FileName && Overlaps(pair.Span, start, end))));
        var refactorings = Offers(uri, document, compiled, start, end, only)
            .Select(offer => Offered(uri, document, offer, start, end))
            .ToList();

        // Organising imports is a fix for an unused import, too.
        var unusedImports = published
            .Where(pair => Overlaps(pair.Span, start, end))
            .Select(pair => pair.Published)
            .Where(diagnostic => diagnostic.Code?.String?.EndsWith("/" + NyxCompiler.UnusedImports.rule.Id) == true)
            .ToList();
        if (unusedImports.Count > 0 && quickFixes && document.Typed)
        {
            foreach (var organise in OrganiseImports.at(document.RefactoringOptions(uri, _logger), document.FileName, document.Text, compiled, Recompile(uri)))
            {
                if (organise is not Offer.Refused)
                {
                    actions.Add(Offered(uri, document, organise, start, end) with
                    {
                        Kind = CodeActionKind.QuickFix,
                        Diagnostics = new Container<Diagnostic>(unusedImports)
                    });
                }
            }
        }

        return new CommandOrCodeActionContainer(actions.Concat(formatting).Concat(refactorings).Select(action => new CommandOrCodeAction(action)));
    }

    /// <summary>
    /// The fix that writes the newest version the index lists of a package a script's <c>::install</c> names with no
    /// version, for each of <paramref name="diagnostics"/> that says so. A compile doesn't ask the index (see
    /// <c>Script.readFromWithoutIndex</c>), so it is looked up here, when the fixes at the diagnostic are asked for, and
    /// kept for five minutes (#2223).
    /// </summary>
    private IEnumerable<CodeAction> NewestVersionActions(
        DocumentUri uri,
        OpenDocument document,
        IEnumerable<(NyxCompiler.Diagnostic Origin, Parser.Program.SourceSpan Span, Diagnostic Published)> diagnostics)
    {
        var unversioned = diagnostics
            .Where(pair => pair.Origin.Code == NyxCompiler.DiagnosticCodes.InvalidDirective.Code && pair.Origin.Fixes.IsEmpty)
            .ToList();
        if (unversioned.Count == 0 || document.FileName.Length == 0)
        {
            return [];
        }

        var fixes = NyxCompiler.Script.newestVersionFixes(_settings.PackageStore(), document.FileName, document.Text);
        return unversioned
            .SelectMany(pair => fixes
                .Where(fix => fix.Item1.Start == pair.Span.Start && fix.Item1.End == pair.Span.End)
                .Select(fix => ToAction(uri, document, fix.Item2, pair.Published, isPreferred: true)))
            .OfType<CodeAction>();
    }

    /// <summary>
    /// The action for <paramref name="offer"/>, made at the range from <paramref name="start"/> to <paramref name="end"/>:
    /// a deferred one without its edits, which <see cref="Resolve"/> makes, if the client resolves them, or else decided now.
    /// </summary>
    private CodeAction Offered(DocumentUri uri, OpenDocument document, Offer offer, int start, int end)
    {
        if (offer is not Offer.Deferred deferred)
        {
            return ToAction(uri, document, offer);
        }

        if (!_clientResolvesEdits)
        {
            return ToAction(uri, document, OfferModule.decided(offer));
        }

        return new CodeAction
        {
            Title = deferred.title,
            Kind = KindOf(deferred.kind),
            Data = new JObject
            {
                ["uri"] = uri.ToString(),
                ["kind"] = KindOf(deferred.kind).ToString(),
                ["text"] = document.Text.GetHashCode(),
                ["start"] = start,
                ["end"] = end
            }
        };
    }

    /// <summary>
    /// <paramref name="request"/>, a deferred refactoring the user picked, with its edits: the offers at its range are
    /// found again, which compiles nothing, and the one of its title and kind decided. Unchanged if the document has
    /// changed since it was offered; refused, it has no edits, and its reason is shown.
    /// </summary>
    private CodeAction Resolve(CodeAction request)
    {
        if (request.Data is not JObject data
            || data["uri"]?.Value<string>() is not { } uriText
            || DocumentUri.Parse(uriText) is not { } uri
            || _documents.TryGet(uri) is not { Compiled: { } compiled } document
            || data["text"]?.Value<int>() != document.Text.GetHashCode())
        {
            return request;
        }

        var start = data["start"]!.Value<int>();
        var end = data["end"]!.Value<int>();
        var offer = Offers(uri, document, compiled, start, end, only: null)
            .OfType<Offer.Deferred>()
            .FirstOrDefault(offer => offer.title == request.Title && KindOf(offer.kind).ToString() == data["kind"]?.Value<string>());
        if (offer is null)
        {
            return request;
        }

        var decided = OfferModule.decided(offer);
        if (decided is Offer.Refused refused)
        {
            _server.Window.ShowWarning($"{refused.title}: {refused.reason}");
            return request;
        }

        var action = ToAction(uri, document, decided);
        return request with { Edit = action.Edit, Command = action.Command, Data = null };
    }

    /// <summary>
    /// For each formatting hint that touches <paramref name="range"/>, a quick fix that formats its lines, and, if there
    /// is one, a quick fix that formats the whole document, resolving every hint (see <see cref="FormattingHints"/>).
    /// </summary>
    private List<CodeAction> FormattingActions(DocumentUri uri, OpenDocument document, Range range)
    {
        var hints = document.FormattingHints(uri, _logger);
        var touched = hints.Where(hint => Touches(hint.Diagnostic.Range, range)).ToList();
        if (touched.Count == 0)
        {
            return [];
        }

        CodeAction Fix(string title, IEnumerable<FormattingHint> fixing, bool isPreferred) =>
            new()
            {
                Title = title,
                Kind = CodeActionKind.QuickFix,
                Diagnostics = new Container<Diagnostic>(fixing.Select(hint => hint.Diagnostic)),
                IsPreferred = isPreferred,
                Edit = new WorkspaceEdit
                {
                    Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>> { [uri] = fixing.Select(hint => hint.Edit).ToList() }
                }
            };

        return [.. touched.Select(hint => Fix("Format these lines", [hint], isPreferred: true)), Fix("Format the document", hints, isPreferred: false)];
    }

    /// <summary>Whether two ranges share a position, an empty one included.</summary>
    private static bool Touches(Range a, Range b) => a.Start <= b.End && b.Start <= a.End;

    /// <summary>Whether a request for the kinds <paramref name="only"/> (all, when null) wants actions of <paramref name="kind"/>.</summary>
    private static bool Wanted(List<CodeActionKind>? only, CodeActionKind kind) =>
        only is null || only.Any(wanted => kind.ToString() == wanted.ToString() || kind.ToString().StartsWith(wanted + "."));

    /// <summary>Every source file of the document's package, compiled, when a refactoring asks for them; none for a file in no package.</summary>
    private FSharpFunc<Unit, FSharpOption<FSharpList<Rename.ModuleFile>>> PackageFiles(OpenDocument document) =>
        FuncConvert.FromFunc((Unit _) =>
            _documents.CompilePackage(document.FileName) is { } files
                ? FSharpOption<FSharpList<Rename.ModuleFile>>.Some(ListModule.OfSeq(files))
                : FSharpOption<FSharpList<Rename.ModuleFile>>.None);

    /// <summary>
    /// Compiles texts of files, each a full path and a text, as a refactoring that edits several files checks what it
    /// would make: the document's package with them, or the document alone for a file in no package.
    /// </summary>
    private FSharpFunc<FSharpList<Tuple<string, string>>, FSharpList<NyxCompiler.CompileResult>> RecompileFiles(DocumentUri uri, OpenDocument document) =>
        FuncConvert.FromFunc((FSharpList<Tuple<string, string>> texts) =>
            ListModule.OfSeq(
                texts is [var only] && only.Item1 == document.FileName && (document.FileName.Length == 0 || NyxCompiler.PackageModule.sourceFilesOf(NyxCompiler.SeededPackages.user(), document.FileName) is null)
                    ? [_documents.Compile(uri, only.Item2).Compiled ?? throw new InvalidOperationException("The compiler failed on the refactored text")]
                    : _documents.RecompilePackage(document.FileName, texts.Select(text => (text.Item1, text.Item2)).ToList())
                        ?? throw new InvalidOperationException("The compiler failed on the refactored package")));

    /// <summary>Compiles a text of the document at <paramref name="uri"/>, as a refactoring checks what it would make.</summary>
    private Microsoft.FSharp.Core.FSharpFunc<string, NyxCompiler.CompileResult> Recompile(DocumentUri uri) =>
        Microsoft.FSharp.Core.FuncConvert.FromFunc((string text) =>
            _documents.Compile(uri, text).Compiled ?? throw new InvalidOperationException("The compiler failed on the refactored text"));

    /// <summary>
    /// The refactorings at the range from <paramref name="start"/> to <paramref name="end"/> of the kinds
    /// <paramref name="only"/> (all, when null), if the document's text type-checked. Those that compile the document
    /// again are deferred.
    /// </summary>
    private IEnumerable<Offer> Offers(DocumentUri uri, OpenDocument document, NyxCompiler.CompileResult compiled, int start, int end, List<CodeActionKind>? only)
    {
        if (!document.Typed)
        {
            yield break;
        }

        var options = document.RefactoringOptions(uri, _logger);
        var recompile = Recompile(uri);
        if (Wanted(only, CodeActionKind.SourceOrganizeImports))
        {
            foreach (var organise in OrganiseImports.at(options, document.FileName, document.Text, compiled, recompile))
            {
                yield return organise;
            }
        }

        if (!Wanted(only, CodeActionKind.Refactor))
        {
            yield break;
        }

        if (WriteInferredType.at(document.FileName, document.Text, compiled, start)?.Value is { } offer)
        {
            yield return offer;
        }

        foreach (var write in WriteHoleTypes.at(options, document.FileName, document.Text, compiled, start, recompile))
        {
            yield return write;
        }

        foreach (var inline in Inline.at(options, document.FileName, document.Text, compiled, start, recompile))
        {
            yield return inline;
        }

        foreach (var inline in InlineTypeAlias.at(options, document.FileName, document.Text, compiled, start, recompile))
        {
            yield return inline;
        }

        foreach (var convert in CallAndPipe.at(options, document.FileName, document.Text, compiled, start, recompile))
        {
            yield return convert;
        }

        foreach (var convert in ShorthandLambda.at(options, document.FileName, document.Text, compiled, start, recompile))
        {
            yield return convert;
        }

        foreach (var convert in MatchAndIf.at(options, document.FileName, document.Text, compiled, start, recompile))
        {
            yield return convert;
        }

        foreach (var convert in Interpolation.at(options, document.FileName, document.Text, compiled, start, recompile))
        {
            yield return convert;
        }

        foreach (var implement in ImplementContext.at(options, document.FileName, document.Text, compiled, start, recompile))
        {
            yield return implement;
        }

        foreach (var make in MakeNominal.at(options, document.FileName, document.Text, compiled, start, PackageFiles(document), RecompileFiles(uri, document)))
        {
            yield return make;
        }

        // An unknown name's imports are its quick fixes already, so they are listed again only for a request for
        // refactorings alone, as the editor's Refactor menu makes.
        if (only is not null)
        {
            foreach (var import in ImportName.at(compiled, start))
            {
                yield return import;
            }
        }

        if (end > start)
        {
            var selection = new Parser.Program.SourceSpan(document.FileName, start, end, 0, 0);
            foreach (var extract in IntroduceLocal.at(options, document.FileName, document.Text, compiled, selection))
            {
                yield return extract;
            }

            foreach (var extract in IntroduceFunction.at(options, document.FileName, document.Text, compiled, selection, recompile))
            {
                yield return extract;
            }

            foreach (var extract in IntroduceTypeAlias.at(options, document.FileName, document.Text, compiled, selection, recompile))
            {
                yield return extract;
            }
        }
    }

    /// <summary>
    /// The client-side command that starts renaming the name at a position of a document, which the editor's extension
    /// registers: a refactoring that makes up a name runs it after its edits, so the name can be given at once.
    /// </summary>
    public const string RenameAtCommand = "nanyx.renameAt";

    /// <summary>
    /// The action for <paramref name="offer"/>, which is decided. An edit of another file than the document, which a
    /// refactoring across the package makes, is placed in that file's text: the editor's, if it is open.
    /// </summary>
    private CodeAction ToAction(DocumentUri uri, OpenDocument document, Offer offer) =>
        offer switch
        {
            Offer.Available { Item: var refactoring } => new CodeAction
            {
                Title = refactoring.Title,
                Kind = KindOf(refactoring.Kind),
                Edit = new WorkspaceEdit
                {
                    Changes = refactoring.Edits
                        .GroupBy(edit => edit.Span.File == document.FileName || edit.Span.File.Length == 0 ? document.FileName : edit.Span.File, DocumentStore.FilePaths)
                        .ToDictionary(
                            file => file.Key == document.FileName ? uri : _documents.TryGetFile(file.Key)?.Uri ?? DocumentUri.FromFileSystemPath(file.Key),
                            file =>
                            {
                                var positions = file.Key == document.FileName
                                    ? document.Positions
                                    : new SourcePositions(_documents.TryGetFile(file.Key)?.Document.Text ?? File.ReadAllText(file.Key));
                                return (IEnumerable<TextEdit>)file.Select(edit => new TextEdit
                                {
                                    Range = positions.ToRange(edit.Span.Start, edit.Span.End),
                                    NewText = edit.NewText
                                }).ToList();
                            })
                },
                Command = refactoring.Rename?.Value is { } rename ? RenameAt(uri, document, refactoring, rename) : null
            },
            Offer.Refused refused => new CodeAction
            {
                Title = refused.title,
                Kind = KindOf(refused.kind),
                Disabled = new CodeActionDisabled { Reason = refused.reason }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(offer))
        };

    /// <summary>The command that starts renaming the name at <paramref name="offset"/> of the text <paramref name="refactoring"/> makes.</summary>
    private static Command RenameAt(DocumentUri uri, OpenDocument document, Refactoring refactoring, int offset)
    {
        var text = refactoring.Edits
            .OrderByDescending(edit => edit.Span.Start)
            .Aggregate(document.Positions.NormalizedText, (text, edit) => text[..edit.Span.Start] + edit.NewText + text[edit.Span.End..]);
        var position = new SourcePositions(text).ToPosition(offset);
        return new Command
        {
            Name = RenameAtCommand,
            Title = "Rename",
            Arguments = new Newtonsoft.Json.Linq.JArray(uri.ToString(), position.Line, position.Character)
        };
    }

    private static CodeActionKind KindOf(RefactoringKind kind) =>
        kind.Tag switch
        {
            RefactoringKind.Tags.Extract => CodeActionKind.RefactorExtract,
            RefactoringKind.Tags.Rewrite => CodeActionKind.RefactorRewrite,
            RefactoringKind.Tags.Inline => CodeActionKind.RefactorInline,
            RefactoringKind.Tags.OrganiseImports => CodeActionKind.SourceOrganizeImports,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    /// <summary>Whether a span touches the range from <paramref name="start"/> to <paramref name="end"/>.</summary>
    private static bool Overlaps(Parser.Program.SourceSpan span, int start, int end) =>
        span.Start <= end && start <= span.End;

    /// <summary>The quick fix for <paramref name="fix"/>, or <c>null</c> if it edits a file other than the document.</summary>
    private static CodeAction? ToAction(
        DocumentUri uri,
        OpenDocument document,
        NyxCompiler.Fix fix,
        Diagnostic diagnostic,
        bool isPreferred) =>
        FixEdit(uri, document, fix) is { } edit
            ? new CodeAction
            {
                Title = fix.Title,
                Kind = CodeActionKind.QuickFix,
                Diagnostics = new Container<Diagnostic>(diagnostic),
                IsPreferred = isPreferred,
                Edit = edit
            }
            : null;

    /// <summary>
    /// The edit that makes <paramref name="fix"/> to the document, or <c>null</c> if it edits another file: what its
    /// quick fix, and the info view's link to it, apply.
    /// </summary>
    internal static WorkspaceEdit? FixEdit(DocumentUri uri, OpenDocument document, NyxCompiler.Fix fix)
    {
        if (fix.Edits.Any(edit => edit.Span.File != document.FileName))
        {
            return null;
        }

        var edits = fix.Edits.Select(edit => new TextEdit
        {
            Range = document.Positions.ToRange(edit.Span.Start, edit.Span.End),
            NewText = edit.NewText
        });
        return new WorkspaceEdit { Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>> { [uri] = edits.ToList() } };
    }

    protected override CodeActionRegistrationOptions CreateRegistrationOptions(
        CodeActionCapability capability,
        ClientCapabilities clientCapabilities)
    {
        _clientResolvesEdits = capability?.ResolveSupport?.Properties.Contains("edit") == true;
        return new()
        {
            DocumentSelector = TextDocumentSyncHandler.Selector,
            CodeActionKinds = new Container<CodeActionKind>(CodeActionKind.QuickFix, CodeActionKind.RefactorExtract, CodeActionKind.RefactorInline, CodeActionKind.RefactorRewrite, CodeActionKind.SourceOrganizeImports),
            ResolveProvider = _clientResolvesEdits
        };
    }
}