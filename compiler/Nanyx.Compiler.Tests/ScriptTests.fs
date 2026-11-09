module Nanyx.Compiler.ScriptTests

open System
open System.IO
open Xunit
open NyxCompiler

let private repositoryRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

/// Where a script with no package around it is compiled from: a new directory under the temporary directory.
let private scriptPath () =
    let directory = Path.Combine(Path.GetTempPath(), "nanyx-script-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory directory |> ignore
    Path.Combine(directory, "script.nyx")

let private unusedBinding = "import nanyx/console\n\ndef main = {\n  def unused = 1\n  println(\"hi\")\n}\n"

let private compile (source: string) = Script.compileSource (scriptPath ()) source

let private codesOf (result: CompileResult) = result.Diagnostics |> List.map _.Code

[<Fact>]
let ``Without directives a script runs no linters, as a manifest without a linters table does`` () =
    let directives = Script.read "" unusedBinding
    Assert.Equal(None, directives.Linters)
    Assert.Empty(directives.Diagnostics)
    Assert.Empty((compile unusedBinding).Diagnostics)
    // A directive in a comment is just a comment.
    Assert.Empty((compile ("--::linter default\n\n" + unusedBinding)).Diagnostics)

[<Fact>]
let ``A linter directive turns the linter on, as an empty table in a manifest does`` () =
    let directives = Script.read "" "::linter default\n\nprintln(\"hi\")\n"
    Assert.Empty(directives.Diagnostics)
    Assert.Equal<string list>([ "default" ], directives.Linters.Value.Linters |> List.map _.Name)
    Assert.Empty(directives.Linters.Value.Severities)

[<Fact>]
let ``A rule directive sets the rule's severity`` () =
    let diagnostic = Assert.Single((compile ("::linter default.unusedBindings error\n\n" + unusedBinding)).Diagnostics)
    Assert.Equal("default/unusedBindings", diagnostic.Code)
    Assert.Equal(Severity.Error, diagnostic.Severity)
    // The header does not move the code: the binding is still on its own line.
    Assert.Equal(5, diagnostic.Span.Value.StartLine)

[<Fact>]
let ``A rule directive can turn the rule off`` () =
    Assert.Empty((compile ("::linter default.unusedBindings none\n" + unusedBinding)).Diagnostics)

[<Fact>]
let ``A #! line, comments and blank lines can come among the directives`` () =
    let source = "#!/usr/bin/env nanyx run\n-- Says hello.\n::linter default.unusedBindings error\n\n::formatter lineWidth 80\n" + unusedBinding
    let directives = Script.read "" source
    Assert.Empty(directives.Diagnostics)
    Assert.Equal(Some(Some Severity.Error), directives.Linters.Value.Severities |> Map.tryFind "default/unusedBindings")
    Assert.Equal<string list>([ "default/unusedBindings" ], codesOf (compile source))

[<Theory>]
[<InlineData("::linter nope", "NYX0009", "unknown linter 'nope'; the linters are 'default'")>]
[<InlineData("::linter default.nope error", "NYX0009", "unknown rule 'nope' of the linter 'default'; the rules are 'unusedBindings', 'nonTailRecursion', 'unreachableArms', 'unusedImports', 'linterFailed'")>]
[<InlineData("::linter default.unusedBindings loud", "NYX0009", "::linter default.unusedBindings must be 'error', 'warning', 'info', 'hint', 'none', not 'loud'")>]
[<InlineData("::linter default error", "NYX0009", "::linter takes a linter, or one of its rules and a severity")>]
[<InlineData("::linter", "NYX0009", "::linter takes a linter")>]
[<InlineData("::package acme.json 1.2", "NYX0011", "::package is not supported yet")>]
[<InlineData("::package acme.json", "NYX0009", "::package takes a package and its version")>]
[<InlineData("::colour red", "NYX0009", "unknown directive ::colour; the directives are '::linter', '::formatter', '::package'")>]
[<InlineData("::", "NYX0009", "a directive needs a name after '::'")>]
let ``A directive that is not valid is an error at its line`` (directive: string, code: string, message: string) =
    let result = compile ("-- A script.\n" + directive + "\nprintln(\"hi\")\n")
    let diagnostic = result.Diagnostics |> List.head
    Assert.Equal(code, diagnostic.Code)
    Assert.Equal(Severity.Error, diagnostic.Severity)
    Assert.Contains(message, diagnostic.Message)
    Assert.Equal(1, diagnostic.Span.Value.StartLine)
    Assert.Equal(0, diagnostic.Span.Value.StartColumn)
    Assert.True(result.Diagnostics |> List.exists Diagnostics.isError)

[<Fact>]
let ``A directive that is not valid leaves the others in force`` () =
    let result = compile ("::linter nope\n::linter default.unusedBindings error\n" + unusedBinding)
    Assert.Equal<string list>([ "NYX0009"; "default/unusedBindings" ], codesOf result)
    Assert.Equal(Severity.Error, result.Diagnostics[1].Severity)

[<Fact>]
let ``A directive after the first line of code is a syntax error saying where directives go`` () =
    let result = compile "println(\"hi\")\n::linter default\n"
    let diagnostic = Assert.Single(result.Diagnostics)
    Assert.Equal(DiagnosticCodes.SyntaxError.Code, diagnostic.Code)
    Assert.Contains("must come before the first line of code", diagnostic.Message)
    Assert.Equal(1, diagnostic.Span.Value.StartLine)

[<Fact>]
let ``A directive in a file of a package is an error saying to use the manifest`` () =
    let root = Path.GetDirectoryName(scriptPath ())
    let manifest = Path.Combine(root, "demo.nyxpack")
    File.WriteAllText(manifest, "[package]\nname = \"demo\"\nversion = \"0.1.0\"\n")
    let main = Path.Combine(root, "main.nyx")
    File.WriteAllText(main, "::linter default.unusedBindings error\n" + unusedBinding)
    try
        let directives = Script.read main (File.ReadAllText main)
        Assert.Equal(None, directives.Linters)
        let diagnostic = Assert.Single(directives.Diagnostics)
        Assert.Equal(DiagnosticCodes.DirectiveInPackage.Code, diagnostic.Code)
        Assert.Contains(manifest, diagnostic.Message)
        let build = Package.build (match Package.find root with Result.Ok package -> package | Result.Error e -> failwith (String.concat "\n" e))
        Assert.Equal<string list>([ DiagnosticCodes.DirectiveInPackage.Code ], build.Diagnostics |> List.map _.Code)
    finally
        Directory.Delete(root, true)

[<Fact>]
let ``The example script runs cleanly`` () =
    let result = Script.compileFile (Path.Combine(repositoryRoot, "examples", "directives.nyx"))
    Assert.Empty(result.Diagnostics)
    Assert.True(result.Typed.IsSome)
