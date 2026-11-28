module Nanyx.Compiler.Tests.Features.Linters.UnconstructibleType

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// What `default/unconstructibleType` reports for `source`, compiled with every linter on: each diagnostic with the
/// text it is reported at. The module must type-check, or no linter would run.
let private diagnostics (source: string) =
    let source = withFinalNewline source
    let result = Compiler.compileWith (everyLinter ()) source
    Assert.True(result.TypeChecked, sprintf "%A" result.Diagnostics)
    result.Diagnostics
    |> List.filter (fun diagnostic -> diagnostic.Code = "default/unconstructibleType")
    |> List.map (fun diagnostic ->
        Assert.Equal(Severity.Error, diagnostic.Severity)
        let span = diagnostic.Span.Value
        diagnostic, source.Substring(span.Start, span.End - span.Start))

let private reported (source: string) =
    diagnostics source |> List.map (fun (diagnostic, text) -> diagnostic.Message, text)

[<Fact>]
let ``A private type that nothing its module exports returns is reported at its name`` () =
    let source =
        """module noms =
  export type UserId = private int

  export def UserId.toString: UserId -> string = { id -> "user" }"""
    Assert.Equal<(string * string) list>(
        [ "'UserId' is private, and nothing 'noms' exports returns one, so no code outside 'noms' can get a 'UserId'", "UserId" ],
        reported source)

[<Fact>]
let ``A protected type in a file module is reported, by the module's name`` () =
    let source =
        """module emails

export type Email = protected string

export def Email.length: Email -> int = { e -> string.length(e) }"""
    Assert.Equal<(string * string) list>(
        [ "'Email' is protected, and nothing 'emails' exports returns one, so no code outside 'emails' can get a 'Email'", "Email" ],
        reported source)

[<Fact>]
let ``The fix exports an of that wraps the representation, and the rule is satisfied`` () =
    let source =
        """module noms =
  export type UserId = private int

  export def UserId.toString: UserId -> string = { id -> "user" }"""
    let diagnostic, _ = List.exactlyOne (diagnostics source)
    let fix = List.exactlyOne diagnostic.Fixes
    Assert.Equal("Export `UserId.of`, which makes a `UserId`", fix.Title)
    let fixedSource = applyFix (withFinalNewline source) fix
    Assert.Equal(
        (withFinalNewline
            """module noms =
  export type UserId = private int

  export def UserId.of: int -> UserId = { value -> UserId(value) }

  export def UserId.toString: UserId -> string = { id -> "user" }"""),
        fixedSource)
    Assert.Empty(reported fixedSource)

[<Fact>]
let ``A def that gives one out in any output position counts as a producer`` () =
    let producers =
        [ """export def UserId.parse: string -> #some(UserId) | #invalid = { s -> #some(UserId(1)) }"""
          """export def UserId.all: () -> list(UserId) = { -> [ UserId(1) ] }"""
          """export def UserId.default: UserId = UserId(0)"""
          """export def admins: (first: UserId, count: int) = (first = UserId(0), count = 1)"""
          """export def withUser: (UserId -> a) -> a = { f -> f(UserId(1)) }""" ]
    for producer in producers do
        let source =
            $"""module noms =
  export type UserId = private int

  {producer}"""
        Assert.Empty(reported source)

[<Fact>]
let ``A protected type that gives out its field gives out what the field holds`` () =
    let source =
        """module noms =
  export type UserId = private int

  export type Session = protected (user: UserId)

  export def Session.start: () -> Session = { -> Session(user = UserId(1)) }"""
    Assert.Empty(reported source)

[<Fact>]
let ``A def that only takes the type, or a callback that gives one back, is no producer`` () =
    let source =
        """module noms =
  export type UserId = private int

  export def UserId.toString: UserId -> string = { id -> "user" }

  export def check: (() -> UserId) -> bool = { f -> true }"""
    Assert.Equal<string list>([ "UserId" ], reported source |> List.map snd)

[<Fact>]
let ``A type the module does not export, a structural type and a context are not reported`` () =
    let source =
        """module noms =
  type Hidden = private int

  export type Plain = int

  export context Tx = private (commit: () -> ())

  export def Hidden.toString: Hidden -> string = { h -> "hidden" }"""
    Assert.Empty(reported source)

[<Fact>]
let ``An allow before the type suppresses it`` () =
    let source =
        """module noms =
  @allow(default/unconstructibleType)
  export type Marker = private ()"""
    let result = Compiler.compileWith (everyLinter ()) (withFinalNewline source)
    Assert.True(result.TypeChecked, sprintf "%A" result.Diagnostics)
    let codes = result.Diagnostics |> List.map _.Code
    Assert.DoesNotContain("default/unconstructibleType", codes)
    // The `@allow` suppressed something, so it is not reported as suppressing nothing.
    Assert.DoesNotContain("default/unusedAllows", codes)
