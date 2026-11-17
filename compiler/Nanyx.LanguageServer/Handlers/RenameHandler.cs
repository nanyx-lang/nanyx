using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using CompileResult = NyxCompiler.CompileResult;
using PackageModule = NyxCompiler.PackageModule;
using NyxCompiler.Refactorings;
using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace Nanyx.LanguageServer.Handlers;

/// <summary>
/// Renames a name and every use of it (see <c>NyxCompiler.Refactorings.Rename</c>), in a document whose text
/// type-checked: in every file of its package, compiled as <see cref="DocumentStore.CompilePackage"/> compiles them, or
/// in the document alone for a file in no package. The renamed files are compiled before the edit is given, and a
/// rename that would make any name refer to something else is refused. A refusal is an error response with its reason, which the editor shows.
/// Renaming a module renames its file too, where the file is named for it and the client can rename files.
/// </summary>
public sealed class RenameHandler : RenameHandlerBase
{
    private readonly DocumentStore _documents;

    /// <summary>Whether the client can rename a file in a <c>WorkspaceEdit</c> (<c>workspace.workspaceEdit.resourceOperations</c>).</summary>
    private bool _renamesFiles;

    public RenameHandler(DocumentStore documents)
    {
        _documents = documents;
    }

    public override Task<WorkspaceEdit?> Handle(RenameParams request, CancellationToken cancellationToken) =>
        Task.FromResult(Compute(request));

    private WorkspaceEdit? Compute(RenameParams request)
    {
        var uri = request.TextDocument.Uri;
        if (_documents.TryGet(uri) is not { Typed: true, Compiled: { } compiled } document)
        {
            return null;
        }

        var offset = document.Positions.ToOffset(request.Position);
        var package = _documents.CompilePackage(document.FileName);
        var result = package is null
            ? Rename.editsIn(
                Rename.Scope.Module,
                ListModule.OfSeq([new Rename.ModuleFile(document.FileName, document.Text, compiled)]),
                document.FileName,
                offset,
                request.NewName,
                FuncConvert.FromFunc((FSharpList<Tuple<string, string>> renamed) => ListModule.OfSeq(renamed.Select(file =>
                    _documents.Compile(uri, file.Item2).Compiled ?? throw Refused("The compiler failed on the renamed text")))))
            : Rename.editsIn(
                Rename.Scope.NewPackage(ListModule.OfSeq(package.Select(file => file.Path))),
                ListModule.OfSeq(package),
                document.FileName,
                offset,
                request.NewName,
                FuncConvert.FromFunc((FSharpList<Tuple<string, string>> renamed) => Recompiled(document.FileName, renamed)));
        if (result.IsError)
        {
            throw Refused(result.ErrorValue);
        }

        // A file's edits are placed in the text it was renamed in: the editor's, for an open file.
        var sources = package?.ToDictionary(file => file.Path, file => file.Source, DocumentStore.FilePaths)
            ?? new Dictionary<string, string>(DocumentStore.FilePaths) { [document.FileName] = document.Text };
        DocumentUri UriOf(string path) => path == document.FileName ? uri : _documents.TryGetFile(path)?.Uri ?? DocumentUri.FromFileSystemPath(path);
        var changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>>();
        foreach (var (path, edits) in result.ResultValue.Edits)
        {
            var positions = path == document.FileName ? document.Positions : new SourcePositions(sources[path]);
            changes[UriOf(path)] = edits.Select(edit => new TextEdit
            {
                Range = positions.ToRange(edit.Span.Start, edit.Span.End),
                NewText = edit.NewText
            }).ToList();
        }

        // A module's file is renamed with it only where the editor can rename files; otherwise only its text changes.
        var renamedFiles = result.ResultValue.Files;
        if (renamedFiles.IsEmpty || !_renamesFiles)
        {
            return new WorkspaceEdit { Changes = changes };
        }

        var operations = changes
            .Select(change => new WorkspaceEditDocumentChange(new TextDocumentEdit
            {
                TextDocument = new OptionalVersionedTextDocumentIdentifier { Uri = change.Key },
                Edits = new TextEditContainer(change.Value)
            }))
            .Concat(renamedFiles.Select(renamed => new WorkspaceEditDocumentChange(new RenameFile
            {
                OldUri = UriOf(renamed.Item1),
                NewUri = DocumentUri.FromFileSystemPath(renamed.Item2)
            })));
        return new WorkspaceEdit { DocumentChanges = new Container<WorkspaceEditDocumentChange>(operations) };
    }

    /// <summary>The package of <paramref name="file"/> compiled again with the renamed texts, in the order they are given.</summary>
    private FSharpList<CompileResult> Recompiled(string file, FSharpList<Tuple<string, string>> renamed) =>
        ListModule.OfSeq(_documents.RecompilePackage(file, renamed.Select(entry => (entry.Item1, entry.Item2)).ToList())
            ?? throw Refused("The compiler failed on the renamed package"));

    /// <summary>The error response for a refused rename: the request failed (<c>RequestFailed</c>, -32803), for the reason given.</summary>
    internal static Exception Refused(string reason) => new RpcErrorException(-32803, null, reason);

    protected override RenameRegistrationOptions CreateRegistrationOptions(
        RenameCapability capability,
        ClientCapabilities clientCapabilities)
    {
        var workspaceEdit = clientCapabilities.Workspace?.WorkspaceEdit.Value;
        _renamesFiles = workspaceEdit is { DocumentChanges: true }
            && workspaceEdit.ResourceOperations?.Contains(ResourceOperationKind.Rename) == true;
        return new() { DocumentSelector = TextDocumentSyncHandler.Selector, PrepareProvider = true };
    }
}

/// <summary>
/// Says what <c>textDocument/rename</c> would rename at a position: the name's own part, or, for a name that can't be
/// renamed, an error response with the reason.
/// </summary>
public sealed class PrepareRenameHandler : PrepareRenameHandlerBase
{
    private readonly DocumentStore _documents;

    public PrepareRenameHandler(DocumentStore documents)
    {
        _documents = documents;
    }

    public override Task<RangeOrPlaceholderRange?> Handle(PrepareRenameParams request, CancellationToken cancellationToken) =>
        Task.FromResult(Compute(request));

    private RangeOrPlaceholderRange? Compute(PrepareRenameParams request)
    {
        if (_documents.TryGet(request.TextDocument.Uri) is not { Typed: true, Compiled: { } compiled } document
            || Rename.prepare(ScopeOf(document.FileName), document.FileName, document.Text, compiled, document.Positions.ToOffset(request.Position))?.Value is not { } prepared)
        {
            return null;
        }

        if (prepared.IsError)
        {
            throw RenameHandler.Refused(prepared.ErrorValue);
        }

        var target = prepared.ResultValue;
        return new RangeOrPlaceholderRange(new PlaceholderRange
        {
            Range = document.Positions.ToRange(target.Span.Start, target.Span.End),
            Placeholder = target.Name
        });
    }

    /// <summary>Where a rename in <paramref name="file"/> looks for uses: its package's files, or the module alone.</summary>
    private static Rename.Scope ScopeOf(string file) =>
        file.Length != 0 && PackageModule.sourceFilesOf(file) is { } files
            ? Rename.Scope.NewPackage(files.Value)
            : Rename.Scope.Module;

    protected override RenameRegistrationOptions CreateRegistrationOptions(
        RenameCapability capability,
        ClientCapabilities clientCapabilities) =>
        new() { DocumentSelector = TextDocumentSyncHandler.Selector, PrepareProvider = true };
}
