module Nanyx.Compiler.Tests.Features.UndeclaredContexts

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

/// The diagnostics compiling `source` gives, which must all be undeclared contexts: the text each is reported at, and
/// its message.
let private undeclaredContexts (source: string) =
    let result = Compiler.compile source
    for diagnostic in result.Diagnostics do
        Assert.Equal(DiagnosticCodes.UndeclaredContext.Code, diagnostic.Code)
    result.Diagnostics |> List.map (fun diagnostic -> textAt source (spanOf diagnostic), diagnostic.Message)

let private entryCalling (call: string) = $"\n@entry\ndef main = {{\n  {call}\n}}\n"

[<Fact>]
let ``A def whose type declares no context cannot call a member of the runtime's Console`` () =
    let source = "def f: int -> int = { n ->\n  write(\"x\")\n  n\n}\n" + entryCalling "println(f(1))"
    match undeclaredContexts source with
    | [ text, message ] ->
        Assert.Equal("write", text)
        Assert.Contains("'write' is a member of the context 'Console', which 'f' does not declare", message)
    | other -> failwith $"Expected one diagnostic, got %A{other}"

[<Fact>]
let ``A def whose type declares no context cannot call a function that requires one`` () =
    let source =
        "def sayHello: [Console] () -> () = { println(\"hello\") }\n" +
        "def g: int -> int = { n ->\n  sayHello()\n  n\n}\n" + entryCalling "println(\"{g(1)}\")"
    match undeclaredContexts source with
    | [ text, message ] ->
        Assert.Equal("sayHello", text)
        Assert.Contains("'sayHello' requires the context 'Console', which 'g' does not declare", message)
    | other -> failwith $"Expected one diagnostic, got %A{other}"

[<Fact>]
let ``A lambda runs in the contexts of the def it is written in`` () =
    let source =
        "def twice: (() -> ()) -> () = { k ->\n  k()\n  k()\n}\n" +
        "def run: () -> () = { twice({ println(\"x\") }) }\n" + entryCalling "run()"
    match undeclaredContexts source with
    | [ text, _ ] -> Assert.Equal("println", text)
    | other -> failwith $"Expected one diagnostic, got %A{other}"

[<Fact>]
let ``A local function whose type declares no context cannot call a member of the runtime's Console`` () =
    let source = "\n@entry\ndef main = {\n  def shout: string -> () = { s -> println(s) }\n  shout(\"x\")\n}\n"
    match undeclaredContexts source with
    | [ text, message ] ->
        Assert.Equal("println", text)
        Assert.Contains("which 'shout' does not declare", message)
    | other -> failwith $"Expected one diagnostic, got %A{other}"

[<Fact>]
let ``An unannotated def infers the contexts it uses, so a def that declares none cannot call it`` () =
    // `g` comes first: what `f` needs is only known once every def has been inferred.
    let source =
        "def g: int -> int = { n -> f(n) }\n" +
        "def f = { n ->\n  h()\n  n + 1\n}\n" +
        "def h = { println(\"x\") }\n" + entryCalling "println(\"{g(1)}\")"
    match undeclaredContexts source with
    | [ text, message ] ->
        Assert.Equal("f", text)
        Assert.Contains("'f' requires the context 'Console', which 'g' does not declare", message)
    | other -> failwith $"Expected one diagnostic, got %A{other}"

[<Fact>]
let ``Code that declares or is given the Console context can use it`` () =
    let source =
        "def sayHello: [Console] () -> () = { println(\"hello\") }\n" +
        "def greet: [Console] string -> () = { name ->\n  sayHello()\n  println(name)\n}\n" +
        "def helper = { n ->\n  greet(\"x\")\n  n\n}\n" +
        "def g: [Console] int -> int = { n -> helper(n) }\n" +
        "def quiet: () -> () = {\n" +
        "  use Console(\n" +
        "    write = { _ -> () }\n    writeError = { _ -> () }\n" +
        "    readLine = { #error(#endOfInput) }\n    readToEnd = { #error(#endOfInput) }\n    flush = { () }\n" +
        "  ) in sayHello()\n" +
        "}\n" +
        "\n@entry\ndef main = {\n  quiet()\n  println(\"{g(1)}\")\n  println(\"{helper(2)}\")\n}\n"
    Assert.Empty(undeclaredContexts source)

