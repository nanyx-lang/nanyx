module Nanyx.Compiler.DiagnosticsTests

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program

let private single (result: CompileResult) =
    Assert.Single(result.Diagnostics) |> ignore
    result.Diagnostics.Head

/// The text `span` covers in `source` (whose line endings are already `\n`).
let private textAt (source: string) (span: SourceSpan) = source.Substring(span.Start, span.End - span.Start)

let private spanOf (diagnostic: Diagnostic) =
    Assert.True(diagnostic.Span.IsSome, $"Expected a span on %A{diagnostic}")
    diagnostic.Span.Value

[<Fact>]
let ``Every compiler code is distinct and has the NYX form`` () =
    let codes = DiagnosticCodes.all |> List.map (fun code -> code.Code)
    Assert.Equal(codes.Length, (List.distinct codes).Length)
    for code in codes do
        Assert.Matches("^NYX\\d{4}$", code)

[<Fact>]
let ``The compiler reports only critical and error severities, but for a deprecated type name`` () =
    for code in DiagnosticCodes.all |> List.filter ((<>) DiagnosticCodes.DeprecatedTypeName) do
        Assert.True(code.Severity = Severity.Critical || code.Severity = Severity.Error, $"{code.Code} is {code.Severity}")

[<Fact>]
let ``An unknown identifier is reported at the reference, not an earlier mention of the name`` () =
    // The old positioning searched the source for the first quoted name, which is in the string.
    let source = "def note = \"missing\"\ndef value = missing"
    let diagnostic = single (Compiler.compile source)
    Assert.Equal(DiagnosticCodes.UnknownIdentifier.Code, diagnostic.Code)
    Assert.Equal(DiagnosticCodes.UnknownIdentifier.Title, diagnostic.Title)
    let span = spanOf diagnostic
    Assert.Equal((1, 12), (span.StartLine, span.StartColumn))
    Assert.Equal("missing", textAt source span)

[<Fact>]
let ``A type mismatch in an argument is reported at the argument`` () =
    let source = "import nanyx/console\n\nprintln(42)"
    let diagnostic = single (Compiler.compile source)
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, diagnostic.Code)
    Assert.Equal("42", textAt source (spanOf diagnostic))

[<Fact>]
let ``A type mismatch between operands is reported at the operation`` () =
    let source = "def a = 1\ndef b = a + \"x\""
    let diagnostic = single (Compiler.compile source)
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, diagnostic.Code)
    Assert.Equal("a + \"x\"", textAt source (spanOf diagnostic))

[<Fact>]
let ``An unknown member is reported at the member name`` () =
    let source = "def point = (x = 1, y = 2)\ndef z = point.z"
    let diagnostic = single (Compiler.compile source)
    Assert.Equal(DiagnosticCodes.UnknownMember.Code, diagnostic.Code)
    Assert.Equal("z", textAt source (spanOf diagnostic))
    Assert.Equal(1, (spanOf diagnostic).StartLine)

[<Fact>]
let ``A non-exhaustive match is reported at its head`` () =
    let source =
        "def value: #some(int) | #none = #none\n" +
        "def result = match value\n" +
        "| #some(v) -> v\n"
    let diagnostic = single (Compiler.compile source)
    Assert.Equal(DiagnosticCodes.NonExhaustiveMatch.Code, diagnostic.Code)
    let span = spanOf diagnostic
    Assert.Equal((1, 13), (span.StartLine, span.StartColumn))
    Assert.Equal("match value", textAt source span)

[<Fact>]
let ``A pattern of the wrong type is reported at the pattern`` () =
    let source =
        "def value = 1\n" +
        "def result = match value\n" +
        "| 0 -> 0\n" +
        "| \"one\" -> 1\n" +
        "| _ -> 2\n"
    let diagnostic = single (Compiler.compile source)
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, diagnostic.Code)
    Assert.Equal("\"one\"", textAt source (spanOf diagnostic))

