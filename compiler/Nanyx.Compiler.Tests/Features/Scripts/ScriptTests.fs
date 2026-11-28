module Nanyx.Compiler.Tests.Features.Scripts.Scripts

open System
open System.IO
open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

let private repositoryRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "..", ".."))

let private unusedBinding = withFinalNewline """export def main: [Console] () -> () = {
  def unused = 1
  println("hi")
}"""

/// `source` compiled as a script with no package around it, in a new directory under the temporary directory.
let private compile (source: string) =
    let store = SeededPackages.user ()
    withTempDir (fun directory -> Script.compileSourceMakingIn (makeLinter store) store CompileListener.none (Path.Combine(directory, "script.nyx")) source)

let private codesOf (result: CompileResult) = result.Diagnostics |> List.map _.Code

[<Fact>]
let ``Without directives a script runs no linters, as a manifest without linters does`` () =
    let directives = Script.readFrom (SeededPackages.user ()) "" unusedBinding
    Assert.Equal(None, directives.Linters)
    Assert.Empty(directives.Diagnostics)
    Assert.Empty((compile unusedBinding).Diagnostics)
    // A directive in a comment is just a comment.
    Assert.Empty((compile ("--::linter nanyx.lint\n\n" + unusedBinding)).Diagnostics)

[<Fact>]
let ``A ::linter directive turns the linter on, as its entry in a manifest's linters does`` () =
    let directives = Script.readFrom (SeededPackages.user ()) "" (withFinalNewline """::linter nanyx.lint

println("hi")""")
    Assert.Empty(directives.Diagnostics)
    Assert.Equal<string list>([ "default" ], directives.Linters.Value.Linters |> List.map _.Name)
    Assert.Empty(directives.Linters.Value.Severities)

[<Fact>]
let ``A ::linter at the compiler's version is the linter the compiler ships`` () =
    let directives = Script.readFrom (SeededPackages.user ()) "" $"::linter nanyx.lint {Toolchain.version}\n"
    Assert.Empty(directives.Diagnostics)
    Assert.Equal<string list>([ "default" ], directives.Linters.Value.Linters |> List.map _.Name)

[<Fact>]
let ``A ::lint line sets the severity of a rule of the linter above it`` () =
    let diagnostic = Assert.Single((compile ("::linter nanyx.lint\n::lint unusedBindings #error\n\n" + unusedBinding)).Diagnostics)
    Assert.Equal("default/unusedBindings", diagnostic.Code)
    Assert.Equal(Severity.Error, diagnostic.Severity)
    // The header does not move the code: the binding is still on its own line.
    Assert.Equal(4, diagnostic.Span.Value.StartLine)

[<Fact>]
let ``A ::lint line can turn the rule off`` () =
    Assert.Empty((compile ("::linter nanyx.lint\n::lint unusedBindings #off\n" + unusedBinding)).Diagnostics)

[<Fact>]
let ``Each ::lint line is for the nearest ::linter above it`` () =
    let source = "::linter nanyx.lint\n::lint unusedBindings #error\n::linter acme.lint\n::lint noTodo #off\n"
    let linters =
        Script.settings (Parser.Program.Header.Directives.read "" source)
        |> List.choose (function _, Result.Ok (LintSetting(linter, rule, _)) -> Some (rule.Text, linter.Text) | _ -> None)
    Assert.Equal<(string * string) list>([ "unusedBindings", "nanyx.lint"; "noTodo", "acme.lint" ], linters)
    // The second linter is not one the compiler has, so it is the manifest's error, at its name.
    let directives = Script.readFrom (SeededPackages.user ()) "" source
    let diagnostic = Assert.Single(directives.Diagnostics)
    Assert.Equal(DiagnosticCodes.InvalidManifestValue.Code, diagnostic.Code)
    Assert.Equal("acme.lint", source.Substring(diagnostic.Span.Value.Start, diagnostic.Span.Value.End - diagnostic.Span.Value.Start))
    Assert.Equal(Some(Some Severity.Error), directives.Linters.Value.Severities |> Map.tryFind "default/unusedBindings")

[<Fact>]
let ``A #! line, comments and blank lines can come among the directives`` () =
    let source = "#!/usr/bin/env nanyx run\n-- Says hello.\n::linter nanyx.lint\n::lint unusedBindings #error\n\n::format lineWidth 80\n" + unusedBinding
    let directives = Script.readFrom (SeededPackages.user ()) "" source
    Assert.Empty(directives.Diagnostics)
    Assert.Equal(Some(Some Severity.Error), directives.Linters.Value.Severities |> Map.tryFind "default/unusedBindings")
    Assert.Equal<string list>([ "default/unusedBindings" ], codesOf (compile source))

