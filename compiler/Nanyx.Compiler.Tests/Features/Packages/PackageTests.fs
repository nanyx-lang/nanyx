module Nanyx.Compiler.Tests.Features.Packages.Packages

open System
open System.IO
open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

let private repositoryRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "..", ".."))

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
    use package = new TempPackage([ "demo.nyxp", manifest ""; "main.nyx", entrySource ])
    let read = okOf (Manifest.read (package.Path "demo.nyxp"))
    Assert.Equal("demo", read.Name)
    Assert.Equal("0.1.0", read.Version)
    Assert.Equal<string list>([ "**/*.nyx" ], read.Sources)
    Assert.Empty(read.Linters.Linters)

[<Fact>]
let ``A linters table turns on the linters it names, with the severities it sets`` () =
    use package =
        new TempPackage([ "demo.nyxp", manifest "[linters.default]\nunusedBindings = \"error\"\nlinterFailed = \"none\"" ])
    let read = okOf (Manifest.read (package.Path "demo.nyxp"))
    Assert.Equal<string list>([ "default" ], read.Linters.Linters |> List.map _.Name)
    Assert.Equal(Some(Some Severity.Error), read.Linters.Severities |> Map.tryFind "default/unusedBindings")
    Assert.Equal(Some None, read.Linters.Severities |> Map.tryFind "default/linterFailed")

[<Fact>]
let ``Every problem with a manifest is reported, naming the key`` () =
    let text =
        "[package]\nname = \"Demo\"\nversion = 1\ncolour = \"red\"\n"
        + "[linters.default]\nunusedBindings = \"loud\"\nnoSuchRule = \"error\"\n"
        + "[linters.other]\n[dependencies]\nacme.json = \"1.0\"\n[extras]\n"
    use package = new TempPackage([ "demo.nyxp", text ])
    let errors = errorsOf (Manifest.read (package.Path "demo.nyxp")) |> List.map (fun e -> e.Substring(e.IndexOf(": ") + 2))
    Assert.Contains("[package] name \"Demo\" must be lower case, with words separated by dots: acme.web", errors)
    Assert.Contains("[package] version must be a string, not 1", errors)
    Assert.Contains("unknown key [package] colour; the keys are 'name', 'version', 'sources', 'public-modules'", errors)
    Assert.Contains("[linters.default] unusedBindings must be 'error', 'warning', 'info', 'hint', 'none', not the string \"loud\"", errors)
    Assert.Contains("unknown rule [linters.default] noSuchRule; the rules are 'unusedBindings', 'nonTailRecursion', 'unreachableArms', 'unusedImports', 'noTagInCommon', 'unusedPrivateDefinitions', 'unreachableCode', 'useInBraces', 'strayDocComments', 'plusBetweenContexts', 'unannotatedExports', 'unusedResults', 'shadowedPreludeNames', 'linterFailed'", errors)
    Assert.Contains("unknown linter [linters.other]; the linters are 'default'", errors)
    Assert.Contains("[dependencies] is not supported yet: a package can only use the standard library and its own files", errors)
    Assert.Contains("unknown table [extras]; the tables are 'package', 'dependencies', 'project-references', 'linters', 'formatter'", errors)

[<Fact>]
let ``A manifest that is not TOML says so`` () =
    use package = new TempPackage([ "demo.nyxp", "[package" ])
    let error = Assert.Single(errorsOf (Manifest.read (package.Path "demo.nyxp")))
    Assert.Contains("not valid TOML", error)

[<Fact>]
let ``A package is found from a directory under it, with its sources and entry`` () =
    use package =
        new TempPackage(
            [ "demo.nyxp", manifest "sources = [\"src/**/*.nyx\"]"
              "src/main.nyx", entrySource
              "src/lib/util.nyx", "module util\n\nexport def one = 1\n"
              "scripts/other.nyx", "println(\"not a source\")\n" ])
    let found = okOf (Package.find (package.Path "src/lib"))
    Assert.Equal<string list>([ package.Path "src/lib/util.nyx"; package.Path "src/main.nyx" ] |> List.map Path.GetFullPath |> List.sort, found.Sources)
    Assert.Equal(Some(Path.GetFullPath(package.Path "src/main.nyx")), found.Entry)