[<Fact>]
let ``A mismatch inside a tuple pattern is reported within the pattern, not at the whole match`` () =
    let source =
        "def value = (1, 2)\n" +
        "def result = match value\n" +
        "| (a, \"two\") -> a\n" +
        "| _ -> 0\n"
    let diagnostic = single (Compiler.compile source)
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, diagnostic.Code)
    // The element's constraint is solved first, so the tuple's own constraint is the one that fails.
    Assert.Equal("(a, \"two\")", textAt source (spanOf diagnostic))

[<Fact>]
let ``A reference to a private definition links to where it is declared`` () =
    let source = "module inner =\n  def secret = 1\n  export def open = secret + 1\ndef value = inner.secret"
    let diagnostic = single (Compiler.compile source)
    Assert.Equal(DiagnosticCodes.NotExported.Code, diagnostic.Code)
    Assert.Equal("inner.secret", textAt source (spanOf diagnostic))
    let relatedSpan, relatedMessage = Assert.Single(diagnostic.Related)
    Assert.Equal((1, 6), (relatedSpan.StartLine, relatedSpan.StartColumn))
    Assert.Contains("declared here", relatedMessage)

[<Fact>]
let ``An annotation more general than its body is reported at the definition's name`` () =
    let source = "def id: a -> a = { x -> x + 1 }"
    let diagnostic = single (Compiler.compile source)
    Assert.Equal(DiagnosticCodes.AnnotationTooGeneral.Code, diagnostic.Code)
    Assert.Equal("id", textAt source (spanOf diagnostic))

[<Fact>]
let ``A parse error has a code and the position where parsing failed`` () =
    let result = Compiler.compile "def x = 1\ndef = 2"
    let diagnostic = single result
    Assert.Equal(DiagnosticCodes.SyntaxError.Code, diagnostic.Code)
    let span = spanOf diagnostic
    Assert.Equal(1, span.StartLine)
    // The span already says where; the message does not repeat it.
    Assert.DoesNotContain("Ln:", diagnostic.Message)

[<Fact>]
let ``A parse error's message leaves out the file however long its path is`` () =
    // FParsec wraps the line naming the file and position when the path is long.
    let directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nanyx-diagnostics-tests", String.replicate 6 "a-rather-long-directory-name-", System.Guid.NewGuid().ToString("N"))
    System.IO.Directory.CreateDirectory directory |> ignore
    let file = System.IO.Path.Combine(directory, "broken.nyx")
    System.IO.File.WriteAllText(file, "def = 2")
    let diagnostic = single (Compiler.compileFile file)
    Assert.Equal(DiagnosticCodes.SyntaxError.Code, diagnostic.Code)
    Assert.DoesNotContain("a-rather-long", diagnostic.Message)
    Assert.DoesNotContain("Ln:", diagnostic.Message)
    Assert.StartsWith("def = 2", diagnostic.Message)

[<Fact>]
let ``An unknown standard library module is reported at the import`` () =
    let source = "import nanyx/sockets\n\ndbg(1)"
    let diagnostic = single (Compiler.compile source)
    Assert.Equal(DiagnosticCodes.UnknownStandardLibraryModule.Code, diagnostic.Code)
    Assert.Equal("nanyx/sockets", textAt source (spanOf diagnostic))

[<Fact>]
let ``A missing file is a critical diagnostic about the whole compilation`` () =
    let diagnostic = single (Compiler.compileFile (Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".nyx")))
    Assert.Equal(DiagnosticCodes.FileNotFound.Code, diagnostic.Code)
    Assert.Equal(Severity.Critical, diagnostic.Severity)
    Assert.True(diagnostic.Span.IsNone)

