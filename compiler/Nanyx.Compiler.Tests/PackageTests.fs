module Nanyx.Compiler.PackageTests

open System
open System.IO
open Xunit
open NyxCompiler

let private repositoryRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

/// A new directory holding `files` (relative path, contents), deleted when disposed.
type private TempPackage(files: (string * string) list) =
    let root = Path.Combine(Path.GetTempPath(), "nanyx-package-" + Guid.NewGuid().ToString("N"))
    do
        for (path, contents) in files do
            let full = Path.Combine(root, path)
            Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
            File.WriteAllText(full, contents)
    member _.Root = root
    member _.Path(relative: string) = Path.Combine(root, relative)
    interface IDisposable with
        member _.Dispose() = try Directory.Delete(root, true) with _ -> ()

let private manifest extra = $"[package]\nname = \"demo\"\nversion = \"0.1.0\"\n{extra}"

let private entrySource = "module main\n\n@entry\ndef main = {\n  def unused = 1\n  println(\"hi\")\n}\n"

let private errorsOf result =
    match result with
    | Result.Error errors -> errors
    | Result.Ok _ -> failwith "Expected errors"

let private okOf result =
    match result with
    | Result.Ok value -> value
    | Result.Error errors -> failwith (String.concat "\n" errors)

[<Fact>]
let ``A manifest's package table is read, with the default sources`` () =
    use package = new TempPackage([ "demo.nyxpack", manifest ""; "main.nyx", entrySource ])
    let read = okOf (Manifest.read (package.Path "demo.nyxpack"))
    Assert.Equal("demo", read.Name)
    Assert.Equal("0.1.0", read.Version)
    Assert.Equal<string list>([ "**/*.nyx" ], read.Sources)
    Assert.Empty(read.Analyzers.Analyzers)

[<Fact>]
let ``An analyzers table turns on the analyzers it names, with the severities it sets`` () =
    use package =
        new TempPackage([ "demo.nyxpack", manifest "[analyzers.default]\nunusedBindings = \"error\"\nanalyzerFailed = \"none\"" ])
    let read = okOf (Manifest.read (package.Path "demo.nyxpack"))
    Assert.Equal<string list>([ "default" ], read.Analyzers.Analyzers |> List.map _.Name)
    Assert.Equal(Some(Some Severity.Error), read.Analyzers.Severities |> Map.tryFind "default/unusedBindings")
    Assert.Equal(Some None, read.Analyzers.Severities |> Map.tryFind "default/analyzerFailed")

[<Fact>]
let ``Every problem with a manifest is reported, naming the key`` () =
    let text =
        "[package]\nname = \"Demo\"\nversion = 1\ncolour = \"red\"\n"
        + "[analyzers.default]\nunusedBindings = \"loud\"\nnoSuchRule = \"error\"\n"
        + "[analyzers.other]\n[dependencies]\nacme.json = \"1.0\"\n[extras]\n"
    use package = new TempPackage([ "demo.nyxpack", text ])
    let errors = errorsOf (Manifest.read (package.Path "demo.nyxpack")) |> List.map (fun e -> e.Substring(e.IndexOf(": ") + 2))
    Assert.Contains("[package] name \"Demo\" must be lower case, with words separated by dots: acme.web", errors)
    Assert.Contains("[package] version must be a string, not 1", errors)
    Assert.Contains("unknown key [package] colour; the keys are 'name', 'version', 'sources', 'public-modules'", errors)
    Assert.Contains("[analyzers.default] unusedBindings must be 'error', 'warning', 'info', 'hint', 'none', not the string \"loud\"", errors)
    Assert.Contains("unknown rule [analyzers.default] noSuchRule; the rules are 'unusedBindings', 'analyzerFailed'", errors)
    Assert.Contains("unknown analyzer [analyzers.other]; the analyzers are 'default'", errors)
    Assert.Contains("[dependencies] is not supported yet: a package can only use the standard library and its own files", errors)
    Assert.Contains("unknown table [extras]; the tables are 'package', 'dependencies', 'project-references', 'analyzers', 'formatter'", errors)

[<Fact>]
let ``A manifest that is not TOML says so`` () =
    use package = new TempPackage([ "demo.nyxpack", "[package" ])
    let error = Assert.Single(errorsOf (Manifest.read (package.Path "demo.nyxpack")))
    Assert.Contains("not valid TOML", error)

[<Fact>]
let ``A package is found from a directory under it, with its sources and entry`` () =
    use package =
        new TempPackage(
            [ "demo.nyxpack", manifest "sources = [\"src/**/*.nyx\"]"
              "src/main.nyx", entrySource
              "src/lib/util.nyx", "module util\n\nexport def one = 1\n"
              "scripts/other.nyx", "println(\"not a source\")\n" ])
    let found = okOf (Package.find (package.Path "src/lib"))
    Assert.Equal<string list>([ package.Path "src/lib/util.nyx"; package.Path "src/main.nyx" ] |> List.map Path.GetFullPath |> List.sort, found.Sources)
    Assert.Equal(Some(Path.GetFullPath(package.Path "src/main.nyx")), found.Entry)

[<Fact>]
let ``A package with two entries is an error naming both`` () =
    use package = new TempPackage([ "demo.nyxpack", manifest ""; "a.nyx", entrySource; "b.nyx", entrySource ])
    let error = Assert.Single(errorsOf (Package.find package.Root))
    Assert.Contains("a.nyx, b.nyx", error)

[<Fact>]
let ``Without an analyzers table no analyzer runs`` () =
    use package = new TempPackage([ "demo.nyxpack", manifest ""; "main.nyx", entrySource ])
    let build = Package.build (okOf (Package.find package.Root))
    Assert.Empty(build.Diagnostics)
    Assert.True((Package.entryResult build).IsSome)

[<Fact>]
let ``The manifest's analyzers check every source file at the severities it sets`` () =
    use package =
        new TempPackage(
            [ "demo.nyxpack", manifest "[analyzers.default]\nunusedBindings = \"error\""
              "main.nyx", entrySource ])
    let build = Package.build (okOf (Package.find package.Root))
    let diagnostic = Assert.Single(build.Diagnostics)
    Assert.Equal("default/unusedBindings", diagnostic.Code)
    Assert.Equal(Severity.Error, diagnostic.Severity)
    Assert.Equal(Some(Some Severity.Error), (Package.analyzersFor (package.Path "main.nyx")) |> Option.map (fun o -> o.Severities |> Map.tryFind "default/unusedBindings" |> Option.flatten))

[<Fact>]
let ``The example package builds cleanly`` () =
    let build = Package.build (okOf (Package.find (Path.Combine(repositoryRoot, "examples", "greeter"))))
    Assert.Empty(build.Diagnostics)
    Assert.True((Package.entryResult build).IsSome)
