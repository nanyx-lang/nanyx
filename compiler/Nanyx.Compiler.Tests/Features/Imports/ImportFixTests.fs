module Nanyx.Compiler.Tests.Features.ImportFixes

open Xunit
open NyxCompiler

/// The diagnostic `source` compiles to: it must have just one.
let private onlyDiagnostic (result: CompileResult) =
    match result.Diagnostics with
    | [ diagnostic ] -> diagnostic
    | diagnostics -> failwith $"Expected one diagnostic, got %A{diagnostics}"

/// `source` with the edits of `fix` applied.
let private applied (source: string) (fix: Fix) =
    fix.Edits
    |> List.sortByDescending (fun edit -> edit.Span.Start)
    |> List.fold (fun (text: string) edit -> text.Substring(0, edit.Span.Start) + edit.NewText + text.Substring edit.Span.End) source

/// The title of each fix offered for the one diagnostic `source` compiles to, with the source that fix gives.
let private fixesFor (source: string) =
    (onlyDiagnostic (Compiler.compile source)).Fixes |> List.map (fun fix -> fix.Title, applied source fix)

[<Fact>]
let ``A function a standard-library module exports offers its import at the top`` () =
    let source = "def s = String.trim(\" a \")\n"
    Assert.Equal<(string * string) list>(
        [ "Add `import nanyx/text`", "import nanyx/text\n\ndef s = String.trim(\" a \")\n" ],
        fixesFor source)

[<Fact>]
let ``The fix is for the unknown function's diagnostic`` () =
    let diagnostic = onlyDiagnostic (Compiler.compile "def n = abs(1)\n")
    Assert.Equal(DiagnosticCodes.UnknownFunction.Code, diagnostic.Code)
    Assert.Equal<string list>([ "Add `import nanyx/math`" ], diagnostic.Fixes |> List.map _.Title)

[<Fact>]
let ``The import goes after the last import`` () =
    let source = "import nanyx/console\nimport nanyx/text\n\ndef n = abs(1)\n"
    Assert.Equal<(string * string) list>(
        [ "Add `import nanyx/math`", "import nanyx/console\nimport nanyx/text\nimport nanyx/math\n\ndef n = abs(1)\n" ],
        fixesFor source)

[<Fact>]
let ``The import goes after the module line when there are no imports`` () =
    let source = "module main\n\ndef n = abs(1)\n"
    Assert.Equal<(string * string) list>(
        [ "Add `import nanyx/math`", "module main\n\nimport nanyx/math\n\ndef n = abs(1)\n" ],
        fixesFor source)

[<Fact>]
let ``The import goes after the directives when there are no imports, since directives come before any code`` () =
    let source = "#!/usr/bin/env nanyx\n::linters.default\n\ndef a = sqrt(4.0)\n"
    Assert.Equal<(string * string) list>(
        [ "Add `import nanyx/math`", "#!/usr/bin/env nanyx\n::linters.default\n\nimport nanyx/math\n\ndef a = sqrt(4.0)\n" ],
        fixesFor source)

[<Fact>]
let ``The edit is an insertion where the import goes`` () =
    let source = "import nanyx/console\n\ndef n = abs(1)\n"
    let edit = (onlyDiagnostic (Compiler.compile source)).Fixes |> List.exactlyOne |> _.Edits |> List.exactlyOne
    Assert.Equal((21, 21, 1, 0, "import nanyx/math\n"), (edit.Span.Start, edit.Span.End, edit.Span.StartLine, edit.Span.StartColumn, edit.NewText))

[<Fact>]
let ``A name qualified by a module's name offers that module imported under its name`` () =
    let source = "def n = math.abs(1)\n"
    Assert.Equal<(string * string) list>(
        [ "Add `import nanyx/math as math`", "import nanyx/math as math\n\ndef n = math.abs(1)\n" ],
        fixesFor source)

[<Fact>]
let ``A name no standard-library module exports offers no fix`` () =
    let diagnostic = onlyDiagnostic (Compiler.compile "def n = frobnicate(1)\n")
    Assert.Equal(DiagnosticCodes.UnknownFunction.Code, diagnostic.Code)
    Assert.Empty diagnostic.Fixes

[<Fact>]
let ``An unknown identifier offers its import too`` () =
    let diagnostic = onlyDiagnostic (Compiler.compile "def f = abs\n")
    Assert.Equal(DiagnosticCodes.UnknownIdentifier.Code, diagnostic.Code)
    Assert.Equal<string list>([ "Add `import nanyx/math`" ], diagnostic.Fixes |> List.map _.Title)

[<Fact>]
let ``A name several modules export offers one fix for each`` () =
    let source = "import nanyx/console\n\ndef n = frobnicate(1)\n"
    let result = Compiler.compile source
    let exports = Map.ofList [ "frobnicate", [ "alpha"; "beta" ] ]
    let diagnostic = ImportFixes.addWith exports source result.Syntax.Value result.Diagnostics |> List.exactlyOne
    Assert.Equal<(string * string) list>(
        [ "Add `import nanyx/alpha`", "import nanyx/console\nimport nanyx/alpha\n\ndef n = frobnicate(1)\n"
          "Add `import nanyx/beta`", "import nanyx/console\nimport nanyx/beta\n\ndef n = frobnicate(1)\n" ],
        diagnostic.Fixes |> List.map (fun fix -> fix.Title, applied source fix))

[<Fact>]
let ``The standard library's exports leave out the prelude's`` () =
    let exports = ImportFixes.standardLibraryExports.Value
    Assert.Equal<string list>([ "text" ], exports.["String.trim"])
    Assert.False(exports.ContainsKey "List.map")
