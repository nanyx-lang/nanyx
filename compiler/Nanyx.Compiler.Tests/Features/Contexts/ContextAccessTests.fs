module Nanyx.Compiler.Tests.Features.ContextAccess

open System.IO
open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// `protected` and `private` on a context are about access (#1449). Any module can name either in a context list, but
// only the home module can construct one in a `use`, and only it can call a private one's members.

/// A `module db =` block that declares `Tx` as `declaration` says, with a function that requires it.
let private dbBlock (declaration: string) =
    normalized $"""module db =
  {declaration}
  export def query: [Tx] string -> () = {{ sql -> commit() }}

import db

"""

let private privateTx = dbBlock "export context Tx = private (commit: () -> ())"
let private protectedTx = dbBlock "export context Tx = protected (commit: () -> ())"

/// The diagnostics compiling `source` gives: each one's code and the text it is reported at.
let private codesAt (source: string) =
    (Compiler.compile source).Diagnostics |> List.map (fun diagnostic -> diagnostic.Code, textAt source (spanOf diagnostic))

let private constructed = "def main = {\n  use Tx(commit = { () })\n  ()\n}\n"

[<Fact>]
let ``Any module can require a private context and call what requires it`` () =
    Assert.Empty(codesAt (privateTx + "def f: [Tx] () -> () = { query(\"select 1\") }\n"))

[<Fact>]
let ``Only a private context's home module can call its members`` () =
    let source = privateTx + "def f: [Tx] () -> () = {\n  query(\"select 1\")\n  commit()\n}\n"
    Assert.Equal<(string * string) list>([ DiagnosticCodes.PrivateContextMember.Code, "commit" ], codesAt source)
    let diagnostic = Assert.Single (Compiler.compile source).Diagnostics
    Assert.Equal(
        "'commit' is a member of the private context 'Tx', which only its own module can call: call a function that module exports instead",
        diagnostic.Message)

[<Fact>]
let ``A private context's member can't be read as a value outside its home module`` () =
    let source = privateTx + "def f: [Tx] () -> () = {\n  def c = commit\n  c()\n}\n"
    Assert.Equal<(string * string) list>([ DiagnosticCodes.PrivateContextMember.Code, "commit" ], codesAt source)

[<Fact>]
let ``Any module can call a protected context's members`` () =
    Assert.Empty(codesAt (protectedTx + "def f: [Tx] () -> () = { commit() }\n"))

[<Fact>]
let ``Only a protected or private context's home module can construct it in a use`` () =
    for block in [ protectedTx; privateTx ] do
        Assert.Equal<(string * string) list>([ DiagnosticCodes.NominalConstructedOutsideItsModule.Code, "Tx" ], codesAt (block + constructed))
    let diagnostic = (Compiler.compile (privateTx + constructed)).Diagnostics.Head
    Assert.Equal(
        "The private context 'Tx' can only be constructed in the module that defines it: call a function that module exports to supply it instead",
        diagnostic.Message)

[<Fact>]
let ``The rules are the same for a scoped context`` () =
    let scopedPrivate = dbBlock "export scoped context Tx = private (commit: () -> ())"
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.PrivateContextMember.Code, "commit" ],
        codesAt (scopedPrivate + "def f: [Tx] () -> () = { commit() }\n"))
    let scopedProtected = dbBlock "export scoped context Tx = protected (commit: () -> ())"
    Assert.Empty(codesAt (scopedProtected + "def f: [Tx] () -> () = { commit() }\n"))
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.NominalConstructedOutsideItsModule.Code, "Tx" ],
        codesAt (scopedProtected + constructed))

[<Fact>]
let ``Hovering a private context outside its home module leaves out its members`` () =
    withTempDir (fun dir ->
        File.WriteAllText(Path.Combine(dir, "db.nyx"), "module db\n\nexport context Tx = private (commit: () -> ())\n")
        let mainPath = Path.Combine(dir, "main.nyx")
        let source = "import db\n\ndef f: [Tx] () -> () = { () }\n"
        File.WriteAllText(mainPath, source)
        let result = Compiler.compileFile mainPath
        Assert.Empty(result.Diagnostics)
        let hover = Definition.typeDefinitionAt Unicode mainPath result (offsetOf source "Tx]" 0 1)
        Assert.Equal(Some "context Tx", hover |> Option.map _.Type))

[<Fact>]
let ``Completion outside its home module doesn't offer a private context's member`` () =
    let source = privateTx + "def f: [Tx] () -> () = {\n  query(\"x\")\n}\n"
    let result = Compiler.compile source
    Assert.Empty(result.Diagnostics)
    let names = Completion.namesInScope result (offsetOf source "query(\"x\")" 0 0) |> List.map _.Name
    Assert.DoesNotContain("commit", names)
