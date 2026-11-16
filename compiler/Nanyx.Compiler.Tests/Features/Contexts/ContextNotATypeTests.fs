module Nanyx.Compiler.Tests.Features.ContextNotAType

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A context is not a type: its name is written only in a context list and as the constructor of the instance a `use`
// loads (#1447).

let private log = """context Log = (log: string -> ())

def logged: [Log] () -> () = { log("x") }
"""

/// The text each NYX0247 that `log` followed by `source` compiles to is reported at.
let private contextsAsTypes (source: string) =
    let source = (log + source).ReplaceLineEndings "\n"
    (Compiler.compile source).Diagnostics
    |> List.filter (fun diagnostic -> diagnostic.Code = DiagnosticCodes.ContextAsType.Code)
    |> List.map (spanOf >> textAt source)

[<Fact>]
let ``A context is not a def's result type`` () =
    Assert.Equal<string list>(
        [ "Log" ],
        contextsAsTypes """def consoleLogger: [Console] () -> Log = { Log(log = { m -> println(m) }) }
""")

[<Fact>]
let ``A context is not a parameter's type`` () =
    Assert.Equal<string list>([ "Log" ], contextsAsTypes """def take = { l: Log -> () }
""")

[<Fact>]
let ``A context is not a field's type`` () =
    Assert.Equal<string list>([ "Log" ], contextsAsTypes """type Logger = (inner: Log, name: string)
""")

[<Fact>]
let ``A context is not a def's type`` () =
    Assert.Equal<string list>([ "Log" ], contextsAsTypes """def x: Log = (log = { m -> () })
""")

[<Fact>]
let ``A context is not a type argument`` () =
    Assert.Equal<string list>([ "Log" ], contextsAsTypes """def logs: list(Log) = []
""")

[<Fact>]
let ``A parameterised context is not a type, and only its name is reported`` () =
    Assert.Equal<string list>(
        [ "ToString" ],
        contextsAsTypes """def hash: ToString(int) = (toString = { n -> "#{n}" })
""")

[<Fact>]
let ``A context list is not a type`` () =
    compileClean (
        (log
         + """def twice: [Log, ToString(int)] int -> string = { n ->
  logged()
  "{n}"
}
""")
            .ReplaceLineEndings "\n")
    |> ignore

[<Fact>]
let ``An instance built as a def's value is reported at the context's name`` () =
    let code, text = singleError ((log + "def inst = Log(log = { m -> () })\n").ReplaceLineEndings "\n")
    Assert.Equal(DiagnosticCodes.InstanceOutsideUse.Code, code)
    Assert.Equal("Log", text)

[<Fact>]
let ``An instance a function returns is reported`` () =
    let code, text = singleError ((log + "def make = { Log(log = { m -> () }) }\n").ReplaceLineEndings "\n")
    Assert.Equal(DiagnosticCodes.InstanceOutsideUse.Code, code)
    Assert.Equal("Log", text)

[<Fact>]
let ``An instance written in a use, or in a handler's use, compiles`` () =
    compileClean (
        (log
         + """def statement = {
  use Log(log = { m -> () })
  logged()
}
def expression = use Log(log = { m -> () }) in logged()
def withLog = { f -> use Log(log = { m -> () }) in f() }
""")
            .ReplaceLineEndings "\n")
    |> ignore