[<Fact>]
let ``A package with two entries is an error naming both`` () =
    use package = new TempPackage([ "demo.nyxp", manifest ""; "a.nyx", entrySource; "b.nyx", entrySource ])
    let error = Assert.Single(errorsOf (Package.find package.Root))
    Assert.Contains("a.nyx, b.nyx", error)

[<Fact>]
let ``A manifest with the old .nyxpack extension is not a manifest`` () =
    use package = new TempPackage([ "demo.nyxpack", manifest ""; "main.nyx", entrySource ])
    Assert.Contains("No .nyxp manifest in", Assert.Single(errorsOf (Package.find package.Root)))
    Assert.Contains("Not a directory or a .nyxp manifest", Assert.Single(errorsOf (Package.find (package.Path "demo.nyxpack"))))

[<Fact>]
let ``Without a linters table no linter runs`` () =
    use package = new TempPackage([ "demo.nyxp", manifest ""; "main.nyx", entrySource ])
    let build = Package.build (okOf (Package.find package.Root))
    Assert.Empty(build.Diagnostics)
    Assert.True((Package.entryResult build).IsSome)

[<Fact>]
let ``The manifest's linters check every source file at the severities it sets`` () =
    use package =
        new TempPackage(
            [ "demo.nyxp", manifest "[linters.default]\nunusedBindings = \"error\""
              "main.nyx", entrySource ])
    let build = Package.build (okOf (Package.find package.Root))
    let diagnostic = Assert.Single(build.Diagnostics)
    Assert.Equal("default/unusedBindings", diagnostic.Code)
    Assert.Equal(Severity.Error, diagnostic.Severity)
    Assert.Equal(Some(Some Severity.Error), (Package.lintersFor (package.Path "main.nyx")) |> Option.map (fun o -> o.Severities |> Map.tryFind "default/unusedBindings" |> Option.flatten))

[<Fact>]
let ``The example package builds cleanly`` () =
    let build = Package.build (okOf (Package.find (Path.Combine(repositoryRoot, "examples", "greeter"))))
    Assert.Empty(build.Diagnostics)
    Assert.True((Package.entryResult build).IsSome)

/// The greeter example as it was first written with a nominal `$Person`: it named the type without the import's alias
/// and built a `$Person` from its shape outside the module that defines it.
let private greeterGotWrong (construct: string) =
    [ "greeter.nyxp", manifest ""
      "src/greetings.nyx",
      "module greetings\n\nexport def greet: $Person -> string = { p ->\n  \"Hello, \" ++ p.name ++ \"!\"\n}\n\n"
      + "export type $Person = (name: string)\n"
      "src/main.nyx",
      $"module main\n\nimport greetings as greetings\n\n@entry\ndef main = {{\n  println(greetings.greet({construct}(name = \"Ada\")))\n}}\n" ]

[<Fact>]
let ``A package that names an aliased import's nominal type without the alias does not build`` () =
    use package = new TempPackage(greeterGotWrong "$Person")
    let build = Package.build (okOf (Package.find package.Root))
    let diagnostic = Assert.Single(build.Diagnostics)
    Assert.Equal(DiagnosticCodes.UnknownFunction.Code, diagnostic.Code)
    Assert.True(diagnostic.Span.IsSome && diagnostic.Span.Value.File.EndsWith "main.nyx")

[<Fact>]
let ``A package that builds another module's nominal type from its shape does not build`` () =
    use package = new TempPackage(greeterGotWrong "greetings.$Person")
    let build = Package.build (okOf (Package.find package.Root))
    let diagnostic = Assert.Single(build.Diagnostics)
    Assert.Equal(DiagnosticCodes.NominalConstructedOutsideItsModule.Code, diagnostic.Code)