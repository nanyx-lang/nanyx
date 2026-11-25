/// Every def is the scope of the tasks it starts, and a task shares only immutable values (#1890, see "Structured
/// concurrency" in docs/src/content/work-in-progress/concurrency-and-crash-boundaries.md).
module Nanyx.Compiler.Tests.Features.Tasks.TaskScope

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// `source` after the import of what starts and awaits tasks.
let private withTasks (source: string) =
    withFinalNewline ("import nanyx/async as (Async, Task)\n\n" + normalized source)

[<Fact>]
let ``A def that starts tasks doesn't require their scope`` () =
    let source =
        withTasks
            """def both = { _ ->
  def a = start({ 1 + 2 })
  def b = start({ "x" })
  "{await(a)} {await(b)}"
}"""
    Assert.Equal(Some "def both: [Async] α -> string", hoverType source "both")

[<Fact>]
let ``A def that starts tasks and calls itself last is shown as written, not as the def its body runs in`` () =
    // It is two defs, one opening the scope and one running the body in it (see `TaskScopes`), and the first is the
    // one written.
    let annotated =
        withTasks "def work: [Async] int, int -> int = { n, acc ->\n  if n == 0 then acc else work(n - 1, acc + await(start! n))\n}"
    Assert.Equal(Some "def work: [Async] int, int -> int", hoverType annotated "work")
    let inferred = withTasks "def work = { n, acc ->\n  if n == 0 then acc else work(n - 1, acc + await(start! n))\n}"
    Assert.Equal(Some "def work: [Async] int, int -> int", hoverType inferred "work")

[<Fact>]
let ``A task given back from the def that started it is the escape of its scope`` () =
    let source = withTasks "def leaks = { _ -> start({ 1 }) }"
    let code, message, at = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.ScopedContextEscapes.Code, code)
    Assert.StartsWith("A task can't leave the def that started it", message)
    Assert.Equal("start({ 1 })", at)

[<Fact>]
let ``A lambda that starts a task, given back from its def, is the escape of its scope`` () =
    let source = withTasks "def starter = { _ -> { start({ 1 }) } }"
    let code, message, at = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.ScopedContextEscapes.Code, code)
    Assert.StartsWith("A task can't leave the def that started it: 'starter' gives back what starts one", message)
    Assert.Equal("start({ 1 })", at)

[<Fact>]
let ``A function that awaits a task it is given uses its caller's scope`` () =
    let source =
        withTasks
            """def twice = { t: Task(int) -> await(t) * 2 }

def main = { _ -> twice(start({ 21 })) }"""
    Assert.Empty((Compiler.compile source).Diagnostics)

[<Fact>]
let ``A task given back from a local def stays in the def it is written in`` () =
    let source =
        withTasks
            """def outer = { _ ->
  def fetch = { n: int -> start({ n * 2 }) }
  await(fetch(21))
}"""
    Assert.Empty((Compiler.compile source).Diagnostics)

[<Fact>]
let ``Starting a task whose function uses a cell is an error`` () =
    let source =
        withTasks
            """def counts = { _ ->
  memory {
    mut total = 0
    def t = start({ total := total + 1 })
    await(t)
    total
  }
}"""
    let code, _, at = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.TaskHoldsMemory.Code, code)
    Assert.Equal("{ total := total + 1 }", at)

[<Fact>]
let ``Starting a task whose function holds an MRef is an error`` () =
    let source =
        withFinalNewline
            """import nanyx/async as (Async)
import nanyx/mref

def reads = { _ ->
  memory {
    def r = MRef.new(1)
    await(start({ MRef.get(r) }))
  }
}"""
    let code, _, at = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.TaskHoldsMemory.Code, code)
    Assert.Equal("{ MRef.get(r) }", at)

[<Fact>]
let ``Starting a task through another name for start with a function that holds an MRef is an error`` () =
    let source =
        withFinalNewline
            """import nanyx/async as (Async)
import nanyx/mref

def reads = { _ ->
  def go = start
  memory {
    def r = MRef.new([1, 2, 3])
    go({ MRef.get(r) })
    1
  }
}"""
    let code, _, at = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.TaskHoldsMemory.Code, code)
    Assert.Equal("{ MRef.get(r) }", at)

[<Fact>]
let ``Starting a task through a local def that passes on its function, with one that uses a cell, is an error`` () =
    let source =
        withTasks
            """def counts = { _ ->
  def spawn = { f -> start(f) }
  memory {
    mut total = 0
    spawn({ total := total + 1 })
    total
  }
}"""
    let code, _, at = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.TaskHoldsMemory.Code, code)
    Assert.Equal("{ total := total + 1 }", at)

[<Fact>]
let ``Racing functions that use a cell is an error`` () =
    let source =
        withFinalNewline
            """import nanyx/async as (Async)

def counts = { _ ->
  memory {
    mut total = 0
    def winner = race([{ sleep(1); total := total + 1; "a" }, { sleep(1); total := total + 10; "b" }])
    "{winner} {total}"
  }
}"""
    let code, _, at = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.TaskHoldsMemory.Code, code)
    Assert.Equal("""[{ sleep(1); total := total + 1; "a" }, { sleep(1); total := total + 10; "b" }]""", at)

[<Fact>]
let ``A local def that passes on its function may start a task with one that holds nothing of a block`` () =
    let source =
        withTasks
            """def counts = { _ ->
  def spawn = { f -> start(f) }
  memory {
    mut total = 41
    def now = total
    await(spawn({ now + 1 }))
  }
}"""
    Assert.Empty((Compiler.compile source).Diagnostics)

[<Fact>]
let ``A task may be given what a cell holds`` () =
    let source =
        withTasks
            """def counts = { _ ->
  memory {
    mut total = 41
    def now = total
    await(start({ now + 1 }))
  }
}"""
    Assert.Empty((Compiler.compile source).Diagnostics)

[<Fact>]
let ``A def that starts a task without naming start is told so at the call`` () =
    let source =
        withTasks
            """def go = Async.start

def f = { _ -> go({ println("task runs") }) }

f()"""
    let code, message, at = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.UndeclaredContext.Code, code)
    Assert.StartsWith("'go' starts a task, but 'f' is not the scope of its tasks", message)
    Assert.Equal("""go({ println("task runs") })""", at)

[<Fact>]
let ``A def that names Async.start is a scope in a module that defines its own start`` () =
    let source =
        withTasks
            """def start = 5

def f = { _ ->
  Async.start({ println("task runs") })
  start
}

println(f())"""
    Assert.Empty((Compiler.compile source).Diagnostics)

[<Fact>]
let ``A script whose def gives back what starts a task is told only of the escape`` () =
    let source =
        withTasks
            """def s = { _ -> seq.init(3, { i -> await(start! i * 10) }) }

println(s() \seq.toList)"""
    let code, _, at = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.ScopedContextEscapes.Code, code)
    Assert.Equal("start! i * 10", at)

[<Fact>]
let ``A smaller scope may await a task of the def's scope`` () =
    let source =
        withTasks
            """def outer = { _ ->
  def t = start! 20
  def inner = concurrent({ await(t) + await(start! 1) })
  inner + 1
}"""
    Assert.Empty((Compiler.compile source).Diagnostics)
