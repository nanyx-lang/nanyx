namespace NyxCompiler

open System.IO

/// `nanyx new`: the first files of a package, a manifest and an `@entry` that prints a greeting.
module NewPackage =
    /// The manifest of a new package called `name`: its sources are under `src`, and the official linter is on with
    /// every rule at its default.
    let manifest (name: string) =
        $"[package]\nname = \"{name}\"\nversion = \"0.1.0\"\nsources = [\"src/**/*.nyx\"]\n\n[linters.default]\n"

    /// The new package's one source file, `src/main.nyx`.
    let entry = "module main\n\n@entry\ndef main = {\n  println(\"Hello, World!\")\n}\n"

    /// The directory a package called `name` is made in when none is given: the name's last segment.
    let defaultDirectory (name: string) = name.Substring(name.LastIndexOf '.' + 1)

    /// Makes a package called `name` in `directory`, which must not exist yet or must be empty, and gives the full
    /// path of its manifest. An error says why it was not made; nothing is written then.
    let create (name: string) (directory: string) : Result<string, string> =
        let full = Path.GetFullPath directory
        if not (Manifest.namePattern.IsMatch name) then
            Result.Error $"The package name \"{name}\" must be {Manifest.nameRule}"
        elif File.Exists full then
            Result.Error $"{full} is a file, not a directory"
        elif Directory.Exists full && not (Seq.isEmpty (Directory.EnumerateFileSystemEntries full)) then
            match Directory.GetFiles(full, "*" + Manifest.extension) |> Array.sort with
            | [||] -> Result.Error $"{full} is not empty: a new package is made in a new or empty directory"
            | manifests -> Result.Error $"{full} is already a package: it has {Path.GetFileName manifests[0]}"
        else
            Directory.CreateDirectory(Path.Combine(full, "src")) |> ignore
            let manifestPath = Path.Combine(full, name + Manifest.extension)
            File.WriteAllText(manifestPath, manifest name)
            File.WriteAllText(Path.Combine(full, "src", "main.nyx"), entry)
            Result.Ok manifestPath
