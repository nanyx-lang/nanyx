/// A `#delay` keyword's call, `k! e`, is `k({ e })`, so a local, a parameter or a pattern's variable named `k` hides the
/// keyword's def. One that doesn't fit is reported at the keyword, naming it (#2607).
module Nanyx.Compiler.Tests.Features.HiddenKeywords

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// `source` after the import of what starts tasks.
let private withTasks (source: string) =
    withFinalNewline ("import nanyx/async as (Async)\n\n" + normalized source)

/// Each diagnostic of compiling `source`: its code, its message, and the text it is reported at.
let private diagnosticsOf (source: string) =
    (Compiler.compile source).Diagnostics
    |> List.map (fun diagnostic -> diagnostic.Code, diagnostic.Message, textAt source (spanOf diagnostic))

[<Fact>]
let ``A local that hides start makes each start! an error at the keyword, naming the local`` () =
    let source =
        withTasks
            """def work = { _ ->
  def start = 5
  def t1 = start! 1 + 2
  def t2 = start! 3
  start
}"""
    let message = "'start!' calls the local 'start' defined at line 4, of type 'int', not 'Async.start': rename the local, or write 'Async.start({ ... })'"
    Assert.Equal<(string * string * string) list>(
        [ DiagnosticCodes.TypeMismatch.Code, message, "start!"; DiagnosticCodes.TypeMismatch.Code, message, "start!" ],
        diagnosticsOf source)

[<Fact>]
let ``A parameter that hides start is named as one`` () =
    let source = withTasks "def work = { start: int -> start! 1 }"
    let code, message, at = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.StartsWith("'start!' calls the parameter 'start' defined at line 3, of type 'int'", message)
    Assert.Equal("start!", at)

[<Fact>]
let ``A local that hides start and takes a block is called with no error`` () =
    let source =
        withTasks
            """def work = { _ ->
  def start = { f -> f() }
  start! 3
}"""
    Assert.Empty((Compiler.compile source).Diagnostics)
