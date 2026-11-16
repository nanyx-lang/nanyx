/// Every piece of Nanyx in the repository: each `.nyx` file, each `nanyx` code block in the docs, and each source
/// the compiler's tests write in a triple-quoted string. The parser's rewrite is checked against all of it (see the
/// Work in progress page "The parser").
module Nanyx.Parser.Corpus

open System
open System.IO
open System.Text.RegularExpressions

/// Where a piece of the corpus comes from.
type Origin =
    /// A `.nyx` file: the standard library, a package, an example, a scratch file or a test's input.
    | SourceFile
    /// A code block in the docs, with what its fence says after `nanyx`: `""` for a whole program, `fragment`,
    /// `skip=#<issue>` or `unformatted`.
    | DocBlock of info: string
    /// A triple-quoted string in a test project's F# or C# source. Not every one is Nanyx, and many are meant not to
    /// parse.
    | TestSource

/// A piece of the corpus: a name to report it by (a path, with a line for a part of a file), its text, and where it
/// comes from.
type Piece = { Name: string; Source: string; Origin: Origin }

/// The repository's root.
let repositoryRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

let private skipped = [ "bin"; "obj"; "node_modules"; ".git"; ".claude" ]

let private relative (path: string) = Path.GetRelativePath(repositoryRoot, path)

let private isSkipped (path: string) =
    (relative path).Split([| '/'; '\\' |]) |> Array.exists (fun part -> List.contains part skipped)

let private filesNamed (pattern: string) (directory: string) =
    Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories)
    |> Seq.filter (isSkipped >> not)
    |> Seq.sort

/// The line `index` is on in `text`, counting from 1.
let private lineAt (text: string) (index: int) = text.AsSpan(0, index).Count '\n' + 1

/// Each `.nyx` file in the repository, as it is on disk.
let files =
    lazy
        filesNamed "*.nyx" repositoryRoot
        |> Seq.map (fun path -> { Name = relative path; Source = File.ReadAllText path; Origin = SourceFile })
        |> List.ofSeq

let private codeBlock = Regex(@"^([ \t]*)```nanyx([^\n]*)\n(.*?)^[ \t]*```", RegexOptions.Singleline ||| RegexOptions.Multiline)

/// Each ```` ```nanyx ```` block in the docs, named by its file and the line of its fence. A block indented in a list
/// loses its fence's indent.
let docBlocks =
    lazy
        filesNamed "*.md" (Path.Combine(repositoryRoot, "docs", "src", "content"))
        |> Seq.collect (fun path ->
            let text = (File.ReadAllText path).ReplaceLineEndings "\n"
            codeBlock.Matches text
            |> Seq.map (fun m ->
                let indent = m.Groups[1].Value.Length
                let source =
                    m.Groups[3].Value.Split('\n')
                    |> Array.map (fun line ->
                        if line.Length >= indent && String.IsNullOrWhiteSpace(line.Substring(0, indent)) then line.Substring indent
                        else line.TrimStart())
                    |> String.concat "\n"
                { Name = $"{relative path}:{lineAt text m.Index}"
                  Source = source
                  Origin = DocBlock(m.Groups[2].Value.Trim()) }))
        |> List.ofSeq

// An interpolated string (`$"""`) is left out: its holes are F#, not Nanyx.
let private tripleQuoted = Regex("(\\$*)\"\"\"(.*?)\"\"\"", RegexOptions.Singleline)

/// Each triple-quoted string in the test projects' sources, named by its file and line.
let testSources =
    lazy
        Directory.EnumerateDirectories(Path.Combine(repositoryRoot, "compiler"), "*Test*")
        |> Seq.sort
        |> Seq.collect (fun directory -> Seq.append (filesNamed "*.fs" directory) (filesNamed "*.cs" directory))
        |> Seq.collect (fun path ->
            let text = File.ReadAllText path
            tripleQuoted.Matches text
            |> Seq.filter (fun m -> m.Groups[1].Value = "")
            |> Seq.map (fun m -> { Name = $"{relative path}:{lineAt text m.Index}"; Source = m.Groups[2].Value; Origin = TestSource }))
        |> List.ofSeq

/// The whole corpus: the files, then the docs' code blocks, then the tests' sources.
let all = lazy (files.Value @ docBlocks.Value @ testSources.Value)
