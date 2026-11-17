namespace NyxCompiler

open System.IO

/// `nanyx new`: the first files of a package, a manifest and an `@entry` that prints a greeting.
module NewPackage =
    /// The manifest of a new package called `name`: its sources are under `src`, the official linter is on with every
    /// rule at its default, and the console host runs it. Both are the release `toolVersion` of the compiler making it.
    let manifest (name: string) (toolVersion: string) =
        let shipped package = Manifest.entryText toolVersion "" package Shipped
        $"""(
  package = (name = {Manifest.quotedString name}, version = "0.1.0", sources = ["src/**/*.nyx"])
  linters = [{shipped "nanyx.lint"}]
  host = {shipped Hosts.consolePackage}
)
"""
            .ReplaceLineEndings "\n"

    /// The new package's one source file, `src/main.nyx`.
    let entry =
        """module main

@entry
def main = {
  println("Hello, World!")
}
"""
            .ReplaceLineEndings "\n"

    /// The directory a package called `name` is made in when none is given: the name's last segment.
    let defaultDirectory (name: string) = name.Substring(name.LastIndexOf '.' + 1)

    /// Makes a package called `name` in `directory`, which must not exist yet or must be empty, with a manifest naming
    /// the compiler's release `toolVersion`, and gives the full path of its manifest. An error says why it was not made;
    /// nothing is written then.
    let create (name: string) (toolVersion: string) (directory: string) : Result<string, string> =
        let full = Path.GetFullPath directory
        if not (Manifest.namePattern.IsMatch name) then
            Result.Error $"""The package name '{name}' must be {Manifest.nameRule}"""
        elif File.Exists full then
            Result.Error $"'{full}' is a file, not a directory"
        elif Directory.Exists full && not (Seq.isEmpty (Directory.EnumerateFileSystemEntries full)) then
            match Manifest.manifestsIn full with
            | [||] -> Result.Error $"'{full}' is not empty, and a new package is made in a new or empty directory: give another directory"
            | manifests -> Result.Error $"'{full}' is already a package, with the manifest '{Path.GetFileName manifests[0]}'"
        else
            Directory.CreateDirectory(Path.Combine(full, "src")) |> ignore
            let manifestPath = Path.Combine(full, name + Manifest.extension)
            File.WriteAllText(manifestPath, manifest name toolVersion)
            File.WriteAllText(Path.Combine(full, "src", "main.nyx"), entry)
            Result.Ok manifestPath