let private withTempDir (action: string -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    try
        action dir
    finally
        if Directory.Exists dir then
            Directory.Delete(dir, true)

[<Fact>]
let ``A diagnostic in an imported module is reported in that module's file`` () =
    withTempDir (fun dir ->
        let modulePath = Path.Combine(dir, "mod.nyx")
        let mainPath = Path.Combine(dir, "main.nyx")
        File.WriteAllText(modulePath, "module mod\nexport def value = missing")
        // The root mentions `missing` too, where the old positioning would have put the error.
        File.WriteAllText(mainPath, "module main\nimport \"mod.nyx\"\ndef missing2 = \"missing\"")
        let diagnostic = single (Compiler.compileFile mainPath)
        Assert.Equal(DiagnosticCodes.UnknownIdentifier.Code, diagnostic.Code)
        let span = spanOf diagnostic
        Assert.Equal(Path.GetFullPath modulePath, span.File)
        Assert.Equal((1, 19), (span.StartLine, span.StartColumn)))

[<Fact>]
let ``An import cycle is reported at the import that closes it`` () =
    withTempDir (fun dir ->
        let mainPath = Path.Combine(dir, "main.nyx")
        File.WriteAllText(Path.Combine(dir, "a.nyx"), "module a\nimport \"main.nyx\"\nexport def x = 1")
        File.WriteAllText(mainPath, "module main\nimport \"a.nyx\"\ndef y = x")
        let diagnostic = single (Compiler.compileFile mainPath)
        Assert.Equal(DiagnosticCodes.ImportCycle.Code, diagnostic.Code)
        let span = spanOf diagnostic
        Assert.Equal(Path.Combine(dir, "a.nyx") |> Path.GetFullPath, span.File)
        Assert.Equal(1, span.StartLine))

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
    let source = "def f: int -> int = { n ->\n  println(\"x\")\n  n\n}\n" + entryCalling "println(\"{f(1)}\")"
    match undeclaredContexts source with
    | [ text, message ] ->
        Assert.Equal("println", text)
        Assert.Contains("'println' is a member of the context 'Console', which 'f' does not declare", message)
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
        "    print = { _ -> () }\n    println = { _ -> () }\n    eprint = { _ -> () }\n    eprintln = { _ -> () }\n" +
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
        File.WriteAllText(mainPath, "module main\nimport \"mod.nyx\"\ndef g: () -> () = { loud() }\n@entry\ndef main = {\n  greet()\n  g()\n}")
        let diagnostic = single (Compiler.compileFile mainPath)
        Assert.Equal(DiagnosticCodes.UndeclaredContext.Code, diagnostic.Code)
        Assert.Contains("'loud' requires the context 'Console', which 'g' does not declare", diagnostic.Message))

/// A module `people` that exports the nominal `$Person`, with a function that makes one and one that takes one.
let private peopleModule =
    "module people\n" +
    "export type $Person = (name: string)\n" +
    "export def $Person.new: string -> $Person = { name -> $Person(name = name) }\n" +
    "export def greet: $Person -> string = { p -> \"Hello, \" ++ p.name }\n"

/// The diagnostics compiling `main`, beside the `people` module, gives: each one's code and the text it is reported at.
let private compiledWithPeople (main: string) =
    let mutable result = []
    withTempDir (fun dir ->
        let mainPath = Path.Combine(dir, "main.nyx")
        File.WriteAllText(Path.Combine(dir, "people.nyx"), peopleModule)
        File.WriteAllText(mainPath, main)
        result <-
            (Compiler.compileFile mainPath).Diagnostics
            |> List.map (fun diagnostic -> diagnostic.Code, textAt main (spanOf diagnostic)))
    result

[<Fact>]
let ``An aliased import's nominal type cannot be named without the alias`` () =
    let main = "module main\nimport \"people.nyx\" as people\ndef p: $Person = people.$Person.new(\"Ada\")\n"
    Assert.Equal<(string * string) list>([ DiagnosticCodes.UnknownType.Code, "$Person" ], compiledWithPeople main)

/// The diagnostics compiling `source` gives: each one's code and the text it is reported at.
let private compiled (source: string) =
    (Compiler.compile source).Diagnostics |> List.map (fun diagnostic -> diagnostic.Code, textAt source (spanOf diagnostic))

