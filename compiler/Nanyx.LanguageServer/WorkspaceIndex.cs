using System.Collections.Concurrent;
using Nanyx.Formatter;
using NyxCompiler;

namespace Nanyx.LanguageServer;

/// <summary>
/// What each <c>.nyx</c> file under the workspace folders declares at its top level, for Go to Symbol in Workspace:
/// each file only parsed, never type-checked (see <c>NyxCompiler.Outline</c>). The folders are scanned when first
/// asked for, skipping the folders <c>Nanyx.Formatter.SourceFiles</c> skips, and each file is parsed when
/// first needed, and again after the file watcher reports it changed (see <see cref="Handlers.WatchedFilesHandler"/>).
/// </summary>
public sealed class WorkspaceIndex
{
    /// <summary>A file's text as last read and its outline, parsed when first asked for; null if it does not parse.</summary>
    private sealed class Entry(string path)
    {
        private readonly Lazy<(string Text, Outline.OutlineItem[]? Outline)> _parsed = new(() =>
        {
            var text = ReadOrEmpty(path);
            return (text, NyxCompiler.Outline.ofSource(path, text)?.Value.ToArray());
        });

        public string Text => _parsed.Value.Text;

        public Outline.OutlineItem[]? Outline => _parsed.Value.Outline;
    }

    private readonly ConcurrentDictionary<string, Entry> _files = new(DocumentStore.FilePaths);
    private readonly object _scanLock = new();
    private List<string>? _folders;

    /// <summary>
    /// The workspace's files, by full path, with their text and outline, scanning <paramref name="folders"/> the first
    /// time it is asked. A file that does not parse is left out.
    /// </summary>
    public IEnumerable<(string Path, string Text, Outline.OutlineItem[] Outline)> Files(Func<IEnumerable<string>> folders)
    {
        lock (_scanLock)
        {
            if (_folders is null)
            {
                _folders = folders().Select(Path.GetFullPath).ToList();
                foreach (var file in _folders.SelectMany(SourceFiles.under))
                {
                    _files.TryAdd(file, new Entry(file));
                }
            }
        }

        foreach (var (path, entry) in _files)
        {
            if (entry.Outline is { } outline)
            {
                yield return (path, entry.Text, outline);
            }
        }
    }

    /// <summary>
    /// Records that the file at the full path <paramref name="file"/> was made or changed (it is parsed again when
    /// next needed), or, when <paramref name="deleted"/>, that it is gone. Until the folders are scanned there is
    /// nothing to record: the scan reads every file as it is. A file outside the folders, or in a skipped folder under
    /// one, is not added.
    /// </summary>
    public void FileChanged(string file, bool deleted)
    {
        lock (_scanLock)
        {
            if (_folders is null)
            {
                return;
            }

            if (deleted)
            {
                _files.TryRemove(file, out _);
            }
            else if (file.EndsWith(".nyx", StringComparison.OrdinalIgnoreCase) && SourceFiles.isUnder(_folders, file))
            {
                _files[file] = new Entry(file);
            }
        }
    }

    private static string ReadOrEmpty(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}