[<Theory>]
[<InlineData("::install nanyx.json 0.2.0", "nanyx.json", "version 0.2.0")>]
[<InlineData("::install nanyx.regex", "nanyx.regex", "shipped")>]
[<InlineData("::install acme.util ../util", "acme.util", "path ../util")>]
let ``A script names a package it uses with ::install, at a version, by its name, or by its directory`` (directive: string, package: string, place: string) =
    match Script.settings (Parser.Program.Header.Directives.read "" (directive + "\n")) with
    | [ _, Result.Ok(DependencySetting(name, found)) ] ->
        Assert.Equal(package, name.Text)
        let foundText =
            match found with
            | DirectivePlace.AtVersion version -> $"version {version.Text}"
            | DirectivePlace.AtPath path -> $"path {path.Text}"
            | DirectivePlace.AtGitHub reference -> $"github {reference.Text}"
            | DirectivePlace.Shipped -> "shipped"
        Assert.Equal(place, foundText)
    | settings -> failwith $"Expected one package, not {settings}"

/// The one error the directives at the top of `source` have, with the text it is at.
let private directiveError (source: string) =
    let source = "-- A script.\n" + source + "\n"
    let diagnostic = (Script.readFrom (SeededPackages.user ()) "" source).Diagnostics |> List.head
    let span = diagnostic.Span.Value
    diagnostic, source.Substring(span.Start, span.End - span.Start)

[<Theory>]
[<InlineData("::linter nope", "NYX0023", "nope", "There is no linter 'nope': the linters are 'nanyx.lint'")>]
[<InlineData("::linter nanyx.lint ../lint", "NYX0009", "../lint", "A linter cannot be found by its directory yet")>]
[<InlineData("::linter nanyx.lint 0.0.1", "NYX0029", "0.0.1", "The compiler ships 'nanyx.lint'")>]
[<InlineData("::linter", "NYX0009", "::linter", "`::linter` takes a linter: write `::linter nanyx.lint`")>]
[<InlineData("::linter nanyx.lint\n::lint nope #error", "NYX0021", "nope", "The rules of 'nanyx.lint' has no field 'nope'")>]
[<InlineData("::linter nanyx.lint\n::lint unusedBindings #loud", "NYX0023", "#loud", "The rule unusedBindings must be #error, #warning, #info, #hint, #off, not #loud")>]
[<InlineData("::linter nanyx.lint\n::lint unusedBindings 3", "NYX0023", "3", "not 3")>]
[<InlineData("::linter nanyx.lint\n::lint unusedBindings error", "NYX0009", "error", "'error' is not a value")>]
[<InlineData("::linter nanyx.lint\n::lint unusedBindings", "NYX0009", "::lint unusedBindings", "`::lint` takes a rule of the linter above it and a severity")>]
[<InlineData("::format lineWidth", "NYX0009", "::format lineWidth", "`::format` takes a setting and its value")>]
[<InlineData("::colour red", "NYX0009", "colour", "Nanyx has no directive of this name: the directives are `::host`, `::install`, `::linter`, `::lint` and `::format`")>]
[<InlineData("::", "NYX0009", "::", "`::` has no directive's name after it")>]
// `::install` was `::dependency`, which is no longer a name of it.
[<InlineData("::dependency acme.util ../util", "NYX0009", "dependency", "Nanyx has no directive of this name: the directives are `::host`, `::install`, `::linter`, `::lint` and `::format`")>]
let ``A directive written wrong is an error at the word it is about`` (directive: string, code: string, at: string, message: string) =
    let diagnostic, text = directiveError directive
    Assert.Equal(code, diagnostic.Code)
    Assert.Equal(Severity.Error, diagnostic.Severity)
    Assert.Contains(message, diagnostic.Message)
    Assert.Equal(at, text)

[<Fact>]
let ``A ::lint with no ::linter above it is an error whose fix turns on the official linter`` () =
    let source = "-- A script.\n::lint unusedBindings #error\n::linter nanyx.lint\n"
    let diagnostic = Assert.Single((Script.readFrom (SeededPackages.user ()) "" source).Diagnostics)
    Assert.Equal(DiagnosticCodes.LintWithoutLinter.Code, diagnostic.Code)
    Assert.Equal(1, diagnostic.Span.Value.StartLine)
    let fix = Assert.Single(diagnostic.Fixes)
    Assert.Equal("-- A script.\n::linter nanyx.lint\n::lint unusedBindings #error\n::linter nanyx.lint\n", applyFix source fix)