[<Fact>]
let ``A type name in a def's annotation that names no type is reported`` () =
    Assert.Equal<(string * string) list>([ DiagnosticCodes.UnknownType.Code, "Strnig" ], compiled "def x: Strnig = \"a\"\n")
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.UnknownType.Code, "Strnig"; DiagnosticCodes.UnknownType.Code, "Nmuber" ],
        compiled "def f: (Strnig, list(Nmuber)) -> Strnig = { s, _ -> s }\n")

[<Fact>]
let ``A type name in a lambda parameter's annotation that names no type is reported`` () =
    Assert.Equal<(string * string) list>([ DiagnosticCodes.UnknownType.Code, "Foo" ], compiled "def f = { (x: Foo) -> x }\n")

[<Fact>]
let ``A type name in a type definition that names no type is reported`` () =
    Assert.Equal<(string * string) list>([ DiagnosticCodes.UnknownType.Code, "Foo" ], compiled "type T = (a: Foo)\n")
    Assert.Equal<(string * string) list>([ DiagnosticCodes.UnknownType.Code, "Missing" ], compiled "def f = {\n  type T = #some(Missing)\n  1\n}\n")

[<Fact>]
let ``Built-in types, type variables, type parameters and types defined anywhere in the module are known`` () =
    let source =
        "def n: int = 1\n" +
        "def xs: list(string) = [ \"a\" ]\n" +
        "def ys: seq(bool) -> seq(bool) = { s -> s }\n" +
        "def id: a -> a = { x -> x }\n" +
        "def t: Tree = #leaf\n" +
        "type Tree = #leaf | #node(Tree, Tree)\n" +
        "type Box(item) = (value: item)\n" +
        "type Pair(a, b) = (first: a, second: b)\n" +
        "def g = { (p: Pair(int, Tree)), (b: Box(string)) -> (p.first, b.value) }\n"
    Assert.Empty(compiled source)

[<Fact>]
let ``An aliased import's constructor cannot be called without the alias`` () =
    let main = "module main\nimport \"people.nyx\" as people\ndef p = $Person(name = \"Ada\")\n"
    Assert.Equal<(string * string) list>([ DiagnosticCodes.UnknownFunction.Code, "$Person" ], compiledWithPeople main)

[<Fact>]
let ``An aliased import's types and their attached definitions are named with the alias`` () =
    let main =
        "module main\nimport \"people.nyx\" as people\n" +
        "def p: people.$Person = people.$Person.new(\"Ada\")\n" +
        "def greeting: string = people.greet(p)\n" +
        "def all: list(people.$Person) = [ p ]\n"
    Assert.Empty(compiledWithPeople main)

[<Fact>]
let ``An unaliased import brings its types into scope unqualified`` () =
    let main = "module main\nimport \"people.nyx\"\ndef p: $Person = $Person.new(\"Ada\")\ndef greeting: string = greet(p)\n"
    Assert.Empty(compiledWithPeople main)

[<Fact>]
let ``A nominal type cannot be constructed outside the module that defines it`` () =
    let unaliased = "module main\nimport \"people.nyx\"\ndef p = $Person(name = \"Ada\")\n"
    Assert.Equal<(string * string) list>([ DiagnosticCodes.NominalConstructedOutsideItsModule.Code, "$Person" ], compiledWithPeople unaliased)
    let aliased = "module main\nimport \"people.nyx\" as people\ndef p = people.$Person(name = \"Ada\")\n"
    Assert.Equal<(string * string) list>([ DiagnosticCodes.NominalConstructedOutsideItsModule.Code, "people.$Person" ], compiledWithPeople aliased)

[<Fact>]
let ``A nominal type can be constructed in the module that defines it`` () =
    let result = Compiler.compile "type $Person = (name: string)\ndef p: $Person = $Person(name = \"Ada\")\ndef name: string = p.name\n"
    Assert.Empty(result.Diagnostics)