[<Fact>]
let ``The top level of a module runs in the runtime's Console`` () =
    Assert.Empty(undeclaredContexts "import nanyx/console\n\nprintln(\"hi\")\ndef x = println(\"value\")\n")

[<Fact>]
let ``A function imported from another module requires its contexts where it is called`` () =
    withTempDir (fun dir ->
        let mainPath = Path.Combine(dir, "main.nyx")
        File.WriteAllText(
            Path.Combine(dir, "mod.nyx"),
            "module mod\nimport nanyx/console\nexport def greet: [Console] () -> () = { println(\"hi\") }\nexport def loud = { println(\"LOUD\") }")
        File.WriteAllText(mainPath, "module main\nimport mod\ndef g: () -> () = { loud() }\n@entry\ndef main = {\n  greet()\n  g()\n}")
        let diagnostic = single (Compiler.compileFile mainPath)
        Assert.Equal(DiagnosticCodes.UndeclaredContext.Code, diagnostic.Code)
        Assert.Contains("'loud' requires the context 'Console', which 'g' does not declare", diagnostic.Message))

/// The undeclared-context diagnostics compiling `main` beside a module `mod` that exports a `[Console]` function `greet`.
let private undeclaredContextsWithMod (main: string) =
    let mutable result = []
    withTempDir (fun dir ->
        let mainPath = Path.Combine(dir, "main.nyx")
        File.WriteAllText(
            Path.Combine(dir, "mod.nyx"),
            "module mod\nimport nanyx/console\nexport def greet: [Console] () -> () = { println(\"hi\") }\n")
        File.WriteAllText(mainPath, main)
        result <-
            (Compiler.compileFile mainPath).Diagnostics
            |> List.filter (fun diagnostic -> diagnostic.Code = DiagnosticCodes.UndeclaredContext.Code)
            |> List.map (fun diagnostic -> textAt main (spanOf diagnostic)))
    result

[<Fact>]
let ``An aliased import's function still requires its contexts where it is called`` () =
    let main = "module main\nimport mod as m\ndef g: () -> () = { m.greet() }\n@entry\ndef main = { g() }\n"
    Assert.Equal<string list>([ "m.greet" ], undeclaredContextsWithMod main)

[<Fact>]
let ``An imported function requires its contexts in a module that does not have them in scope`` () =
    let main = "module main\nimport mod\ndef g: () -> () = { greet() }\n"
    Assert.Equal<string list>([ "greet" ], undeclaredContextsWithMod main)

[<Fact>]
let ``A standard-library function imported under an alias still requires its context`` () =
    let main = "module main\nimport nanyx/console as c\ndef g: () -> () = { c.write(\"hi\") }\n@entry\ndef main = { g() }\n"
    Assert.Equal<string list>([ "c.write" ], undeclaredContextsWithMod main)
[<Fact>]
let ``An imported function's context needs nothing more where the module cannot name it, at the top level or in a def that infers it`` () =
    let main = "module main\nimport mod\ngreet()\ndef h = { greet() }\ndef k: () -> () = { h() }\n"
    Assert.Equal<string list>([ "h" ], undeclaredContextsWithMod main)

[<Fact>]
let ``A def that declares an aliased context can use its members under the alias`` () =
    let main = "module main\nimport nanyx/console as c\ndef g: [c.Console] () -> () = { c.println(\"hi\") }\n@entry\ndef main = { g() }\n"
    Assert.Equal<string list>([], undeclaredContextsWithMod main)
