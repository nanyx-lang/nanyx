using System.Reflection;
using NyxCompiler;

namespace Nanyx.LanguageServer;

/// <summary>
/// Files on disk holding the standard library's source, so the editor can open a definition in it.
/// </summary>
/// <remarks>
/// The library is embedded in the compiler, and a declaration in it is in a file named by its import path
/// (<c>nanyx/text.nyx</c>), which is not a file on disk. The first time a module is asked for, its embedded source is
/// written, read-only, under a directory per compiler version, and that file is what the editor opens. The text is
/// the embedded text exactly, so a declaration's line and column apply to it as they are. A plain file works in
/// every editor, with nothing for the client to provide.
/// </remarks>
public sealed class StandardLibrarySource
{
    private readonly string _root;
    private readonly object _lock = new();

    /// <summary>Writes the library under the user's local application data, by compiler version.</summary>
    public StandardLibrarySource()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "nanyx",
            "stdlib",
            CompilerVersion))
    {
    }

    /// <summary>Writes the library under <paramref name="root"/>.</summary>
    public StandardLibrarySource(string root)
    {
        _root = root;
    }

    /// <summary>
    /// The file on disk with the source of the standard-library module at <paramref name="importPath"/>
    /// (<c>nanyx/text.nyx</c>), or null when the path does not name one.
    /// </summary>
    public string? TryGetPath(string importPath)
    {
        var prefix = StandardLibrary.importPrefix;
        if (!importPath.StartsWith(prefix, StringComparison.Ordinal) || !importPath.EndsWith(".nyx", StringComparison.Ordinal))
        {
            return null;
        }

        var moduleName = importPath[prefix.Length..^".nyx".Length];
        if (StandardLibrary.tryLoadSource(moduleName) is not { } source)
        {
            return null;
        }

        var path = Path.Combine(_root, StandardLibrary.packageName, moduleName + ".nyx");
        lock (_lock)
        {
            // A file left by a build of the same version with a different library is rewritten.
            if (!File.Exists(path) || File.ReadAllText(path) != source.Value)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                }

                File.WriteAllText(path, source.Value);
                // Read-only, since an edit would change nothing the compiler reads.
                File.SetAttributes(path, FileAttributes.ReadOnly);
            }
        }

        return path;
    }

    private static string CompilerVersion =>
        typeof(StandardLibrary).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(StandardLibrary).Assembly.GetName().Version?.ToString()
        ?? "dev";
}
