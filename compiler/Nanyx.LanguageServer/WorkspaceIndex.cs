using System.Collections.Concurrent;
using NyxCompiler;

namespace Nanyx.LanguageServer;

/// <summary>
/// What each <c>.nyx</c> file under the workspace folders declares at its top level, for Go to Symbol in Workspace:
/// each file only parsed, never type-checked (see <c>NyxCompiler.Outline</c>). The folders are scanned when first
/// asked for, skipping <c>bin</c>, <c>obj</c>, <c>node_modules</c> and hidden folders, and each file is parsed when
/// first needed, and again after the file watcher reports it changed (see <see cref="Handlers.WatchedFilesHandler"/>).
/// </summary>
public sealed class WorkspaceIndex
{
    /// <summary>The folders a scan does not go into: build output, packages and hidden folders such as <c>.git</c>.</summary>
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules" };

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
                foreach (var file in _folders.SelectMany(SourceFiles))
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
            else if (file.EndsWith(".nyx", StringComparison.OrdinalIgnoreCase) && InWorkspace(_folders, file))
            {
                _files[file] = new Entry(file);
            }
        }
    }

    /// <summary>Whether <paramref name="file"/> is under one of <paramref name="folders"/>, outside the skipped folders.</summary>
    private static bool InWorkspace(IEnumerable<string> folders, string file) =>
        folders.Any(folder =>
            Path.GetRelativePath(folder, file) is var relative
            && !relative.StartsWith("..", StringComparison.Ordinal)
            && !Path.IsPathRooted(relative)
            && !(Path.GetDirectoryName(relative) ?? "").Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(IsSkipped));

    /// <summary>Every <c>.nyx</c> file under <paramref name="folder"/>, by full path, outside the skipped folders.</summary>
    private static IEnumerable<string> SourceFiles(string folder)
    {
        var pending = new Stack<string>([folder]);
        while (pending.TryPop(out var directory))
        {
            IEnumerable<string> files, directories;
            try
            {
                files = Directory.EnumerateFiles(directory, "*.nyx").ToList();
                directories = Directory.EnumerateDirectories(directory).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return Path.GetFullPath(file);
            }

            foreach (var child in directories.Where(child => !IsSkipped(Path.GetFileName(child))))
            {
                pending.Push(child);
            }
        }
    }

    private static bool IsSkipped(string folderName) => folderName.StartsWith('.') || SkippedFolders.Contains(folderName);


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
