namespace NyxCompiler

open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes

/// The files `nanyx publish` and `nanyx yank` change in the package index's repository, as its README specifies them:
/// a package's registration, `registry/<namespace>/<name>.json`, and its index file, `index/<namespace>/<name>.json`.
module Publishing =
    let private namespaceOf (name: string) = name.Substring(0, max 0 (name.IndexOf '.'))

    /// Where the registration of the package `name` is, in the index's repository.
    let registrationPath (name: string) = $"registry/{namespaceOf name}/{name}.json"

    /// Where the index file of the package `name` is, in the index's repository.
    let indexPath (name: string) = $"index/{namespaceOf name}/{name}.json"

    let private options = JsonSerializerOptions(WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    /// The registration of the package `name`, whose versions are the releases of `repository`, as a pull request adds it.
    let registration (name: string) (repository: GitHubRepository) : string =
        let file = JsonObject()
        file.["name"] <- JsonValue.Create name
        file.["repository"] <- JsonValue.Create $"https://github.com/{repository}"
        file.ToJsonString options + "\n"

    /// `index`, the text of a package's index file, with `version` yanked, as a pull request changes it; or why it can't
    /// be: the index doesn't list that version, or it is yanked already.
    let yank (name: string) (version: SemanticVersion) (index: string) : Result<string, string> =
        try
            let file = JsonNode.Parse index
            let entry =
                file.["versions"].AsArray()
                |> Seq.tryFind (fun entry -> SemanticVersion.tryParse (string entry.["version"]) = Some version)
            match entry with
            | None -> Result.Error $"The index has no {name} {version} to yank"
            | Some entry when entry.["yanked"] <> null && entry.["yanked"].GetValue<bool>() -> Result.Error $"{name} {version} is yanked already"
            | Some entry ->
                entry.["yanked"] <- JsonValue.Create true
                Result.Ok(file.ToJsonString options + "\n")
        with :? JsonException as error ->
            Result.Error $"The index file of {name} isn't JSON: {error.Message}"
