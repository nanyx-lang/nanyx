/// The program's entry point runs through its host's `run`, and `HostRuntime` is provided only to hosts (#1245, see
/// docs/src/content/work-in-progress/hosts.md).
module Nanyx.Compiler.Tests.Features.Hosts.ConsoleHost

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

[<Fact>]
let ``An entry point that takes an argument doesn't fit the console host, reported at @entry`` () =
    let source =
        """module main

@entry
def main = { name: string -> println("Hello, {name}!") }
"""
    let code, message, text = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.EntryDoesNotFitHost.Code, code)
    Assert.Equal(
        """The entry point 'main' doesn't fit the host 'nanyx/console.host'
  expected: [Console] () -> a
  found:    string -> ()""".ReplaceLineEndings "\n",
        message)
    Assert.Equal("@entry", text)

[<Fact>]
let ``An entry point that is not a function doesn't fit the console host`` () =
    let code, message, _ = onlyDiagnostic "@entry\ndef main = 42\n"
    Assert.Equal(DiagnosticCodes.EntryDoesNotFitHost.Code, code)
    Assert.EndsWith("found:    int", message)

[<Fact>]
let ``An entry point that returns a value fits the console host, which ignores it`` () =
    compileClean "@entry\ndef main = { 1 + 1 }\n" |> ignore

[<Fact>]
let ``A def that requires HostRuntime outside a host is an error, reported at its name`` () =
    let source =
        """import nanyx/host.runtime

def shout = { text -> writeStdout(text ++ "!") }
"""
    let code, message, text = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.HostRuntimeOutsideHost.Code, code)
    Assert.StartsWith("'shout' requires the context 'HostRuntime', which only a host may require", message)
    Assert.Equal("shout", text)

[<Fact>]
let ``A def that writes HostRuntime in its type outside a host is an error`` () =
    let source =
        """import nanyx/host.runtime

def shout: [HostRuntime] string -> () = { text -> writeStdout(text) }
"""
    Assert.Equal((DiagnosticCodes.HostRuntimeOutsideHost.Code, "shout"), singleError source)

[<Fact>]
let ``A call at the top level that needs HostRuntime outside a host is an error, reported at the call`` () =
    let source =
        """import nanyx/host.runtime

writeStdout("hi")
"""
    Assert.Equal((DiagnosticCodes.HostRuntimeOutsideHost.Code, "writeStdout(\"hi\")"), singleError source)

[<Fact>]
let ``Calling a host's run outside a host is an error, as it requires HostRuntime`` () =
    let source =
        """import nanyx/console.host as h

h.run({ () })
"""
    let code, message, _ = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.HostRuntimeOutsideHost.Code, code)
    Assert.StartsWith("'h.run' requires the context 'HostRuntime'", message)

[<Fact>]
let ``The console host requires HostRuntime, and checks`` () =
    let source = (StandardLibrary.tryLoadSource "console.host").Value
    let result = Compiler.compileFrom Linters.everyLinter (StandardLibraryModule "console.host") source
    Assert.True(result.TypeChecked, $"%A{result.Diagnostics}")
    Assert.Empty(result.Diagnostics |> List.filter Diagnostics.isError)
