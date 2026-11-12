module Nanyx.Compiler.Tests.Features.Linters.ShadowedPreludeNames

open System.IO
open Xunit
open NyxCompiler

let private code = "default/shadowedPreludeNames"

/// The diagnostics of `result`, which must type-check, or no linter would run.
let private checkedDiagnostics (result: CompileResult) =
    Assert.True(result.TypeChecked && not (result.Diagnostics |> List.exists Diagnostics.isError), sprintf "%A" result.Diagnostics)
    result.Diagnostics

/// What `default/shadowedPreludeNames` reports in `diagnostics` of `source`: each diagnostic's message with the text
/// it is reported at.
let private reportedIn (source: string) (diagnostics: Diagnostic list) =
    diagnostics
    |> List.filter (fun diagnostic -> diagnostic.Code = code)
    |> List.map (fun diagnostic ->
        Assert.Equal(Severity.Warning, diagnostic.Severity)
        Assert.Empty(diagnostic.Fixes)
        let span = diagnostic.Span.Value
        source.Substring(span.Start, span.End - span.Start), diagnostic.Message)

/// What `default/shadowedPreludeNames` reports for `source`, compiled with every linter on. Its line endings are made
/// `\n`, as for spans.
let private reported (source: string) =
    let source = source.ReplaceLineEndings "\n"
    Compiler.compileWith Linters.everyLinter source |> checkedDiagnostics |> reportedIn source

/// The names `default/shadowedPreludeNames` reports in `source`, compiled as the file `file`.
let private reportedAsFile (file: string) (source: string) =
    let source = source.ReplaceLineEndings "\n"
    Compiler.compileSourceWith Linters.everyLinter file source |> checkedDiagnostics |> reportedIn source |> List.map fst

[<Fact>]
let ``A top-level def is reported at its name, saying what it hides`` () =
    let source =
        """def pow = { x: int -> x * x }

export def nine: int = pow(3)
"""
    Assert.Equal<(string * string) list>(
        [ "pow", "`pow` hides the prelude's `pow`, which can't be named where it is in scope" ],
        reported source)

[<Fact>]
let ``A local def is reported`` () =
    let source =
        """export def total: int -> int = { x ->
  def panic = x + 1
  panic * 2
}
"""
    Assert.Equal<string list>([ "panic" ], reported source |> List.map fst)

[<Fact>]
let ``A lambda's argument is reported`` () =
    let source =
        """export def apply: (int -> int), int -> int = { ignore, x -> ignore(x) }
"""
    Assert.Equal<string list>([ "ignore" ], reported source |> List.map fst)

[<Fact>]
let ``A variable a pattern binds is reported`` () =
    let source =
        """export def unwrap: (#some(int) | #none) -> int = {
  | #some(memory) -> memory
  | #none -> 0
}
"""
    Assert.Equal<string list>([ "memory" ], reported source |> List.map fst)

[<Fact>]
let ``A name the prelude attaches to a type is not reported`` () =
    let source =
        """export def doubled: list(int) -> list(int) = { xs ->
  def map = { f, ys: list(int) -> list.map(ys, f) }
  map({ x -> x * 2 }, xs)
}

export def length: int = 3
"""
    Assert.Empty(reported source)

[<Fact>]
let ``A context's member is not reported where the context is not required`` () =
    let source =
        """export def toString: int -> string = { _ -> "my own" }
"""
    Assert.Empty(reported source)

[<Fact>]
let ``An intrinsic's name is not reported`` () =
    let source =
        """export def __nanyx_string_length: int = 1
"""
    Assert.Empty(reported source)

[<Fact>]
let ``The wildcard is not reported`` () =
    let source =
        """export def first: int, int -> int = { x, _ -> x }
"""
    Assert.Empty(reported source)

[<Fact>]
let ``A module of the standard library is not reported`` () =
    let source =
        """module math

export def pow: int -> int = { x -> x * x }
"""
    Assert.Empty(reportedAsFile (Path.Combine("stdlib", "nanyx", "math.nyx")) source)

[<Fact>]
let ``A file named as a standard library module outside the library's folder is reported`` () =
    let source =
        """module math

export def pow: int -> int = { x -> x * x }
"""
    Assert.Equal<string list>([ "pow" ], reportedAsFile (Path.Combine("src", "math.nyx")) source)