[<Fact>]
let ``A nominal value is assignable to its shape, but the shape is not assignable to the nominal type`` () =
    let toShape = "module main\nimport \"people.nyx\"\ndef nameOf: (name: string) -> string = { r -> r.name }\ndef n: string = nameOf($Person.new(\"Ada\"))\n"
    Assert.Empty(compiledWithPeople toShape)
    let fromShape = "module main\nimport \"people.nyx\"\ndef g: string = greet((name = \"Ada\"))\n"
    match compiledWithPeople fromShape with
    | [ code, _ ] -> Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    | other -> failwith $"Expected one type mismatch, got %A{other}"

[<Fact>]
let ``An aliased import's contexts are named with the alias`` () =
    withTempDir (fun dir ->
        let mainPath = Path.Combine(dir, "main.nyx")
        File.WriteAllText(Path.Combine(dir, "logs.nyx"), "module logs\nexport context Logger = (log: string -> ())\n")
        File.WriteAllText(mainPath, "module main\nimport \"logs.nyx\" as logs\ndef f: [logs.Logger] () -> () = { log(\"x\") }\ndef g: [Logger] () -> () = { log(\"x\") }\n")
        let diagnostic = single (Compiler.compileFile mainPath)
        Assert.Equal(DiagnosticCodes.UnknownContext.Code, diagnostic.Code)
        Assert.Contains("Unknown context 'Logger' in the signature of 'g'", diagnostic.Message))

[<Fact>]
let ``An aliased import's functions cannot be called without the alias`` () =
    let main = "module main\nimport \"people.nyx\" as people\ndef g: string = greet(people.$Person.new(\"Ada\"))\n"
    Assert.Equal<(string * string) list>([ DiagnosticCodes.UnknownFunction.Code, "greet" ], compiledWithPeople main)

[<Fact>]
let ``An aliased import's attached definitions cannot be called without the alias`` () =
    let main = "module main\nimport \"people.nyx\" as people\ndef p: people.$Person = $Person.new(\"Ada\")\n"
    match compiledWithPeople main with
    | [ _, text ] -> Assert.Equal("$Person.new", text)
    | other -> failwith $"Expected one diagnostic at '$Person.new', got %A{other}"

[<Fact>]
let ``A shape is not assignable to a nominal type through an annotation`` () =
    let main = "module main\nimport \"people.nyx\" as people\ndef p: people.$Person = (name = \"Ada\")\n"
    match compiledWithPeople main with
    | [ code, _ ] -> Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    | other -> failwith $"Expected one type mismatch, got %A{other}"

[<Fact>]
let ``A function that returns a nominal type cannot return its shape`` () =
    let main = "module main\nimport \"people.nyx\"\ndef make: string -> $Person = { name -> (name = name) }\n"
    match compiledWithPeople main with
    | [ code, _ ] -> Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    | other -> failwith $"Expected one type mismatch, got %A{other}"

[<Fact>]
let ``A list of a nominal type cannot hold its shape`` () =
    let main = "module main\nimport \"people.nyx\"\ndef all: list($Person) = [ $Person.new(\"Ada\"), (name = \"Grace\") ]\n"
    match compiledWithPeople main with
    | [ code, _ ] -> Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    | other -> failwith $"Expected one type mismatch, got %A{other}"

[<Fact>]
let ``A list of a nominal type is assignable to a list of its shape`` () =
    let main =
        "module main\nimport \"people.nyx\"\n" +
        "def namesOf: list((name: string)) -> int = { xs -> list.length(xs) }\n" +
        "def people: list($Person) = [ $Person.new(\"Ada\") ]\n" +
        "def n: int = namesOf(people)\n"
    Assert.Empty(compiledWithPeople main)