[<Theory>]
[<InlineData("::linters.default", "::linter nanyx.lint")>]
[<InlineData("::linters.default.unusedBindings = \"error\"", "::linter nanyx.lint\n::lint unusedBindings #error")>]
[<InlineData("::formatter.lineWidth = 80", "::format lineWidth 80")>]
[<InlineData("::host.acme.server = { path = \"../server\" }", "::host acme.server ../server")>]
[<InlineData("::host.nanyx.cli.host = \"0.28.0\"", "::host nanyx.cli.host 0.28.0")>]
[<InlineData("::dependencies.acme.util = { path = \"util\" }", "::install acme.util ./util")>]
[<InlineData("::dependencies.acme.json = \"1.2.0\"", "::install acme.json 1.2.0")>]
let ``A directive in the old form is an error whose fix writes it as it is written now`` (old: string, now: string) =
    let source = "-- A script.\n" + old + "\n"
    let diagnostic = Assert.Single((Script.readFrom (SeededPackages.user ()) "" source).Diagnostics)
    Assert.Equal(DiagnosticCodes.OldFormDirective.Code, diagnostic.Code)
    Assert.Equal("-- A script.\n" + now + "\n", applyFix source (Assert.Single(diagnostic.Fixes)))

[<Fact>]
let ``The fix for a directive in the old form keeps a comment after it`` () =
    let source = "::formatter.lineWidth = 80  -- narrow\n"
    let diagnostic = Assert.Single((Script.readFrom (SeededPackages.user ()) "" source).Diagnostics)
    Assert.Equal("::format lineWidth 80  -- narrow\n", applyFix source (Assert.Single(diagnostic.Fixes)))

[<Fact>]
let ``A rule in the old form under a linter needs no ::linter of its own`` () =
    let source = "::linter nanyx.lint\n::linters.default.unusedBindings = \"off\"\n"
    let diagnostic = Assert.Single((Script.readFrom (SeededPackages.user ()) "" source).Diagnostics)
    Assert.Equal("::linter nanyx.lint\n::lint unusedBindings #off\n", applyFix source (Assert.Single(diagnostic.Fixes)))

[<Fact>]
let ``A directive in the old form that can't be read has no fix`` () =
    let diagnostic = Assert.Single((Script.readFrom (SeededPackages.user ()) "" "::linters.default.unusedBindings = error\n").Diagnostics)
    Assert.Equal(DiagnosticCodes.OldFormDirective.Code, diagnostic.Code)
    Assert.Empty(diagnostic.Fixes)

[<Fact>]
let ``A directive that is not valid leaves the others in force`` () =
    let result = compile ("::colour red\n::linter nanyx.lint\n::lint unusedBindings #error\n" + unusedBinding)
    Assert.Equal<string list>([ "NYX0009"; "default/unusedBindings" ], codesOf result)
    Assert.Equal(Severity.Error, result.Diagnostics[1].Severity)

[<Fact>]
let ``A directive after the first line of code is a syntax error saying where directives go`` () =
    let result = compile (withFinalNewline """println("hi")
::linter nanyx.lint""")
    let diagnostic = Assert.Single(result.Diagnostics)
    Assert.Equal(DiagnosticCodes.SyntaxError.Code, diagnostic.Code)
    Assert.Contains("must come before the first line of code", diagnostic.Message)
    Assert.Equal(1, diagnostic.Span.Value.StartLine)

[<Fact>]
let ``A directive in a file of a package is an error saying to use the manifest`` () =
    withTempDir (fun root ->
        let manifest = Path.Combine(root, "demo.nyxp")
        File.WriteAllText(manifest, $"""(package = (name = "demo", version = "0.1.0"), {consoleHostEntry})""")
        let main = Path.Combine(root, "main.nyx")
        File.WriteAllText(main, "::linter nanyx.lint\nmodule main\n\n" + unusedBinding)
        let directives = Script.readFrom (SeededPackages.user ()) main (File.ReadAllText main)
        Assert.Equal(None, directives.Linters)
        let diagnostic = Assert.Single(directives.Diagnostics)
        Assert.Equal(DiagnosticCodes.DirectiveInPackage.Code, diagnostic.Code)
        Assert.Contains(manifest, diagnostic.Message)
        let build = Package.build (match Package.findIn (SeededPackages.user ()) root with Result.Ok package -> package | Result.Error e -> failwith (String.concat "\n" e))
        Assert.Equal<string list>([ DiagnosticCodes.DirectiveInPackage.Code ], build.Diagnostics |> List.map _.Code))

[<Fact>]
let ``The example script runs cleanly`` () =
    let result = Script.compileFileIn (SeededPackages.user ()) (Path.Combine(repositoryRoot, "examples", "directives.nyx"))
    Assert.Empty(result.Diagnostics)
    Assert.True(result.Typed.IsSome)
