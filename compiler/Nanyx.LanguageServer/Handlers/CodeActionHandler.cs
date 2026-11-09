using Microsoft.FSharp.Core;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nanyx.LanguageServer.Handlers;

/// <summary>
/// Offers the fixes of the diagnostics in the requested range as quick fixes: each fix's edits, applied to the
/// document, with the diagnostic it resolves as published. A diagnostic's first fix is the preferred one. Only
/// linters offer fixes so far (removing an unused local, renaming an unused name to <c>_</c>). A fix that would
/// edit another file is not offered.
/// </summary>
public sealed class CodeActionHandler : CodeActionHandlerBase
{
    private readonly DocumentStore _documents;

    public CodeActionHandler(DocumentStore documents)
    {
        _documents = documents;
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
        var published = DiagnosticsPublisher.Compute(uri, document);
        var actions = compiled.Diagnostics
            .Zip(published)
            .Where(pair => FSharpOption<Parser.Program.SourceSpan>.get_IsSome(pair.First.Span))
            .Where(pair => pair.First.Span.Value.File == document.FileName && Overlaps(pair.First.Span.Value, start, end))
            .SelectMany(pair => pair.First.Fixes.Select((fix, index) => ToAction(uri, document, fix, pair.Second, isPreferred: index == 0)))
            .OfType<CodeAction>()
            .Select(action => new CommandOrCodeAction(action));

        return new CommandOrCodeActionContainer(actions);
    }

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
        new() { DocumentSelector = TextDocumentSyncHandler.Selector, CodeActionKinds = new Container<CodeActionKind>(CodeActionKind.QuickFix) };
}