[<Fact>]
let ``A nominal type cannot be constructed outside its module from inside a lambda`` () =
    let main = "module main\nimport \"people.nyx\"\ndef all = [ \"Ada\", \"Grace\" ] \\list.map { name -> $Person(name = name) }\n"
    Assert.Equal<(string * string) list>([ DiagnosticCodes.NominalConstructedOutsideItsModule.Code, "$Person" ], compiledWithPeople main)

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
    let main = "module main\nimport \"mod.nyx\" as m\ndef g: () -> () = { m.greet() }\n@entry\ndef main = { g() }\n"
    Assert.Equal<string list>([ "m.greet" ], undeclaredContextsWithMod main)

[<Fact>]
let ``An imported function requires its contexts in a module that does not have them in scope`` () =
    let main = "module main\nimport \"mod.nyx\"\ndef g: () -> () = { greet() }\n"
    Assert.Equal<string list>([ "greet" ], undeclaredContextsWithMod main)

[<Fact>]
let ``A standard-library function imported under an alias still requires its context`` () =
    let main = "module main\nimport nanyx/console as c\ndef g: () -> () = { c.println(\"hi\") }\n@entry\ndef main = { g() }\n"
    Assert.Equal<string list>([ "c.println" ], undeclaredContextsWithMod main)
[<Fact>]
let ``An imported function's context needs nothing more where the module cannot name it, at the top level or in a def that infers it`` () =
    let main = "module main\nimport \"mod.nyx\"\ngreet()\ndef h = { greet() }\ndef k: () -> () = { h() }\n"
    Assert.Equal<string list>([ "h" ], undeclaredContextsWithMod main)

[<Fact>]
let ``A def that declares an aliased context can use its members under the alias`` () =
    let main = "module main\nimport nanyx/console as c\ndef g: [c.Console] () -> () = { c.println(\"hi\") }\n@entry\ndef main = { g() }\n"
    Assert.Equal<string list>([], undeclaredContextsWithMod main)
/// A `module money =` block with a nominal `$Money`, its constructor and printer, an exported value and a private one.
let private moneyBlock =
    "module money =\n" +
    "  export type $Money = (pence: int)\n" +
    "  export def $Money.new: int -> $Money = { pence -> $Money(pence = pence) }\n" +
    "  export def $Money.toString: $Money -> string = { m -> \"{m.pence}p\" }\n" +
    "  export def zero: $Money = $Money.new(0)\n" +
    "  def secret = 42\n\n"

/// The diagnostics compiling `source` gives: each one's code and the text it is reported at.
let private codesAt (source: string) =
    (Compiler.compile source).Diagnostics |> List.map (fun diagnostic -> diagnostic.Code, textAt source (spanOf diagnostic))

[<Fact>]
let ``A module block can be imported by its file, unqualified or under an alias, before or after it`` () =
    let unqualified = moneyBlock + "import money\n\ndef price: $Money = $Money.new(250)\ndef text: string = $Money.toString(price)\ndef nothing: string = zero \\toString\n"
    Assert.Empty(codesAt unqualified)
    let aliased = moneyBlock + "import money as m\n\ndef price: m.$Money = m.$Money.new(250)\ndef text: string = m.$Money.toString(m.zero)\n"
    Assert.Empty(codesAt aliased)
    let first = "import money\n\n" + moneyBlock + "def text: string = $Money.toString(zero)\n"
    Assert.Empty(codesAt first)

[<Fact>]
let ``A module block's import brings in only its exports, and a local still shadows them`` () =
    Assert.Equal<(string * string) list>([ DiagnosticCodes.UnknownIdentifier.Code, "secret" ], codesAt (moneyBlock + "import money\n\ndef s = secret\n"))
    Assert.Empty(codesAt (moneyBlock + "import money\n\ndef f: int -> int = { zero -> zero + 1 }\n"))

