module Nanyx.Compiler.Tests.Features.Linters.DollarPrefixedType

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// What `default/dollarPrefixedType` reports for `source`, compiled with every linter on: each diagnostic with the
/// text it is reported at. The module must type-check, or no linter would run.
let private reported (source: string) =
    let result = Compiler.compileWith Linters.everyLinter source
    Assert.True(result.TypeChecked && not (result.Diagnostics |> List.exists Diagnostics.isError), sprintf "%A" result.Diagnostics)
    result.Diagnostics
    |> List.filter (fun diagnostic -> diagnostic.Code = "default/dollarPrefixedType")
    |> List.map (fun diagnostic ->
        Assert.Equal(Severity.Warning, diagnostic.Severity)
        let span = diagnostic.Span.Value
        diagnostic, source.Substring(span.Start, span.End - span.Start))

[<Fact>]
let ``Each $ before a type's name is reported: its declaration, types, constructors, patterns and attached definitions`` () =
    let source =
        """type $Email = string
def $Email.parse: string -> $Email = { s -> $Email(s) }
def raw: Email -> string = { $Email(s) -> s }
def e = "a" \$Email.parse
"""
    let source = source.ReplaceLineEndings "\n"
    let reports = reported source
    // Every `$` in the source is one.
    let dollars = [ 0 .. source.Length - 1 ] |> List.filter (fun offset -> source.[offset] = '$')
    Assert.Equal(6, dollars.Length)
    Assert.Equal<int list>(dollars, reports |> List.map (fun (diagnostic, _) -> diagnostic.Span.Value.Start))

[<Fact>]
let ``The declaration's fix writes protected after the =`` () =
    let source = "type $Email = string\ndef e: Email = Email(\"a\")\n"
    match reported source with
    | [ diagnostic, "$" ] ->
        Assert.Equal(
            "Declare a protected type with `protected` after its `=`, not a `$` before its name: `type Email = protected ...`",
            diagnostic.Message)
        Assert.Equal("type Email = protected string\ndef e: Email = Email(\"a\")\n", applyFix source diagnostic.Fixes.Head)
    | other -> failwith $"Expected one report, got %A{other}"

[<Fact>]
let ``A context's and a record type's declarations are fixed the same way`` () =
    let source = "scoped context $Tx = ()\ntype $User = (name: string)\n"
    let fixedAll = reported source |> List.rev |> List.fold (fun text (diagnostic, _) -> applyFix text diagnostic.Fixes.Head) source
    Assert.Equal("scoped context Tx = protected ()\ntype User = protected (name: string)\n", fixedAll)

[<Fact>]
let ``A private type's fix only removes the $`` () =
    let source = "type $UserId = private int\n"
    match reported source with
    | [ diagnostic, "$" ] -> Assert.Equal("type UserId = private int\n", applyFix source diagnostic.Fixes.Head)
    | other -> failwith $"Expected one report, got %A{other}"

[<Fact>]
let ``A use's fix removes the $`` () =
    let source = "type Email = protected string\ndef e: $Email = Email(\"a\")\n"
    match reported source with
    | [ diagnostic, "$" ] ->
        Assert.Equal("A type's name is written without a `$`", diagnostic.Message)
        Assert.Equal("type Email = protected string\ndef e: Email = Email(\"a\")\n", applyFix source diagnostic.Fixes.Head)
    | other -> failwith $"Expected one report, got %A{other}"

[<Fact>]
let ``A $ in a string or a comment, and a type declared protected, are not reported`` () =
    let source = "-- type $Email = string\ntype Email = protected string\ndef s = \"$Email\"\n"
    Assert.Empty(reported source)
