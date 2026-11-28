module Nanyx.Compiler.Tests.Features.Packages.Packages

open System
open System.IO
open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

let private repositoryRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "..", ".."))

/// The manifest of the application `demo`, run by the console host, with `fields` after its `package`: `linters = [...]`.
let private manifest (fields: string) = $"(\n  package = (name = \"demo\", version = \"0.1.0\")\n  {consoleHostEntry}\n  {fields}\n)\n"

let private entrySource = withFinalNewline """module main

@entry
def main = {
  def unused = 1
  println("hi")
}"""

[<Fact>]
let ``A package is found from a directory under it, with its sources and entry`` () =
    use package =
        new TempPackage(
            [ "demo.nyxp", $"""(package = (name = "demo", version = "0.1.0", sources = ["src/**/*.nyx"]), {consoleHostEntry})"""
              "src/main.nyx", entrySource
              "src/lib/util.nyx", "module util\n\nexport def one = 1\n"
              "scripts/other.nyx", (withFinalNewline """println("not a source")""") ])
    let found = okOf (Package.findIn (SeededPackages.user ()) (package.Path "src/lib"))
    Assert.Equal<string list>([ package.Path "src/lib/util.nyx"; package.Path "src/main.nyx" ] |> List.map Path.GetFullPath |> List.sort, found.Sources)
    Assert.Equal(Some(Path.GetFullPath(package.Path "src/main.nyx")), found.Entry)

[<Fact>]
let ``A package with two entries is an error naming both`` () =
    use package = new TempPackage([ "demo.nyxp", manifest ""; "a.nyx", entrySource; "b.nyx", entrySource ])
    let error = Assert.Single(errorsOf (Package.findIn (SeededPackages.user ()) package.Root))
    Assert.Contains("'a.nyx' and 'b.nyx'", error)

[<Fact>]
let ``A manifest with the old .nyxpack extension is not a manifest`` () =
    use package = new TempPackage([ "demo.nyxpack", manifest ""; "main.nyx", entrySource ])
    Assert.Contains("There is no .nyxp manifest in", Assert.Single(errorsOf (Package.findIn (SeededPackages.user ()) package.Root)))
    Assert.Contains("is neither a directory nor a .nyxp manifest", Assert.Single(errorsOf (Package.findIn (SeededPackages.user ()) (package.Path "demo.nyxpack"))))

[<Fact>]
let ``A file under a manifest that does not read has the manifest's errors, not its imports'`` () =
    use package =
        new TempPackage(
            [ "shop.nyxp", "[package]\nname = \"shop\"\n"
              "src/main.nyx", "module main\n\nimport shop.catalogue\n\n@entry\ndef main = {\n  println(\"hi\")\n}\n" ])
    let mainPath = package.Path "src/main.nyx"
    let result = Script.compileFileIn (SeededPackages.user ()) mainPath
    let diagnostic = Assert.Single(result.Diagnostics)
    Assert.Equal(DiagnosticCodes.TomlManifest.Code, diagnostic.Code)
    Assert.Equal(Path.GetFullPath(package.Path "shop.nyxp"), diagnostic.Span.Value.File)

[<Fact>]
let ``A build tells its caller each source file as it starts on it, in order`` () =
    use package =
        new TempPackage(
            [ "demo.nyxp", manifest ""
              "src/main.nyx", "module main\n\nimport util\n\n@entry\ndef main = {\n  println(\"{one}\")\n}\n"
              "src/util.nyx", "module util\n\nexport def one = 1\n" ])
    let found = okOf (Package.findIn (SeededPackages.user ()) package.Root)
    let started = Collections.Generic.List<string>()
    let listener =
        { new CompileListener with
            member _.FileStarted file = started.Add file
            member _.ModuleStarted _ = ()
            member _.ModuleFinished(_, _) = ()
            member _.ModuleReused _ = () }
    let build = Package.buildReporting listener found
    Assert.Empty(build.Diagnostics)
    Assert.Equal<string list>(found.Sources, List.ofSeq started)

[<Fact>]
let ``Without linters no linter runs`` () =
    use package = new TempPackage([ "demo.nyxp", manifest ""; "main.nyx", entrySource ])
    let build = Package.build (okOf (Package.findIn (SeededPackages.user ()) package.Root))
    Assert.Empty(build.Diagnostics)
    Assert.True((Package.entryResult build).IsSome)

[<Fact>]
let ``The manifest's linters check every source file at the severities it sets`` () =
    use package =
        new TempPackage(
            [ "demo.nyxp", (manifest $"""linters = [(package = "nanyx.lint", version = "{Toolchain.version}", settings = (rules = (unusedBindings = #error)))]""")
              "main.nyx", entrySource ])
    let store = SeededPackages.user ()
    let build = Package.build (Package.withLinters (makeLinter store) (okOf (Package.findIn store package.Root)))
    let diagnostic = Assert.Single(build.Diagnostics)
    Assert.Equal("default/unusedBindings", diagnostic.Code)
    Assert.Equal(Severity.Error, diagnostic.Severity)
    Assert.Equal(Some(Some Severity.Error), (Package.lintersFor (SeededPackages.user ()) (fun _ -> Result.Error "no linter packages here") (package.Path "main.nyx")) |> Option.map (fun o -> o.Severities |> Map.tryFind "default/unusedBindings" |> Option.flatten))

[<Fact>]
let ``The example package builds cleanly`` () =
    let build = Package.build (okOf (Package.findIn (SeededPackages.user ()) (Path.Combine(repositoryRoot, "examples", "greeter"))))
    Assert.Empty(build.Diagnostics)
    Assert.True((Package.entryResult build).IsSome)

/// The greeter example as it was first written with a nominal `Person`: it named the type without the import's alias
/// and built a `Person` from its shape outside the module that defines it.
let private greeterGotWrong (construct: string) =
    [ "greeter.nyxp", manifest ""
      "src/greetings.nyx",
      "module greetings\n\nexport def greet: Person -> string = { p ->\n  \"Hello, \" ++ p.name ++ \"!\"\n}\n\n"
      + "export type Person = protected (name: string)\n"
      "src/main.nyx",
      $"module main\n\nimport greetings as greetings\n\n@entry\ndef main = {{\n  println(greetings.greet({construct}(name = \"Ada\")))\n}}\n" ]

[<Fact>]
let ``A package that names an aliased import's nominal type without the alias does not build`` () =
    use package = new TempPackage(greeterGotWrong "Person")
    let build = Package.build (okOf (Package.findIn (SeededPackages.user ()) package.Root))
    let diagnostic = Assert.Single(build.Diagnostics)
    Assert.Equal(DiagnosticCodes.UnknownFunction.Code, diagnostic.Code)
    Assert.True(diagnostic.Span.IsSome && diagnostic.Span.Value.File.EndsWith "main.nyx")

[<Fact>]
let ``A package that builds another module's nominal type from its shape does not build`` () =
    use package = new TempPackage(greeterGotWrong "greetings.Person")
    let build = Package.build (okOf (Package.findIn (SeededPackages.user ()) package.Root))
    let diagnostic = Assert.Single(build.Diagnostics)
    Assert.Equal(DiagnosticCodes.NominalConstructedOutsideItsModule.Code, diagnostic.Code)