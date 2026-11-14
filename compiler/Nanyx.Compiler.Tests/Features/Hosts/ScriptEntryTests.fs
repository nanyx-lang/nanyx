/// A script's statements are its entry point, run by the console host's `run`, and so are its values that need a
/// context, as local defs in the order written (#1245, see docs/src/content/work-in-progress/hosts.md).
module Nanyx.Compiler.Tests.Features.Hosts.ScriptEntry

open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``A function that names a script value that needs a context is an error, reported where it names it`` () =
    let source =
        """import nanyx/console

def line = readLine()

def greet = { name -> println("{name} said {line}") }

greet("Ada")
"""
    let code, message, text = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.ScriptRunValue.Code, code)
    Assert.Equal("'line' is computed when the script runs, so a function can't name it: pass it as an argument", message)
    Assert.Equal("line", text)

[<Fact>]
let ``A function that names a script value that needs a context through another value is an error`` () =
    let source =
        """import nanyx/console

def line = readLine()

def shouted = "{line}!"

def greet = { println(shouted) }

greet()
"""
    Assert.Equal((DiagnosticCodes.ScriptRunValue.Code, "shouted"), singleError source)

[<Fact>]
let ``A statement that reads a script value that needs a context before its def is an error`` () =
    let source =
        """import nanyx/console

println(line)

def line = readLine()
"""
    let code, message, text = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.ScriptRunValue.Code, code)
    Assert.Equal("'line' is computed when the script runs, so it can't be read before its def: move the def up", message)
    Assert.Equal("line", text)

[<Fact>]
let ``A function can name a script value that needs no context, which stays at the top level`` () =
    let source =
        """import nanyx/console

def limit = 10

def small = { n -> n < limit }

println(small(3))
"""
    let typed = compileClean source
    Assert.Contains(
        typed.Module,
        fun item -> match item.Kind with Def (ValueDef(_, "limit", _, _)) -> true | _ -> false)

[<Fact>]
let ``A script value that needs a context moves into the entry point, after the statements before it`` () =
    let source =
        """import nanyx/console

println("first")

def line = readLine()

println(line)
"""
    let typed = compileClean source
    Assert.DoesNotContain(typed.Module, fun item -> match item.Kind with Def (ValueDef(_, "line", _, _)) -> true | _ -> false)
    let statements =
        typed.Module
        |> List.pick (function
            | Kind (Def (ValueDef(_, "__script", _, Kind (Lambda(_, Kind (Block statements), _))))) -> Some statements
            | _ -> None)
    let shape =
        statements
        |> List.map (fun statement ->
            match statement.Kind with
            | DefStatement(_, name, _, _) -> $"def {name}"
            | ExprStatement (Kind UnitExpr) -> "()"
            | ExprStatement _ -> "statement"
            | _ -> "other")
    Assert.Equal<string list>([ "statement"; "def line"; "statement"; "()" ], shape)

[<Fact>]
let ``A value that needs a context in a module without statements is an error, as nothing would run it`` () =
    let source =
        """import nanyx/console

def line = readLine()
"""
    let code, message, text = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.UndeclaredContext.Code, code)
    Assert.Equal("'line' needs the context 'Console' to be computed, which only a script's statements run in: compute it in a statement, or make 'line' a function", message)
    Assert.Equal("line", text)