[<Fact>]
let ``A nominal type in a module block can only be constructed in the block`` () =
    let viaImport = moneyBlock + "import money\n\ndef p = $Money(pence = 1)\n"
    Assert.Equal<(string * string) list>([ DiagnosticCodes.NominalConstructedOutsideItsModule.Code, "$Money" ], codesAt viaImport)
    let qualified = moneyBlock + "def p = money.$Money(pence = 1)\n"
    Assert.Equal<(string * string) list>([ DiagnosticCodes.NominalConstructedOutsideItsModule.Code, "money.$Money" ], codesAt qualified)
    match codesAt (moneyBlock + "import money\n\ndef p: $Money = (pence = 1)\n") with
    | [ code, _ ] -> Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    | other -> failwith $"Expected one type mismatch, got %A{other}"

/// A `module ids =` block with a protected `$UserId`, whose shape only the block can see.
let private idsBlock =
    "module ids =\n" +
    "  export type $UserId = private int\n" +
    "  export def $UserId.of: int -> $UserId = { n -> $UserId(n) }\n" +
    "  export def $UserId.value: $UserId -> int = { id -> match id | $UserId(n) -> n }\n" +
    "  export def first: $UserId = $UserId.of(1)\n\n"

[<Fact>]
let ``A protected type can only be taken apart in the module that defines it`` () =
    Assert.Empty(codesAt (idsBlock + "import ids\n\ndef n: int = $UserId.value(first)\n"))
    Assert.Equal<(string * string) list>([ DiagnosticCodes.NominalConstructedOutsideItsModule.Code, "$UserId(n)" ], codesAt (idsBlock + "import ids\n\ndef $UserId(n) = first\n"))
    Assert.Equal<(string * string) list>([ DiagnosticCodes.NominalConstructedOutsideItsModule.Code, "$UserId(n)" ], codesAt (idsBlock + "import ids\n\ndef n = match first | $UserId(n) -> n\n"))
    // A nominal type that is not protected has no hidden shape, so its pattern is usable anywhere.
    Assert.Empty(codesAt (moneyBlock + "import money\n\ndef $Money(m) = zero\ndef p: int = m.pence\n"))

[<Fact>]
let ``A module block cannot use its file's definitions`` () =
    let source = "def outerValue = 1\n\n" + moneyBlock.Replace("def secret = 42", "def secret = outerValue")
    Assert.Equal<(string * string) list>([ DiagnosticCodes.OutsideModuleBlock.Code, "outerValue" ], codesAt source)

[<Fact>]
let ``A module block's imports are its own`` () =
    let fileImport = "import nanyx/console\n\n" + moneyBlock.Replace("def secret = 42", "def secret = { println(\"x\") }")
    Assert.Equal<(string * string) list>([ DiagnosticCodes.OutsideModuleBlock.Code, "println(\"x\")" ], codesAt fileImport)
    let blockImport = moneyBlock.Replace("module money =\n", "module money =\n  import nanyx/console\n").Replace("def secret = 42", "def secret = { println(\"x\") }")
    Assert.Empty(codesAt blockImport)
    Assert.Equal<(string * string) list>([ DiagnosticCodes.OutsideModuleBlock.Code, "println(\"y\")" ], codesAt (blockImport + "println(\"y\")\n"))

[<Fact>]
let ``A module block can import the block beside it, but not reach it without an import`` () =
    let other = "module prices =\n  import money\n  export def price: $Money = $Money.new(5)\n"
    Assert.Empty(codesAt (moneyBlock + other))
    let unimported = "module prices =\n  export def nothing = money.zero\n"
    Assert.Equal<(string * string) list>([ DiagnosticCodes.OutsideModuleBlock.Code, "money.zero" ], codesAt (moneyBlock + unimported))

[<Fact>]
let ``A module block has no top-level code`` () =
    let source = moneyBlock.Replace("def secret = 42", "def secret = 42\n  secret + 1")
    Assert.Equal<(string * string) list>([ DiagnosticCodes.CodeInModuleBlock.Code, "secret + 1" ], codesAt source)
