module Nanyx.Compiler.Tests.Features.Linters.UnusedImports

open System.IO
open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// What `default/unusedImports` reports for `source`, compiled with every linter on: each message with the text it is
/// reported at. The other rules' diagnostics are left out. The module must type-check, or no linter would run.
let private unusedImportsIn (result: CompileResult) (source: string) =
    Assert.True(result.TypeChecked && not (result.Diagnostics |> List.exists Diagnostics.isError), sprintf "%A" result.Diagnostics)
    result.Diagnostics
    |> List.filter (fun diagnostic -> diagnostic.Code = "default/unusedImports")
    |> List.map (fun diagnostic ->
        Assert.Equal(Severity.Warning, diagnostic.Severity)
        let span = diagnostic.Span.Value
        diagnostic.Message, source.Substring(span.Start, span.End - span.Start))

let private reported (source: string) = unusedImportsIn (Compiler.compileWith Linters.everyLinter source) source

/// `source` with the only fix of its only `unusedImports` diagnostic applied, and the fix's title.
let private fixed' (source: string) =
    let diagnostic =
        Assert.Single(
            (Compiler.compileWith Linters.everyLinter source).Diagnostics
            |> List.filter (fun diagnostic -> diagnostic.Code = "default/unusedImports")
        )
    let fix = Assert.Single(diagnostic.Fixes)
    applyFix source fix, fix.Title

[<Fact>]
let ``An import used through its alias is not reported`` () =
    Assert.Empty(reported "import nanyx/math as m\ndef x = m.abs(3)\n")

[<Fact>]
let ``An import used unqualified is not reported`` () =
    Assert.Empty(reported "import nanyx/math\ndef x = abs(3)\n")

[<Fact>]
let ``An import used through a name it selects is not reported`` () =
    Assert.Empty(reported "import nanyx/math as (abs)\ndef x = abs(3)\n")
    Assert.Empty(reported "import nanyx/math as m & (abs)\ndef x = abs(3)\n")

[<Fact>]
let ``An import that selects names nothing uses is reported`` () =
    Assert.Equal<(string * string) list>([ "`nanyx/math` is imported but never used", "nanyx/math as (abs)" ], reported "import nanyx/math as (abs)\ndef x = 3\n")

[<Fact>]
let ``An import used only through a name the module re-exports is not reported`` () =
    withTempDir (fun dir ->
        File.WriteAllText(Path.Combine(dir, "util.nyx"), "module util\nexport def double: int -> int = { x -> x * 2 }")
        File.WriteAllText(Path.Combine(dir, "c.nyx"), "module c\nexport util (double)")
        let source = "module main\nimport c\ndef x = double(3)\n"
        let mainPath = Path.Combine(dir, "main.nyx")
        File.WriteAllText(mainPath, source)
        Assert.Empty(unusedImportsIn (Compiler.compileFileWith Linters.everyLinter mainPath) source))

[<Fact>]
let ``An import used only in a type is not reported`` () =
    Assert.Empty(reported "import nanyx/console as c\ndef e: c.IoError = #endOfInput\n")
    Assert.Empty(reported "import nanyx/console\ndef e: IoError = #endOfInput\n")

[<Fact>]
let ``A module imported by the file and by a module block is used by each where it is used`` () =
    let block = "module inner =\n  import nanyx/text\n  export def a = string.trim(\" x \")\n"
    Assert.Empty(reported ("import nanyx/text\n" + block + "def b = string.trim(\" y \")\n"))
    // The block's import is its own: a use in the block does not use the file's, nor one outside it the block's.
    Assert.Equal<(string * string) list>(
        [ "`nanyx/text` is imported but never used", "nanyx/text" ],
        reported ("import nanyx/text\n" + block + "def b = 1\n"))
    let source = "import nanyx/text\nmodule inner =\n  import nanyx/text\n  export def a = 1\ndef b = string.trim(\" y \")\n"
    let reportedAt =
        (Compiler.compileWith Linters.everyLinter source).Diagnostics
        |> List.filter (fun diagnostic -> diagnostic.Code = "default/unusedImports")
        |> List.map (fun diagnostic -> diagnostic.Span.Value.StartLine)
    Assert.Equal<int list>([ 2 ], reportedAt)

[<Fact>]
let ``An unused import is reported at the import, under its alias or its module`` () =
    let source = "import (\n  nanyx/math as m\n  nanyx/text as s\n  nanyx/console\n)\ndef x = m.abs(3)\n"
    Assert.Equal<(string * string) list>(
        [ "`s` is imported but never used", "nanyx/text as s"
          "`nanyx/console` is imported but never used", "nanyx/console" ],
        reported source)

[<Fact>]
let ``The prelude, the import entry adds, and aliases starting with an underscore are not reported`` () =
    Assert.Empty(reported "@entry\ndef main = {\n  def x = list.length([1, 2])\n  x + 1\n}")
    Assert.Empty(reported "import nanyx/math as _m\ndef x = 1\n")

[<Fact>]
let ``An unused import in a file that imports it by path is reported`` () =
    withTempDir (fun dir ->
        File.WriteAllText(Path.Combine(dir, "lib.nyx"), "export def helper = 1\n")
        let mainPath = Path.Combine(dir, "main.nyx")
        let source = "import \"./lib.nyx\" as lib\ndef answer = 2\n"
        File.WriteAllText(mainPath, source)
        let result = Compiler.compileFileWith Linters.everyLinter mainPath
        Assert.Equal<(string * string) list>([ "`lib` is imported but never used", "\"./lib.nyx\" as lib" ], unusedImportsIn result source))

[<Fact>]
let ``The rule's severity can be configured, or the rule turned off`` () =
    let source = "import nanyx/math as m\ndef x = 1\n"
    let compileWith severity =
        Compiler.compileWith { Linters.everyLinter with Severities = Map.ofList [ "default/unusedImports", severity ] } source
    let importDiagnostics severity =
        (compileWith severity).Diagnostics |> List.filter (fun diagnostic -> diagnostic.Code = "default/unusedImports")
    Assert.Empty(importDiagnostics None)
    let promoted = Assert.Single(importDiagnostics (Some Severity.Error))
    Assert.Equal(Severity.Error, promoted.Severity)

[<Fact>]
let ``The fix for an import on its own removes the statement's line`` () =
    Assert.Equal(("def x = 1\n", "Remove the import of `m`"), fixed' "import nanyx/math as m\ndef x = 1\n")
    Assert.Equal(("def x = 1\n", "Remove the import of `nanyx/math`"), fixed' "import (\n  nanyx/math\n)\ndef x = 1\n")

[<Fact>]
let ``The fix for one import of several removes its line, or the import and the space before it`` () =
    Assert.Equal(
        ("import (\n  nanyx/math as m\n)\ndef x = m.abs(3)\n", "Remove the import of `s`"),
        fixed' "import (\n  nanyx/math as m\n  nanyx/text as s -- for later\n)\ndef x = m.abs(3)\n")
    Assert.Equal(
        ("import (nanyx/math as m)\ndef x = m.abs(3)\n", "Remove the import of `s`"),
        fixed' "import (nanyx/math as m nanyx/text as s)\ndef x = m.abs(3)\n")
