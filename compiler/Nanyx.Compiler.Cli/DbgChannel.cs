using System.Text.Json;
using Microsoft.FSharp.Core;
using Nanyx.Runtime;
using NyxCompiler;

/// <summary>
/// The channel <c>nanyx run --dbg-to</c> writes each <c>dbg</c> a program reaches to, for an editor to show the values
/// where they are written: one JSON object a line, flushed as each is written, and nothing of them on the terminal.
/// </summary>
internal static partial class CliCommands
{
    /// <summary>
    /// The file at <paramref name="path"/>, made anew, that <see cref="Write"/> writes each <c>dbg</c> hit to as a line:
    /// <c>{"file", "line", "column", "value", "seq"}</c>, its file a full path where the program's name for it is the name
    /// of one of the files <paramref name="compiled"/> read (see <c>PackageModules.shownFile</c>), and its sequence number
    /// counted from 0. Another process may read the file while it is written.
    /// </summary>
    private sealed class DbgChannel : IDisposable
    {
        private readonly StreamWriter writer;
        private readonly Dictionary<string, string> fullPaths;
        private int sequence;

        private DbgChannel(StreamWriter writer, Dictionary<string, string> fullPaths)
        {
            this.writer = writer;
            this.fullPaths = fullPaths;
        }

        /// <summary>The channel at <paramref name="path"/>, or why it can't be opened.</summary>
        public static DbgChannel? Open(string path, CompileResult compiled, string? packageRoot, TextWriter errors)
        {
            try
            {
                var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                return new DbgChannel(new StreamWriter(stream) { AutoFlush = true }, FullPaths(compiled, packageRoot));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.WriteLine($"error: --dbg-to can't write to {path}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Each name the program gives a file it was compiled from, with the file's full path: those of the files its code
        /// is in, its own and what it inlined (<c>PackageModules.codeFiles</c>), by <c>PackageModules.shownFile</c>. A name two of them
        /// share is left out, so that a hit in either is written with the name alone.
        /// </summary>
        private static Dictionary<string, string> FullPaths(CompileResult compiled, string? packageRoot)
        {
            var root = packageRoot is null ? FSharpOption<string>.None : FSharpOption<string>.Some(packageRoot);
            var files = compiled.Typed is null ? [] : PackageModulesModule.codeFiles(compiled.Typed.Value.Module).ToList();
            return files
                .GroupBy(file => PackageModulesModule.shownFile(root, file))
                .Where(named => named.Count() == 1)
                .ToDictionary(named => named.Key, named => Path.GetFullPath(named.Single()));
        }

        /// <summary>Writes <paramref name="hit"/> as a line, and says it is dealt with, so the program doesn't print it.</summary>
        public bool Write(DbgHit hit)
        {
            WriteJsonLine(writer, json =>
            {
                json.WriteString("file", fullPaths.TryGetValue(hit.File, out var full) ? full : hit.File);
                json.WriteNumber("line", hit.Line);
                json.WriteNumber("column", hit.Column);
                json.WriteString("value", hit.Text);
                json.WriteNumber("seq", sequence++);
            });
            return true;
        }

        public void Dispose() => writer.Dispose();
    }
}
