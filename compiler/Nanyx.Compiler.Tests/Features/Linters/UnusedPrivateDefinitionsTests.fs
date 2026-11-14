module Nanyx.Compiler.Tests.Features.Linters.UnusedPrivateDefinitions

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

let private code = "default/unusedPrivateDefinitions"

/// What `default/unusedPrivateDefinitions` reports for `source`, compiled with every linter on: each message with the
/// text it is reported at. The other rules' diagnostics are left out. The module must type-check, or no linter would
/// run.
let private reported (source: string) =
    let result = Compiler.compileWith Linters.everyLinter source
    Assert.True(result.TypeChecked && not (result.Diagnostics |> List.exists Diagnostics.isError), sprintf "%A" result.Diagnostics)
    result.Diagnostics
    |> List.filter (fun diagnostic -> diagnostic.Code = code)
    |> List.map (fun diagnostic ->
        Assert.Equal(Severity.Warning, diagnostic.Severity)
        let span = diagnostic.Span.Value
        diagnostic.Message, source.Substring(span.Start, span.End - span.Start))

/// `source` with the only fix of its only `unusedPrivateDefinitions` diagnostic applied, and the fix's title.
let private fixed' (source: string) =
    let diagnostic =
        Assert.Single((Compiler.compileWith Linters.everyLinter source).Diagnostics |> List.filter (fun diagnostic -> diagnostic.Code = code))
    let fix = Assert.Single(diagnostic.Fixes)
    applyFix source fix, fix.Title

[<Fact>]
let ``An unused private def, type and context are reported at their names`` () =
    // The example from the official linter's page in the docs.
    let source =
        normalized """module shapes
def scale = 2
type Corner = (x: int)
context Logger = (log: string -> ())
export def area = { w, h -> w * h }
"""
    Assert.Equal<(string * string) list>(
        [ "`scale` is never used", "scale"
          "Type `Corner` is never used", "Corner"
          "Type `Logger` is never used", "Logger"
          "Context member `log` is never used", "log" ],
        reported source)

[<Fact>]
let ``Exported definitions are not reported, used or not`` () =
    Assert.Empty(reported "module shapes\nexport def scale = 2\nexport type Corner = (x: int)\nexport context Logger = (log: string -> ())\n")

[<Fact>]
let ``A private def, type or context member that the module uses is not reported`` () =
    let source =
        normalized """module shapes
type Corner = (x: int)
context Scale = (factor: () -> int)
def scale = 2
def corner: Corner = (x = scale)
def scaled: [Scale] int -> int = { n -> n * factor() }
export def area: [Scale] int -> int = { w -> scaled(w) + corner.x }
"""
    Assert.Empty(reported source)
    // A type that only a call makes a value of is used.
    Assert.Empty(reported "module m\ntype Corner = (x: int)\nexport def origin = Corner(x = 0)\n")

[<Fact>]
let ``A member of a used context that nothing calls is reported`` () =
    let source =
        normalized """module shapes
context Scale = (factor: () -> int, offset: () -> int)
export def scaled: [Scale] int -> int = { n -> n * factor() }
"""
    Assert.Equal<(string * string) list>([ "Context member `offset` is never used", "offset" ], reported source)

[<Fact>]
let ``A def that only calls itself, or a type that only names itself, is unused`` () =
    let source =
        normalized """module m
def countdown: int -> int = { n -> if n == 0 then 0 else countdown(n - 1) }
type Tree = #leaf | #node(Tree, Tree)
export def x = 1
"""
    Assert.Equal<(string * string) list>(
        [ "`countdown` is never used", "countdown"; "Type `Tree` is never used", "Tree" ],
        reported source)

[<Fact>]
let ``The entry point, defs an instance picks up and names starting with an underscore are not reported`` () =
    Assert.Empty(reported "@entry\ndef main = {\n  println(\"hi\")\n}\n")
    let attached =
        normalized """module points
type Point = (x: int, y: int)
def Point.toString = { p -> "({p.x}, {p.y})" }
export def s = "at {Point(x = 1, y = 2)}"
"""
    Assert.Empty(reported attached)
    Assert.Empty(reported "module m\ndef _spare = 1\ntype _Later = (x: int)\n")

[<Fact>]
let ``A module block's private def that the block does not use is reported`` () =
    let source =
        normalized """module outer =
  def helper = 1
  def unused = 2
  export def value = helper + 1
def total = outer.value
export def answer = total
"""
    Assert.Equal<string list>([ "unused" ], reported source |> List.map snd)

[<Fact>]
let ``The fix removes the definition's lines`` () =
    let text, title = fixed' "module m\ndef scale = 2 -- for later\nexport def x = 1\n"
    Assert.Equal("Remove `scale`", title)
    Assert.Equal("module m\nexport def x = 1\n", text)
    let multiline, _ = fixed' "module m\ndef twice = { n ->\n  n * 2\n}\nexport def x = 1\n"
    Assert.Equal("module m\nexport def x = 1\n", multiline)
    let typeText, _ = fixed' "module m\ntype Corner = (x: int)\nexport def x = 1\n"
    Assert.Equal("module m\nexport def x = 1\n", typeText)

[<Fact>]
let ``An unused context member has no fix`` () =
    let source = "module m\ncontext Scale = (factor: () -> int, offset: () -> int)\nexport def f: [Scale] int -> int = { n -> n * factor() }\n"
    let diagnostic = (Compiler.compileWith Linters.everyLinter source).Diagnostics |> List.find (fun diagnostic -> diagnostic.Code = code)
    Assert.Empty(diagnostic.Fixes)

[<Fact>]
let ``The rule can be turned off on its own`` () =
    let source = "module m\ndef scale = 2\n"
    let result = Compiler.compileWith { Linters.everyLinter with Severities = Map.ofList [ code, None ] } source
    Assert.Empty(result.Diagnostics)
