using Microsoft.Extensions.Logging;
using Microsoft.FSharp.Core;
using NyxCompiler.Refactorings;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nanyx.LanguageServer.Handlers;

/// <summary>
/// Offers the fixes of the diagnostics in the requested range as quick fixes: each fix's edits, applied to the
/// document, with the diagnostic it resolves as published. A diagnostic's first fix is the preferred one. Linters
/// offer fixes (removing an unused local, renaming an unused name to <c>_</c>), and the compiler offers the import
/// an unknown name from the standard library needs (<c>NyxCompiler.ImportFixes</c>). A fix that would edit another
/// file is not offered. It also offers the refactorings at the range (see <c>NyxCompiler.Refactorings</c>), in a
/// document whose text type-checked: writing the inferred type of the name at its start, and introducing a local or a
/// function for what it selects, laid out with the document's formatter settings. One the compiler refuses there is
/// offered disabled, with its reason. One that makes up a name has the <see cref="RenameAtCommand"/> command, which
/// starts renaming it once its edits are made.
/// </summary>
public sealed class CodeActionHandler : CodeActionHandlerBase
{
    private readonly DocumentStore _documents;
    private readonly ILogger<CodeActionHandler> _logger;

    public CodeActionHandler(DocumentStore documents, ILogger<CodeActionHandler> logger)
    {
        _documents = documents;
        _logger = logger;
    }

    public override Task<CommandOrCodeActionContainer?> Handle(CodeActionParams request, CancellationToken cancellationToken) =>
        Task.FromResult(Compute(request));

    public override Task<CodeAction> Handle(CodeAction request, CancellationToken cancellationToken) =>
        Task.FromResult(request);

    private CommandOrCodeActionContainer? Compute(CodeActionParams request)
    {
        var uri = request.TextDocument.Uri;
        if (_documents.TryGet(uri) is not { Compiled: { } compiled } document)
        {
            return null;
        }

        var start = document.Positions.ToOffset(request.Range.Start);
        var end = document.Positions.ToOffset(request.Range.End);

        // Published diagnostics are the compiler's, one for one and in the same order.
        var published = document.PublishedDiagnostics(uri);
        var actions = compiled.Diagnostics
            .Zip(published)
            .Where(pair => FSharpOption<Parser.Program.SourceSpan>.get_IsSome(pair.First.Span))
            .Where(pair => pair.First.Span.Value.File == document.FileName && Overlaps(pair.First.Span.Value, start, end))
            .SelectMany(pair => pair.First.Fixes.Select((fix, index) => ToAction(uri, document, fix, pair.Second, isPreferred: index == 0)))
            .OfType<CodeAction>()
            .Concat(Refactorings(uri, document, compiled, start, end))
            .Select(action => new CommandOrCodeAction(action));

        return new CommandOrCodeActionContainer(actions);
    }

    /// <summary>The refactorings at the range from <paramref name="start"/> to <paramref name="end"/>, if the document's text type-checked.</summary>
    private IEnumerable<CodeAction> Refactorings(DocumentUri uri, OpenDocument document, NyxCompiler.CompileResult compiled, int start, int end)
    {
        if (!document.Typed)
        {
            yield break;
        }

        if (WriteInferredType.at(document.FileName, document.Text, compiled, start)?.Value is { } offer)
        {
            yield return ToAction(uri, document, offer);
        }

        if (end > start)
        {
            var options = FormattingHandler.SettingsFor(document, uri, _logger) ?? Nanyx.Formatter.Formatter.OptionsModule.defaults;
            var selection = new Parser.Program.SourceSpan(document.FileName, start, end, 0, 0);
            foreach (var extract in IntroduceLocal.at(options, document.FileName, document.Text, compiled, selection))
            {
                yield return ToAction(uri, document, extract);
            }

            var recompile = Microsoft.FSharp.Core.FuncConvert.FromFunc((string text) =>
                _documents.Compile(uri, text).Compiled ?? throw new InvalidOperationException("The compiler failed on the refactored text"));
            foreach (var extract in IntroduceFunction.at(options, document.FileName, document.Text, compiled, selection, recompile))
            {
                yield return ToAction(uri, document, extract);
            }
        }
    }

    /// <summary>
    /// The client-side command that starts renaming the name at a position of a document, which the editor's extension
    /// registers: a refactoring that makes up a name runs it after its edits, so the name can be given at once.
    /// </summary>
    public const string RenameAtCommand = "nanyx.renameAt";

    private static CodeAction ToAction(DocumentUri uri, OpenDocument document, Offer offer) =>
        offer switch
        {
            Offer.Available { Item: var refactoring } => new CodeAction
            {
                Title = refactoring.Title,
                Kind = KindOf(refactoring.Kind),
                Edit = new WorkspaceEdit
                {
                    Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>>
                    {
                        [uri] = refactoring.Edits.Select(edit => new TextEdit
                        {
                            Range = document.Positions.ToRange(edit.Span.Start, edit.Span.End),
                            NewText = edit.NewText
                        }).ToList()
                    }
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
        bool isPreferred)
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

        return new CodeAction
        {
            Title = fix.Title,
            Kind = CodeActionKind.QuickFix,
            Diagnostics = new Container<Diagnostic>(diagnostic),
            IsPreferred = isPreferred,
            Edit = new WorkspaceEdit { Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>> { [uri] = edits.ToList() } }
        };
    }

    protected override CodeActionRegistrationOptions CreateRegistrationOptions(
        CodeActionCapability capability,
        ClientCapabilities clientCapabilities) =>
        new() { DocumentSelector = TextDocumentSyncHandler.Selector, CodeActionKinds = new Container<CodeActionKind>(CodeActionKind.QuickFix, CodeActionKind.RefactorExtract, CodeActionKind.RefactorRewrite) };
}
