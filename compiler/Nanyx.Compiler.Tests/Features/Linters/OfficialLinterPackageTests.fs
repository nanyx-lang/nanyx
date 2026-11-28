/// The official linter's package, `nanyx.lint`, shipped with the compiler and run beside the compiler's own rules until
/// they move into it (see the Linters page in Work in progress, #3174).
module Nanyx.Compiler.Tests.Features.Linters.OfficialLinterPackage

open System.IO
open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

let private packageDirectory =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "..", "..", "packages", Linters.official.Package))

[<Fact>]
let ``nanyx.lint declares every rule of the official linter, as the compiler has it`` () =
    let manifest =
        match Manifest.read (Path.Combine(packageDirectory, "nanyx.lint.nyxp")) with
        | Result.Ok manifest -> manifest
        | Result.Error errors -> failwith (errors |> List.map Manifest.describe |> String.concat "\n")
    let declared = manifest.DeclaresLinter.Value
    Assert.Equal(Linters.official.Name, declared.Name)
    Assert.Equal<Rule list>(Linters.official.Rules, declared.Rules)

[<Fact>]
let ``A manifest's nanyx.lint entry is the shipped package too, read offline`` () =
    use package =
        new TempPackage(
            [ "demo.nyxp",
              $"""(package = (name = "demo", version = "0.1.0"), linters = [(package = "nanyx.lint", version = "{Toolchain.version}", settings = (rules = (unusedBindings = #error)))])"""
              "main.nyx", "export module main\n\nexport def answer = 42\n" ]
        )
    match Package.findIn (SeededPackages.user ()) package.Root with
    | Result.Ok found ->
        let linter = Assert.Single(found.LinterPackages)
        Assert.Equal(Linters.official.Name, linter.Declaration.Name)
        Assert.Equal(Some(Some Severity.Error), linter.Severities |> Map.tryFind "default/unusedBindings")
        Assert.Equal<string list>([ "default" ], found.Linters.Linters |> List.map _.Name)
    | Result.Error errors -> failwith (String.concat "\n" errors)

[<Fact>]
let ``A script's ::linter nanyx.lint is the shipped package's seeded copy`` () =
    let directives = Script.readFrom (SeededPackages.user ()) "" "::linter nanyx.lint\n\nprintln(\"hi\")\n"
    Assert.Empty(directives.Diagnostics)
    let linter = Assert.Single(directives.LinterPackages)
    Assert.Equal(Linters.official.Package, linter.Package.Manifest.Name)
    Assert.Equal(Toolchain.version, linter.Package.Manifest.Version)

[<Fact>]
let ``Where the package can't be run, the official linter's own rules run alone`` () =
    let directives = Script.readFrom (SeededPackages.user ()) "" "::linter nanyx.lint\n\nprintln(\"hi\")\n"
    let options = Script.linterOptions (fun _ -> Result.Error "not here") directives
    Assert.Equal<string list>([ "default" ], options.Linters |> List.map _.Name)
    let result = Compiler.compileWith options "def count = { n -> if n == 0 then 0 else 1 + count(n - 1) }\n\nprintln(\"{count(3)}\")\n"
    Assert.Contains(result.Diagnostics, fun diagnostic -> diagnostic.Code = "default/nonTailRecursion")

[<Fact>]
let ``The official linter runs as its package, with the compiler's rules for those it doesn't implement yet`` () =
    let result = Compiler.compileWith { Linters = [ officialLinter.Value ]; Severities = Map.empty } "def unused = 1\n\nprintln(\"hi\")\n"
    Assert.Equal<string list>([ "default/unusedPrivateDefinitions" ], result.Diagnostics |> List.map _.Code)