[<Fact>]
let ``A module block cannot name or make its file's types`` () =
    let point = "type Point = (x: int, y: int)\n\n"
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.OutsideModuleBlock.Code, "Point" ],
        codesAt (point + "module shapes =\n  export def origin: Point = (x = 0, y = 0)\n"))
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.OutsideModuleBlock.Code, "Point" ],
        codesAt (point + "module shapes =\n  export def all: list(Point) = []\n"))
    match codesAt (point + "module shapes =\n  export def origin = Point(x = 0, y = 0)\n") with
    | [ code, text ] ->
        Assert.Equal(DiagnosticCodes.OutsideModuleBlock.Code, code)
        Assert.StartsWith("Point", text)
    | other -> failwith $"Expected one diagnostic, got %A{other}"
    Assert.Empty(codesAt (point + "def p: Point = (x = 1, y = 2)\n"))

[<Fact>]
let ``A module block names the types of the blocks it imports, and its own`` () =
    let shapes = "module shapes =\n  export type Point = (x: int, y: int)\n  export def origin: Point = Point(x = 0, y = 0)\n\n"
    Assert.Empty(codesAt (shapes + "module draw =\n  import shapes\n  export def dot: Point -> int = { p -> p.x }\n"))
    Assert.Equal<(string * string) list>(
        [ DiagnosticCodes.OutsideModuleBlock.Code, "shapes.Point" ],
        codesAt (shapes + "module draw =\n  export def dot: shapes.Point -> int = { p -> p.x }\n"))

[<Fact>]
let ``A module block names only the imported types it imports itself`` () =
    withTempDir (fun dir ->
        File.WriteAllText(Path.Combine(dir, "people.nyx"), "module people\nexport type $Person = (name: string)\n")
        let compile (main: string) =
            let mainPath = Path.Combine(dir, "main.nyx")
            File.WriteAllText(mainPath, main)
            (Compiler.compileFile mainPath).Diagnostics |> List.map (fun diagnostic -> diagnostic.Code, textAt main (spanOf diagnostic))
        Assert.Equal<(string * string) list>(
            [ DiagnosticCodes.OutsideModuleBlock.Code, "$Person" ],
            compile "import \"people.nyx\"\n\nmodule greet =\n  export def name: $Person -> string = { p -> p.name }\n")
        Assert.Empty(compile "module greet =\n  import \"people.nyx\"\n  export def name: $Person -> string = { p -> p.name }\n"))
[<Fact>]
let ``#some() matches a tag carrying (), and () the unit value`` () =
    let save = "def save: int -> #some() | #failed = { n -> if n > 0 then #some() else #failed }\n"
    let both = save + "def f: int -> string = { n -> match save(n)\n  | #some() -> \"saved\"\n  | #failed -> \"failed\"\n}\n"
    Assert.Empty((Compiler.compile both).Diagnostics)
    let unit = "def g: () -> int = { u -> match u\n  | () -> 1\n}\n"
    Assert.Empty((Compiler.compile unit).Diagnostics)
    // `#some()` covers `#some` whole, so leaving out `#failed` is the only gap.
    let partial = save + "def f: int -> string = { n -> match save(n)\n  | #some() -> \"saved\"\n}\n"
    let diagnostic = Assert.Single((Compiler.compile partial).Diagnostics)
    Assert.Equal(DiagnosticCodes.NonExhaustiveMatch.Code, diagnostic.Code)
    Assert.Contains("failed", diagnostic.Message)

[<Fact>]
let ``() only matches the unit type`` () =
    let onInt = "def f: int -> int = { n -> match n\n  | () -> 1\n}\n"
    let diagnostic = Assert.Single((Compiler.compile onInt).Diagnostics)
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, diagnostic.Code)
    let onPayload = "def get: int -> #some(int) | #failed = { n -> #some(n) }\ndef f = { n -> match get(n)\n  | #some() -> 1\n  | #failed -> 0\n}\n"
    let diagnostics = (Compiler.compile onPayload).Diagnostics
    Assert.NotEmpty(diagnostics)
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, diagnostics.Head.Code)