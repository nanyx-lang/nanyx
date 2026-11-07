module Nanyx.Compiler.Tests

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program

/// The compiler without its analyzers. These tests are about what the compiler checks, and many of their programs
/// leave names unused (a lambda that ignores its argument, say), which the official analyzer would warn about.
/// `AnalyzerTests` tests the analyzers.
module private Compiler =
    let private options = { Analyzers.defaultOptions with Analyzers = [] }
    let compile (source: string) = NyxCompiler.Compiler.compileWith options source
    let compileFile (filePath: string) = NyxCompiler.Compiler.compileFileWith options filePath

let private withTempDir (action: string -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    try
        action dir
    finally
        if Directory.Exists dir then
            Directory.Delete(dir, true)

[<Fact>]
let ``Compile returns typed module for valid source`` () =
    let source = "def message = \"Hello\""
    let result = Compiler.compile source
    Assert.Equal(CompilationPhase.Typed, result.Phase)
    Assert.True(result.Diagnostics.IsEmpty)
    Assert.True(result.Typed.IsSome)

[<Fact>]
let ``Typecheck infers literal types`` () =
    let source = "def message = \"Hello\""
    let result = Compiler.compile source
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "string", typed.Types.["message"])

[<Fact>]
let ``Typecheck interpolated string`` () =
    let source =
        "def name = \"Ada\"\n" +
        "def msg = \"Hello {name}\""
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "string", typed.Types.["msg"])

[<Fact>]
let ``Import brings members into scope unqualified`` () =
    withTempDir (fun dir ->
        let modulePath = Path.Combine(dir, "mod.nyx")
        let mainPath = Path.Combine(dir, "main.nyx")
        File.WriteAllText(modulePath, "module mod\nexport def foo = 1")
        File.WriteAllText(mainPath, "module main\nimport \"mod.nyx\"\ndef value = foo")
        let result = Compiler.compileFile mainPath
        Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
        let typed = result.Typed.Value
        Assert.Equal(TyPrimitive "int", typed.Types.["value"]))

[<Fact>]
let ``Import with alias is qualified`` () =
    withTempDir (fun dir ->
        let modulePath = Path.Combine(dir, "mod.nyx")
        let mainPath = Path.Combine(dir, "main.nyx")
        File.WriteAllText(modulePath, "module mod\nexport def foo = 1")
        File.WriteAllText(mainPath, "module main\nimport \"mod.nyx\" as m1\ndef value = m1.foo")
        let result = Compiler.compileFile mainPath
        Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
        let typed = result.Typed.Value
        Assert.Equal(TyPrimitive "int", typed.Types.["value"]))

[<Fact>]
let ``Import with alias does not bring unqualified names`` () =
    withTempDir (fun dir ->
        let modulePath = Path.Combine(dir, "mod.nyx")
        let mainPath = Path.Combine(dir, "main.nyx")
        File.WriteAllText(modulePath, "module mod\nexport def foo = 1")
        File.WriteAllText(mainPath, "module main\nimport \"mod.nyx\" as m1\ndef value = foo")
        let result = Compiler.compileFile mainPath
        Assert.False(result.Diagnostics.IsEmpty)
        let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
        Assert.Contains("Unknown identifier", message))

let private compileImporting (moduleSource: string) (mainSource: string) =
    let mutable result = Unchecked.defaultof<CompileResult>
    withTempDir (fun dir ->
        File.WriteAllText(Path.Combine(dir, "mod.nyx"), moduleSource)
        let mainPath = Path.Combine(dir, "main.nyx")
        File.WriteAllText(mainPath, mainSource)
        result <- Compiler.compileFile mainPath)
    result

let private compileImportingAll (modules: (string * string) list) (mainSource: string) =
    let mutable result = Unchecked.defaultof<CompileResult>
    withTempDir (fun dir ->
        for fileName, source in modules do
            File.WriteAllText(Path.Combine(dir, fileName), source)
        let mainPath = Path.Combine(dir, "main.nyx")
        File.WriteAllText(mainPath, mainSource)
        result <- Compiler.compileFile mainPath)
    result

let private csvAndJson =
    [ "csv.nyx", "module csvParser\nexport def parse = { s -> 1 }\nexport def csvOnly = 1"
      "json.nyx", "module jsonParser\nexport def parse = { s -> 2 }" ]

[<Fact>]
let ``A name two imports export is ambiguous where it is used`` () =
    let result = compileImportingAll csvAndJson "module main\nimport \"csv.nyx\"\nimport \"json.nyx\"\ndef data = parse(\"x\")"
    Assert.False(result.Diagnostics.IsEmpty)
    Assert.Contains("'parse' is ambiguous between 'csvParser.parse' and 'jsonParser.parse'", (result.Diagnostics |> List.head).Message)

[<Fact>]
let ``A name two imports export is ambiguous as a value too`` () =
    let result = compileImportingAll csvAndJson "module main\nimport \"csv.nyx\"\nimport \"json.nyx\"\ndef f = parse"
    Assert.False(result.Diagnostics.IsEmpty)
    Assert.Contains("'parse' is ambiguous", (result.Diagnostics |> List.head).Message)

[<Fact>]
let ``A name two imports export is not an error when unused`` () =
    let result = compileImportingAll csvAndJson "module main\nimport \"csv.nyx\"\nimport \"json.nyx\"\ndef value = csvOnly"
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

[<Fact>]
let ``Aliasing one import resolves an ambiguous name`` () =
    let result = compileImportingAll csvAndJson "module main\nimport \"csv.nyx\" as csv\nimport \"json.nyx\"\ndef a = parse(\"x\")\ndef b = csv.parse(\"x\")"
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

[<Fact>]
let ``The module's own definition wins over a name two imports export`` () =
    let result = compileImportingAll csvAndJson "module main\nimport \"csv.nyx\"\nimport \"json.nyx\"\ndef parse = { s -> 3 }\ndef data = parse(\"x\")"
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

[<Fact>]
let ``A local shadows a name two imports export`` () =
    let result = compileImportingAll csvAndJson "module main\nimport \"csv.nyx\"\nimport \"json.nyx\"\ndef data = { parse -> parse }"
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

[<Fact>]
let ``Importing the same module twice is not ambiguous`` () =
    let result = compileImportingAll csvAndJson "module main\nimport \"csv.nyx\"\nimport \"./csv.nyx\"\ndef data = parse(\"x\")"
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

[<Fact>]
let ``A module imported along two paths is checked once`` () =
    let modules =
        [ "shared.nyx", "module shared\nexport def foo = 1"
          "left.nyx", "module left\nimport \"shared.nyx\"\nexport def l = foo"
          "right.nyx", "module right\nimport \"shared.nyx\"\nexport def r = foo" ]
    let result = compileImportingAll modules "module main\nimport \"left.nyx\"\nimport \"right.nyx\"\nimport \"shared.nyx\"\nimport \"./shared.nyx\" as s2\ndef a = foo\ndef b = s2.foo"
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let typed = result.Typed.Value
    let fooSymbols =
        typed.References
        |> Map.values
        |> Seq.distinct
        |> Seq.filter (fun id -> typed.Symbols |> Map.tryFind id |> Option.exists (fun symbol -> symbol.Name = "foo"))
        |> Seq.toList
    Assert.Equal(1, fooSymbols.Length)

[<Fact>]
let ``An error in a module imported along two paths is reported once`` () =
    let modules =
        [ "shared.nyx", "module shared\nexport def foo = undefinedName"
          "left.nyx", "module left\nimport \"shared.nyx\"\nexport def l = foo"
          "right.nyx", "module right\nimport \"shared.nyx\"\nexport def r = foo" ]
    let result = compileImportingAll modules "module main\nimport \"left.nyx\"\nimport \"right.nyx\"\ndef a = l"
    let unknown = result.Diagnostics |> List.filter (fun diag -> diag.Message.Contains "undefinedName")
    Assert.Equal(1, unknown.Length)

let private valueDefinitionNames (result: CompileResult) =
    result.Typed.Value.Module
    |> List.choose (function
        | Kind (Def (ValueDef(_, name, _, _))) -> Some name
        | _ -> None)
    |> Set.ofList

[<Fact>]
let ``Only the imported definitions a module reaches are inlined`` () =
    let modules =
        [ "mod.nyx",
          "module mod\n" +
          "def c = { 1 }\n" +
          "def b = { c() + 1 }\n" +
          "export def a = { b() + 1 }\n" +
          "export def unused = { 5 }" ]
    let result = compileImportingAll modules "module main\nimport \"mod.nyx\"\ndef x = a()"
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let names = valueDefinitionNames result
    Assert.Contains("a", names)
    Assert.Contains("mod.b", names)
    Assert.Contains("mod.c", names)
    Assert.DoesNotContain("unused", names)

[<Fact>]
let ``Only the standard-library definitions a program reaches are inlined`` () =
    let result = Compiler.compile "import nanyx/text\ndef x = String.contains(\"abc\", \"b\")"
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let names = valueDefinitionNames result
    Assert.Contains("String.contains", names)
    Assert.DoesNotContain("String.padStart", names)
    Assert.DoesNotContain("String.words", names)

[<Fact>]
let ``Definitions reached through an aliased import are inlined`` () =
    let modules = [ "mod.nyx", "module mod\nexport def a = { 1 }\nexport def unused = { 5 }" ]
    let result = compileImportingAll modules "module main\nimport \"mod.nyx\" as m\ndef x = m.a()"
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let names = valueDefinitionNames result
    Assert.Contains("m.a", names)
    Assert.DoesNotContain("m.unused", names)

[<Fact>]
let ``An attached name two imports define is ambiguous where it is used`` () =
    let modules =
        [ "one.nyx", "module one\nexport def String.shout: string -> string = { s -> s }"
          "two.nyx", "module two\nexport def String.shout: string -> string = { s -> s ++ \"!\" }" ]
    let result = compileImportingAll modules "module main\nimport \"one.nyx\"\nimport \"two.nyx\"\ndef a = \"x\" \\ shout"
    Assert.False(result.Diagnostics.IsEmpty)
    Assert.Contains("'String.shout' is ambiguous between 'one.String.shout' and 'two.String.shout'", (result.Diagnostics |> List.head).Message)

[<Fact>]
let ``Import does not bring private definitions into scope`` () =
    let result = compileImporting "module mod\nexport def foo = 1\ndef bar = 2" "module main\nimport \"mod.nyx\"\ndef value = bar"
    Assert.False(result.Diagnostics.IsEmpty)
    Assert.Contains("Unknown identifier", (result.Diagnostics |> List.head).Message)

[<Fact>]
let ``Import with alias does not expose private definitions`` () =
    let result = compileImporting "module mod\nexport def foo = 1\ndef bar = 2" "module main\nimport \"mod.nyx\" as m1\ndef value = m1.bar"
    Assert.False(result.Diagnostics.IsEmpty)

[<Fact>]
let ``Private definitions of an import cannot be reached by the module's name`` () =
    let result = compileImporting "module mod\nexport def foo = 1\ndef bar = 2" "module main\nimport \"mod.nyx\"\ndef value = mod.bar"
    Assert.False(result.Diagnostics.IsEmpty)

[<Fact>]
let ``Exported definitions of an import can use its private ones`` () =
    let result = compileImporting "module mod\nexport def foo = { bar + 1 }\ndef bar = 2" "module main\nimport \"mod.nyx\"\ndef value = foo()"
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    Assert.Equal(TyPrimitive "int", result.Typed.Value.Types.["value"])

[<Fact>]
let ``Import does not bring private types into scope`` () =
    let result = compileImporting "module mod\ntype Secret = int\nexport def foo = 1" "module main\nimport \"mod.nyx\"\ndef value: Secret = 1"
    Assert.False(result.Diagnostics.IsEmpty)

[<Fact>]
let ``Private definitions of a nested module are not visible outside it`` () =
    let result = Compiler.compile "module inner =\n  def secret = 1\n  export def open = secret + 1\ndef value = inner.secret"
    Assert.False(result.Diagnostics.IsEmpty)
    Assert.Contains("'inner.secret' is not exported from module 'inner'", (result.Diagnostics |> List.head).Message)

[<Fact>]
let ``Private functions of a nested module cannot be called outside it`` () =
    let result = Compiler.compile "module inner =\n  def secret = { x -> x + 1 }\n  export def open = { x -> secret(x) }\ndef value = inner.secret(1)"
    Assert.False(result.Diagnostics.IsEmpty)
    Assert.Contains("is not exported from module 'inner'", (result.Diagnostics |> List.head).Message)

[<Fact>]
let ``Exported definitions of a nested module are visible outside it`` () =
    let result = Compiler.compile "module inner =\n  def secret = 1\n  export def open = secret + 1\ndef value = inner.open"
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    Assert.Equal(TyPrimitive "int", result.Typed.Value.Types.["value"])

[<Fact>]
let ``Match rejects non-exhaustive tag unions`` () =
    let source =
        "def value: #some(int) | #none = #none\n" +
        "def result = match value\n" +
        "| #some(v) -> v\n"
    let result = Compiler.compile source
    Assert.False(result.Diagnostics.IsEmpty)
    let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
    Assert.Contains("Non-exhaustive match", message)

[<Fact>]
let ``Match checks exhaustiveness when the scrutinee is a call result or a local bound to one`` () =
    // #61: the scrutinee's type was still an unsolved variable when the check ran, so it passed silently.
    let direct =
        Compiler.compile (
            "def getMessage: () -> #some(string) | #noMessage = { #some(\"hi\") }\n" +
            "def partial = {\n" +
            "  match getMessage()\n" +
            "    | #some(_) -> \"message\"\n" +
            "}\n")
    Assert.False(direct.Diagnostics.IsEmpty, "a match on an annotated call result must be checked")
    Assert.Contains("missing tags: noMessage", (direct.Diagnostics |> List.head).Message)
    let viaLocal =
        Compiler.compile (
            "def getMessage: () -> #some(string) | #noMessage = { #some(\"hi\") }\n" +
            "def partial = {\n" +
            "  def m = getMessage()\n" +
            "  match m\n" +
            "    | #some(_) -> \"message\"\n" +
            "}\n")
    Assert.False(viaLocal.Diagnostics.IsEmpty, "a match on a local bound to a call result must be checked")
    let complete =
        Compiler.compile (
            "def getMessage: () -> #some(string) | #noMessage = { #some(\"hi\") }\n" +
            "def total = {\n" +
            "  match getMessage()\n" +
            "    | #some(_) -> \"message\"\n" +
            "    | #noMessage -> \"none\"\n" +
            "}\n")
    Assert.True(complete.Diagnostics.IsEmpty, sprintf "%A" complete.Diagnostics)

[<Fact>]
let ``Match accepts exhaustive tag unions`` () =
    let source =
        "def value: #some(int) | #none = #none\n" +
        "def result = match value\n" +
        "| #some(v) -> v\n" +
        "| #none -> 0\n"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)

[<Fact>]
let ``Match rejects non-exhaustive list patterns`` () =
    let source =
        "def values = [1, 2]\n" +
        "def result = match values\n" +
        "| [x, y] -> x\n"
    let result = Compiler.compile source
    Assert.False(result.Diagnostics.IsEmpty)
    let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
    Assert.Contains("Non-exhaustive match", message)

[<Fact>]
let ``Match accepts list catch-all`` () =
    let source =
        "def values = [1, 2]\n" +
        "def result = match values\n" +
        "| [x, y] -> x\n" +
        "| _ -> 0\n"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)

[<Fact>]
let ``Match accepts exhaustive list patterns`` () =
    let source =
        "def values = [1, 2]\n" +
        "def result = match values\n" +
        "| [] -> 0\n" +
        "| [head, ...tail] -> head\n"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)

[<Fact>]
let ``Match accepts an unnamed rest as covering every longer list`` () =
    // `[x, ...]` matches any list of one or more, like `[x, ...rest]`; it is not `[x]`.
    let source =
        "def values = [1, 2]\n" +
        "def result = match values\n" +
        "| [x, ...] -> x\n" +
        "| [] -> 0\n"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

[<Fact>]
let ``Typecheck list type application`` () =
    let source =
        "def sum: list(int) -> int = { xs -> 0 }\n" +
        "def values: list(string, int) = []"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

[<Fact>]
let ``Typecheck dbg returns its argument`` () =
    let source =
        "def value = dbg(1)\n" +
        "def text = dbg(\"hi\")"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "int", typed.Types.["value"])
    Assert.Equal(TyPrimitive "string", typed.Types.["text"])

[<Fact>]
let ``Typecheck self recursive function`` () =
    let source =
        "def fact = { n -> if n == 0 then 1 else n * fact(n - 1) }\n" +
        "def result = fact(3)"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "int", typed.Types.["result"])

[<Fact>]
let ``Match rejects non-exhaustive record patterns`` () =
    let source =
        "def point = (x = 1, y = 2)\n" +
        "def result = match point\n" +
        "| (x = 1) -> 1\n"
    let result = Compiler.compile source
    Assert.False(result.Diagnostics.IsEmpty)
    let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
    Assert.Contains("Non-exhaustive match", message)

[<Fact>]
let ``Match accepts record catch-all`` () =
    let source =
        "def point = (x = 1, y = 2)\n" +
        "def result = match point\n" +
        "| (x = 1) -> 1\n" +
        "| _ -> 0\n"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)

[<Fact>]
let ``Match rejects incompatible record patterns`` () =
    let source =
        "def point = (x = 1)\n" +
        "def result = match point\n" +
        "| (x = 1) -> 1\n" +
        "| \"oops\" -> 0\n" +
        "| _ -> 0\n"
    let result = Compiler.compile source
    Assert.False(result.Diagnostics.IsEmpty)
    let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
    Assert.Contains("Type mismatch", message)

[<Fact>]
let ``Match rejects non-exhaustive tag unions with multiple scrutinees`` () =
    let source =
        "def a: #left | #right = #left\n" +
        "def b: #up | #down = #up\n" +
        "def result = match a, b\n" +
        "| #left, #up -> 1\n" +
        "| #right, #up -> 2\n"
    let result = Compiler.compile source
    Assert.False(result.Diagnostics.IsEmpty)
    let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
    Assert.Contains("Non-exhaustive match", message)

[<Fact>]
let ``Typecheck dotted def name`` () =
    let source =
        "type Person = (firstName: string)\n" +
        "def Person.fullName: Person -> string = { p -> p.firstName }"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)
    let typed = result.Typed.Value
    match typed.Types.["Person.fullName"] with
    | TyFunc(TyAlias("Person", [], TyRecord fields), TyPrimitive "string") when fields.ContainsKey("firstName") ->
        Assert.True(true)
    | other -> Assert.True(false, $"Unexpected dotted def type: %A{other}")

[<Fact>]
let ``Typecheck ws-sensitive module block`` () =
    let source =
        "module Root\n" +
        "module Inner =\n" +
        "  def x = 1\n" +
        "  def y = x"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "int", typed.Types.["Root.Inner.x"])
    Assert.Equal(TyPrimitive "int", typed.Types.["Root.Inner.y"])

[<Fact>]
let ``Typecheck infers attached receiver type for unannotated lambda`` () =
    let source =
        "type Person = (firstName: string)\n" +
        "def Person.toString = { p -> \"person\" }"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let typed = result.Typed.Value
    match typed.Types.["Person.toString"] with
    | TyFunc(TyAlias("Person", [], TyRecord fields), TyPrimitive "string") when fields.ContainsKey("firstName") ->
        Assert.True(true)
    | other -> Assert.True(false, $"Unexpected inferred attached def type: %A{other}")

[<Fact>]
let ``Typecheck infers nominal attached receiver type for unannotated lambda`` () =
    let source =
        "type $Email = string\n" +
        "def $Email.toString = { e -> \"ok\" }"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let typed = result.Typed.Value
    match typed.Types.["$Email.toString"] with
    | TyFunc(TyNominal(name, _, _), TyPrimitive "string") when name = "$Email" ->
        Assert.True(true)
    | other -> Assert.True(false, $"Unexpected inferred nominal attached def type: %A{other}")

[<Fact>]
let ``Typecheck infers nominal attached receiver as argument in body`` () =
    let source =
        "type $Email = string\n" +
        "def $Email.toDisplay = { e -> e }"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let typed = result.Typed.Value
    match typed.Types.["$Email.toDisplay"] with
    | TyFunc(TyNominal(nameIn, _, _), TyNominal(nameOut, _, _)) when nameIn = "$Email" && nameOut = "$Email" ->
        Assert.True(true)
    | other -> Assert.True(false, $"Unexpected inferred nominal identity attached def type: %A{other}")

[<Fact>]
let ``Typecheck rejects attached def with missing type`` () =
    let source = "def A.a = { s -> s }"
    let result = Compiler.compile source
    Assert.False(result.Diagnostics.IsEmpty)
    let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
    Assert.Contains("Unknown attached type", message)

[<Fact>]
let ``Typecheck allows attached defs on built-in types`` () =
    let source =
        "def Int.parse: string -> int = { s -> 0 }\n" +
        "def String.shout: string -> string = { s -> s }\n" +
        "def List.isBlank: list(int) -> bool = { xs -> true }"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let typed = result.Typed.Value
    Assert.Equal(TyFunc(TyPrimitive "string", TyPrimitive "int"), typed.Types.["Int.parse"])

[<Fact>]
let ``Typecheck infers primitive attached receiver type for unannotated lambda`` () =
    let source = "def Int.double = { n -> n * 2 }"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let typed = result.Typed.Value
    Assert.Equal(TyFunc(TyPrimitive "int", TyPrimitive "int"), typed.Types.["Int.double"])

[<Fact>]
let ``Typecheck pipe resolves attached functions on built-in types`` () =
    let source =
        "def Int.double: int -> int = { n -> n * 2 }\n" +
        "def n = 3 \\ double"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "int", typed.Types.["n"])

[<Fact>]
let ``Typecheck pipe resolves attached functions on a type known only from earlier constraints`` () =
    let source =
        "def Int.double: int -> int = { n -> n * 2 }\n" +
        "def String.double: string -> string = { s -> s ++ s }\n" +
        "def id = { x -> x }\n" +
        "def n = id(3) \\ double\n" +
        "def s = id(\"a\") \\ double"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "int", typed.Types.["n"])
    Assert.Equal(TyPrimitive "string", typed.Types.["s"])

[<Fact>]
let ``Typecheck pipe resolves attached functions on a lambda parameter pinned earlier in the body`` () =
    let source =
        "def Int.double: int -> int = { n -> n * 2 }\n" +
        "def String.double: string -> string = { s -> s ++ s }\n" +
        "def f = { x ->\n" +
        "  def y: int = x\n" +
        "  x \\ double\n" +
        "}"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let typed = result.Typed.Value
    Assert.Equal(TyFunc(TyPrimitive "int", TyPrimitive "int"), typed.Types.["f"])

[<Fact>]
let ``Typecheck pipe resolves attached functions`` () =
    let source =
        "type Person = (firstName: string surname: string)\n" +
        "def Person.fullName: Person -> string = { p -> p.firstName }\n" +
        "def p: Person = (firstName = \"\", surname = \"\")\n" +
        "def n = p \\ Person.fullName\n" +
        "def n2 = p \\ fullName"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "string", typed.Types.["n"])
    Assert.Equal(TyPrimitive "string", typed.Types.["n2"])

[<Fact>]
let ``Typecheck pipe resolves attached functions on nominals`` () =
    let source =
        "type $Email = string\n" +
        "def $Email.toString: $Email -> string = { e -> \"\" }\n" +
        "def $Email.format: $Email -> string = { e -> \"\" }\n" +
        "def email: $Email = \"a@b.com\"\n" +
        "def s = email \\ toString\n" +
        "def s2 = email \\ format"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "string", typed.Types.["s"])
    Assert.Equal(TyPrimitive "string", typed.Types.["s2"])

[<Fact>]
let ``Typecheck pipe picks the attached function by the name of the value's type`` () =
    let source =
        "type Option(a) = #some(a) | #none\n" +
        "def List.map: (list(a), (a -> b)) -> list(b) = { xs, f -> [] }\n" +
        "def Option.map: (Option(a), (a -> b)) -> Option(b) = { o, f -> #none }\n" +
        "def xs: list(int) = [1, 2]\n" +
        "def o: Option(int) = #some(1)\n" +
        "def ys = xs \\ map({ x -> x > 1 })\n" +
        "def p = o \\ map({ x -> \"a\" })"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let typed = result.Typed.Value
    Assert.Equal(TyApply("list", [ TyPrimitive "bool" ]), typed.Types.["ys"])
    match Unifier.unalias typed.Types.["p"] with
    | TyUnion options -> Assert.Contains(TyTag("some", Some (TyPrimitive "string")), options)
    | other -> Assert.True(false, $"Unexpected type for p: %A{other}")

[<Fact>]
let ``Typecheck pipe calls a plain function when the value's type attaches none`` () =
    let source =
        "def Int.describe: int -> string = { n -> \"int\" }\n" +
        "def describe: string -> string = { s -> s }\n" +
        "def a = \"x\" \\ describe\n" +
        "def b = 1 \\ describe"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "string", typed.Types.["a"])
    Assert.Equal(TyPrimitive "string", typed.Types.["b"])

[<Fact>]
let ``Typecheck pipe does not find attached functions of a module imported with as`` () =
    let modules = [ "one.nyx", "module one\nexport def String.shout: string -> string = { s -> s ++ \"!\" }" ]
    let unqualified = compileImportingAll modules "module main\nimport \"one.nyx\" as t\ndef a = \"x\" \\ shout"
    Assert.False(unqualified.Diagnostics.IsEmpty)
    let qualified = compileImportingAll modules "module main\nimport \"one.nyx\" as t\ndef a = \"x\" \\ t.String.shout"
    Assert.True(qualified.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{qualified.Diagnostics}")
    Assert.Equal(TyPrimitive "string", qualified.Typed.Value.Types.["a"])

[<Fact>]
let ``Typecheck supports polymorphic let`` () =
    let source =
        "def id = { x -> x }\n" +
        "def a = id(1)\n" +
        "def b = id(\"hi\")"
    let result = Compiler.compile source
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "int", typed.Types.["a"])
    Assert.Equal(TyPrimitive "string", typed.Types.["b"])

[<Fact>]
let ``Typecheck expands generic type aliases in annotations`` () =
    let source =
        "type Async(a) = #done(a) | #pending(a)\n" +
        "def fromResult: a -> Async(a) = { r -> #done(r) }\n" +
        "def value = fromResult(5)"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

[<Fact>]
let ``Typecheck uses tuple input for multi-arg functions`` () =
    let source =
        "def f = { x, y -> x }\n" +
        "def result = f(1, 2)"
    let result = Compiler.compile source
    let typed = result.Typed.Value
    match typed.Types.["f"] with
    | TyFunc(TyRecord fields, TyVar _) when fields.ContainsKey("0") && fields.ContainsKey("1") ->
        Assert.True(true)
    | other -> Assert.True(false, $"Unexpected function type: %A{other}")
    Assert.Equal(TyPrimitive "int", typed.Types.["result"])

[<Fact>]
let ``Typecheck uses unit input for zero-arg functions`` () =
    let source =
        "def f = { 1 }\n" +
        "def result = f()"
    let result = Compiler.compile source
    let typed = result.Typed.Value
    Assert.Equal(TyFunc(TyPrimitive "unit", TyPrimitive "int"), typed.Types.["f"])
    Assert.Equal(TyPrimitive "int", typed.Types.["result"])

[<Fact>]
let ``Typecheck allows context members with annotation`` () =
    let source =
        "context Console = (println: string -> ())\n" +
        "def sayHello: [Console] () -> () = { println(\"hi\") }"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)

[<Fact>]
let ``Typecheck rejects an unknown context in a signature`` () =
    let source =
        "context MyConsole = (p: string -> ())\n" +
        "def greet: [Bogus] string -> () = { name -> p(name) }"
    let result = Compiler.compile source
    let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
    Assert.Contains("Unknown context 'Bogus' in the signature of 'greet'", message)

[<Fact>]
let ``Typecheck rejects an unknown context in a local signature`` () =
    let source =
        "def main = {\n" +
        "  def greet: [Bogus] string -> () = { name -> () }\n" +
        "  greet(\"hi\")\n" +
        "}"
    let result = Compiler.compile source
    let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
    Assert.Contains("Unknown context 'Bogus' in the signature of 'greet'", message)

[<Fact>]
let ``Typecheck accepts the Console context from nanyx/console`` () =
    let source =
        "import nanyx/console\n\n" +
        "def greet: [Console] string -> () = { name -> println(name) }"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

[<Fact>]
let ``println comes from nanyx/console with type string -> unit`` () =
    let result = Compiler.compile "import nanyx/console\n\ndef result = println(\"hi\")"
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    Assert.Equal(TyPrimitive "unit", result.Typed.Value.Types.["result"])

[<Fact>]
let ``println rejects a non-string argument`` () =
    let result = Compiler.compile "import nanyx/console\n\nprintln(42)"
    Assert.False(result.Diagnostics.IsEmpty)

[<Fact>]
let ``println is not in scope without importing nanyx/console`` () =
    let result = Compiler.compile "def main = { println(\"hi\") }"
    let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
    Assert.Contains("Unknown function 'println'", message)

[<Fact>]
let ``Standard library import is resolved without a file`` () =
    withTempDir (fun dir ->
        let mainPath = Path.Combine(dir, "main.nyx")
        File.WriteAllText(mainPath, "import nanyx/console\n\nprintln(\"Hello, Nanyx!\")\n")
        let result = Compiler.compileFile mainPath
        Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
        // The module is compiled together with the program: the backend sees its definitions, not the import.
        let hasImport = result.Typed.Value.Module |> List.exists (function Kind (Import _) -> true | _ -> false)
        Assert.False(hasImport)
        let hasPrintln = result.Typed.Value.Module |> List.exists (function Kind (Def (ValueDef(_, "println", _, _))) -> true | _ -> false)
        Assert.True(hasPrintln))

[<Fact>]
let ``nanyx/console declares the Console context`` () =
    let source =
        "import nanyx/console\n" +
        "def greet: [Console] string -> () = { name -> println(name) }\n" +
        "def main = { greet(\"hi\") }"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    Assert.True(result.Typed.Value.TypeDefs.ContainsKey "Console")

[<Fact>]
let ``nanyx/text exports string functions with their types`` () =
    let source =
        "import nanyx/text\n" +
        "def shout = String.uppercase(\"hi\")\n" +
        "def n = String.length(\"hi\")\n" +
        "def parts = String.split(\"a,b\", \",\")\n" +
        "def joined = String.join(parts, \"-\")\n" +
        "def yes = String.startsWith(\"nanyx\", \"nan\")\n" +
        "def found = String.indexOf(\"nanyx\", \"yx\")\n" +
        "def lastFound = String.lastIndexOf(\"nanyx\", \"n\")\n" +
        "def times = String.count(\"nanyx\", \"n\")\n" +
        "def replaced = String.replace(\"nanyx\", \"n\", \"N\")\n" +
        "def padded = String.padStart(\"7\", 3, \"0\")\n" +
        "def blank = String.isBlank(\" \")\n" +
        "def allLines = String.lines(\"a\")\n" +
        "def allWords = String.words(\"a b\")"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let types = result.Typed.Value.Types
    let indexResult = TyUnion [ TyTag("some", Some (TyPrimitive "int")); TyTag("notFound", None) ]
    Assert.Equal(TyPrimitive "string", types.["shout"])
    Assert.Equal(TyPrimitive "int", types.["n"])
    Assert.Equal(TyApply("list", [ TyPrimitive "string" ]), types.["parts"])
    Assert.Equal(TyPrimitive "string", types.["joined"])
    Assert.Equal(TyPrimitive "bool", types.["yes"])
    Assert.Equal(indexResult, types.["found"])
    Assert.Equal(indexResult, types.["lastFound"])
    Assert.Equal(TyPrimitive "int", types.["times"])
    Assert.Equal(TyPrimitive "string", types.["replaced"])
    Assert.Equal(TyPrimitive "string", types.["padded"])
    Assert.Equal(TyPrimitive "bool", types.["blank"])
    Assert.Equal(TyApply("list", [ TyPrimitive "string" ]), types.["allLines"])
    Assert.Equal(TyApply("list", [ TyPrimitive "string" ]), types.["allWords"])

[<Fact>]
let ``nanyx/text rejects a wrongly typed argument`` () =
    let result = Compiler.compile "import nanyx/text\n\ndef bad = String.repeat(\"ab\", \"twice\")"
    Assert.False(result.Diagnostics.IsEmpty)

[<Fact>]
let ``Standard library private helpers are not imported`` () =
    let result = Compiler.compile "import nanyx/text\n\ndef bad = indexFrom(\"abc\", \"b\", 0)"
    let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
    Assert.Contains("Unknown function 'indexFrom'", message)

[<Fact>]
let ``Compiler intrinsics are not in scope in user code`` () =
    let result = Compiler.compile "import nanyx/text\n\ndef bad = __nanyx_string_length(\"abc\")"
    let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
    Assert.Contains("Unknown function '__nanyx_string_length'", message)

[<Fact>]
let ``Standard library import with alias is qualified`` () =
    let source =
        "import nanyx/text as t\n" +
        "def shout = t.String.uppercase(\"hi\")"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    Assert.Equal(TyPrimitive "string", result.Typed.Value.Types.["shout"])
    let unqualified = Compiler.compile "import nanyx/text as t\n\ndef shout = String.uppercase(\"hi\")"
    Assert.False(unqualified.Diagnostics.IsEmpty)

[<Fact>]
let ``nanyx/text functions are attached to String`` () =
    let result = Compiler.compile "import nanyx/text\ndef s = \" a \"\ndef piped = s \\ trim\ndef called = String.trim(s)"
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    Assert.Equal(TyPrimitive "string", result.Typed.Value.Types.["piped"])
    Assert.Equal(TyPrimitive "string", result.Typed.Value.Types.["called"])
    let bare = Compiler.compile "import nanyx/text\ndef x = trim(\" a \")"
    Assert.Contains("Unknown function 'trim'", (List.head bare.Diagnostics).Message)

[<Fact>]
let ``nanyx/text imported with as is only reached qualified from a pipe`` () =
    let qualified = Compiler.compile "import nanyx/text as t\ndef x = \" a \" \\ t.String.trim"
    Assert.True(qualified.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{qualified.Diagnostics}")
    let short = Compiler.compile "import nanyx/text as t\ndef x = \" a \" \\ trim"
    Assert.False(short.Diagnostics.IsEmpty)

[<Fact>]
let ``Unknown standard library module is an error`` () =
    withTempDir (fun dir ->
        let mainPath = Path.Combine(dir, "main.nyx")
        File.WriteAllText(mainPath, "import nanyx/sockets\n")
        let result = Compiler.compileFile mainPath
        let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
        Assert.Contains("Unknown standard library module 'nanyx/sockets'", message)
        Assert.Contains("nanyx/console", message))

[<Fact>]
let ``Entry point module sees nanyx/console without importing it`` () =
    let source =
        "module main\n" +
        "@entry\n" +
        "def main = {\n" +
        "  println(\"Hello, World!\")\n" +
        "}\n"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let withoutEntry = Compiler.compile "module main\n\ndef main = {\n  println(\"Hello, World!\")\n}\n"
    Assert.False(withoutEntry.Diagnostics.IsEmpty)

[<Fact>]
let ``Entry attribute appends a call to the entry point`` () =
    let source =
        "module main\n" +
        "@entry\n" +
        "def main = {\n" +
        "  println(\"Hello, World!\")\n" +
        "}\n"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let entryCalls =
        result.Typed.Value.Module
        |> List.filter (function Kind (Expr (Kind (FunctionCall("main", _, [])))) -> true | _ -> false)
    Assert.Equal(1, entryCalls.Length)
    let hasAttribute = result.Typed.Value.Module |> List.exists (function Kind (Attribute _) -> true | _ -> false)
    Assert.False(hasAttribute)

[<Fact>]
let ``Unknown attribute is an error, reported at the attribute`` () =
    let result = Compiler.compile "def g = 2\n@inline\ndef f = { 1 }"
    let diagnostic = result.Diagnostics |> List.head
    Assert.Contains("Unknown attribute '@inline'", diagnostic.Message)
    let span = diagnostic.Span.Value
    Assert.Equal((10, 17, 1, 0), (span.Start, span.End, span.StartLine, span.StartColumn))

[<Fact>]
let ``Entry attribute must precede a definition, and is reported at the attribute`` () =
    let result = Compiler.compile "@entry\n1 + 1"
    let diagnostic = result.Diagnostics |> List.head
    Assert.Contains("must be followed by a definition", diagnostic.Message)
    let span = diagnostic.Span.Value
    Assert.Equal((0, 6), (span.Start, span.End))

[<Fact>]
let ``Typecheck rejects missing context members`` () =
    let source =
        "context Console = (println: string -> ())\n" +
        "def sayHello = { println(\"hi\") }"
    let result = Compiler.compile source
    Assert.False(result.Diagnostics.IsEmpty)
    let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
    Assert.Contains("Unknown", message)

[<Fact>]
let ``Typecheck allows use statement to bring members into scope`` () =
    let source =
        "context Console = (println: string -> string)\n" +
        "def main = {\n" +
        "  use Console(println = { msg -> msg })\n" +
        "  println(\"hi\")\n" +
        "}"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)

[<Fact>]
let ``Typecheck allows use-in expression`` () =
    let source =
        "type Ctx = (value: int)\n" +
        "def result = use Ctx(value = 1) in value"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "int", typed.Types.["result"])

[<Fact>]
let ``Typecheck supports unit literal`` () =
    let source = "def f = { () }"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)
    let typed = result.Typed.Value
    Assert.Equal(TyFunc(TyPrimitive "unit", TyPrimitive "unit"), typed.Types.["f"])

[<Fact>]
let ``Typecheck rejects missing context on call`` () =
    let source =
        "context Console = (println: string -> ())\n" +
        "def g: [Console] () -> () = { println(\"hi\") }\n" +
        "def h = { g() }"
    let result = Compiler.compile source
    Assert.False(result.Diagnostics.IsEmpty)
    let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
    Assert.Contains("Missing context", message)

[<Fact>]
let ``Typecheck supports record constructor syntax`` () =
    let source =
        "type Person = (firstName: string surname: string)\n" +
        "def p = Person(firstName = \"Joe\", surname = \"Bloggs\")"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)
    let typed = result.Typed.Value
    match typed.Types.["p"] with
    | TyAlias("Person", [], TyRecord fields) ->
        Assert.Equal(TyPrimitive "string", fields.["firstName"])
        Assert.Equal(TyPrimitive "string", fields.["surname"])
    | other -> Assert.True(false, $"Unexpected constructor type: %A{other}")

[<Fact>]
let ``Typecheck supports mixed positional and named records`` () =
    let source = "def value = (1, 2, z = 3)"
    let result = Compiler.compile source
    let typed = result.Typed.Value
    match typed.Types.["value"] with
    | TyRecord fields when fields.ContainsKey("0") && fields.ContainsKey("1") && fields.ContainsKey("z") ->
        Assert.Equal(TyPrimitive "int", fields.["0"])
        Assert.Equal(TyPrimitive "int", fields.["1"])
        Assert.Equal(TyPrimitive "int", fields.["z"])
    | other -> Assert.True(false, $"Unexpected mixed record type: %A{other}")

[<Fact>]
let ``Typecheck allows tuple args variable for function call`` () =
    let source =
        "def f = { x, y -> \"\" }\n" +
        "def args = 4, 5\n" +
        "def result = f(args)"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "string", typed.Types.["result"])

[<Fact>]
let ``Typecheck reports mismatch for missing tuple args`` () =
    let source =
        "def f = { x, y -> \"\" }\n" +
        "def result = f(4)"
    let result = Compiler.compile source
    Assert.False(result.Diagnostics.IsEmpty)
    let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
    Assert.Contains("Type mismatch", message)
    Assert.Contains("(", message)
    Assert.Contains("vs int", message)

[<Fact>]
let ``Typecheck allows record width subtyping`` () =
    let source =
        "def f: (a: int) -> int = { r -> 1 }\n" +
        "def args = (a = 1, b = 2)\n" +
        "def result = f(args)"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "int", typed.Types.["result"])

[<Fact>]
let ``Typecheck treats union order as assignable`` () =
    let source =
        "def f: (#some(int) | #notFound) -> int = { x -> 1 }\n" +
        "def g: (#notFound | #some(int)) -> int = f\n" +
        "def result = g(#some(1))"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "int", typed.Types.["result"])

[<Fact>]
let ``Typecheck allows wider unions in assignment`` () =
    let source =
        "def f: (#some(int) | #notFound) -> int = { x -> 1 }\n" +
        "def g: (#some(int) | #notFound | #error(string)) -> int = f\n" +
        "def result = g(#notFound)"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "int", typed.Types.["result"])

[<Fact>]
let ``Typecheck supports tag union rest parameter`` () =
    let source =
        "def mapOption: (#some(a) | r), (a -> b) -> #some(b) | r = { o, f ->\n" +
        "  match o\n" +
        "  | #some(v) -> #some(f(v))\n" +
        "  | other -> other\n" +
        "}\n" +
        "def result = mapOption(#some(1), { x -> x })"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)
    let typed = result.Typed.Value
    // `#some(1)` has no other tags, so the rest is empty.
    Assert.Equal(TyTag("some", Some (TyPrimitive "int")), typed.Types.["result"])

[<Fact>]
let ``Typecheck rejects an annotation more general than its body`` () =
    let result = Compiler.compile "def f: a -> b = { x -> x }"
    Assert.False(result.Diagnostics.IsEmpty)
    let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
    Assert.Contains("more general than the inferred type", message)
    Assert.Contains("`b` would have to be `a`", message)

[<Fact>]
let ``Typecheck rejects an annotation variable the body makes concrete`` () =
    let result = Compiler.compile "def g: a -> int = { x -> x + 1 }"
    Assert.False(result.Diagnostics.IsEmpty)
    let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
    Assert.Contains("`a` would have to be `int`", message)

[<Fact>]
let ``Typecheck rejects a non-function annotation more general than its value`` () =
    let result = Compiler.compile "def xs: list(a) = [1, 2]"
    Assert.False(result.Diagnostics.IsEmpty)
    let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
    Assert.Contains("`a` would have to be `int`", message)

[<Fact>]
let ``Typecheck accepts annotations the body proves`` () =
    let source =
        "def id: a -> a = { x -> x }\n" +
        "def first: (a, b) -> a = { x, y -> x }\n" +
        "def apply: (a -> b), a -> b = { f, x -> f(x) }\n" +
        "def mapOption: (#some(a) | r), (a -> b) -> (#some(b) | r) = { | #some(a), f -> #some(f(a)) | other, _ -> other }\n" +
        "def result = mapOption(#some(first(1, \"\")), { x -> apply(id, x) })"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"%A{result.Diagnostics}")
    let typed = result.Typed.Value
    Assert.Equal(TyTag("some", Some (TyPrimitive "int")), typed.Types.["result"])

[<Fact>]
let ``Typecheck requires catch-all for tag union rest`` () =
    let source =
        "def mapOption: (#some(a) | r), (a -> b) -> #some(b) | r = { o, f ->\n" +
        "  match o\n" +
        "  | #some(v) -> #some(f(v))\n" +
        "}\n"
    let result = Compiler.compile source
    Assert.False(result.Diagnostics.IsEmpty)
    let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
    Assert.Contains("catch-all", message)

[<Fact>]
let ``Typecheck tag union width and rest regression`` () =
    let source =
        "def u: #some(string) | #nil = #nil\n" +
        "def g: (#some(string) | #nil | #notFound) -> string = { o -> \"\" }\n" +
        "def _ = g(u)\n" +
        "def mapOption: (#some(a) | r), (a -> b) -> #some(b) | r = { o, f ->\n" +
        "  match o\n" +
        "  | #some(v) -> #some(f(v))\n" +
        "  | other -> other\n" +
        "}\n" +
        "def l: #some(string) | #nil = #some(\"\")\n" +
        "def mapper: string -> int = { s -> 1 }\n" +
        "def l2 = l \\mapOption(mapper)"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)
    let typed = result.Typed.Value
    match typed.Types.["l2"] with
    | TyUnion [ TyTag("some", Some (TyPrimitive "int")); TyTag("nil", None) ] -> Assert.True(true)
    | other -> Assert.True(false, $"Unexpected l2 type: %A{other}")

[<Fact>]
let ``Typecheck binds a catch-all after a tag arm at the union's rest`` () =
    // `other` cannot be a `#some`, so it is the rest `r`: returning it does not force `a` and `b` together.
    let source =
        "def mapOption\n" +
        "  : (#some(a) | r), (a -> b) -> (#some(b) | r)\n" +
        "  = { | #some(a), f -> #some(f(a))\n" +
        "      | other, _ -> other }\n" +
        "def x: #some(int) | #notFound | #error = #some(1)\n" +
        "def y = mapOption(x, { n -> \"{n}\" })"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    let typed = result.Typed.Value
    match typed.Types.["mapOption"] with
    | TyFunc(TyRecord fields, TyUnion [ TyTag("some", Some (TyVar b)); TyVar resultRest ]) ->
        match fields.["0"], fields.["1"] with
        | TyUnion [ TyTag("some", Some (TyVar a)); TyVar inputRest ], TyFunc(TyVar fa, TyVar fb) ->
            Assert.NotEqual(a.Id, b.Id)
            Assert.Equal(a.Id, fa.Id)
            Assert.Equal(b.Id, fb.Id)
            Assert.Equal(inputRest.Id, resultRest.Id)
        | other -> Assert.True(false, $"Unexpected mapOption parameters: %A{other}")
    | other -> Assert.True(false, $"Unexpected mapOption type: %A{other}")
    match typed.Types.["y"] with
    | TyUnion [ TyTag("some", Some (TyPrimitive "string")); TyTag("error", None); TyTag("notFound", None) ] -> ()
    | other -> Assert.True(false, $"Unexpected y type: %A{other}")

[<Fact>]
let ``Typecheck gives a lambda argument's parameter its type from the callee`` () =
    // `p.x` is checked inside the lambda, before the call is solved: `p` is a `P` from `apply`'s signature.
    let source =
        "type P = (x: int)\n" +
        "def apply: P, (P -> int) -> int = { p, f -> f(p) }\n" +
        "def n = P(x = 3) \\apply { p -> p.x }"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"%A{result.Diagnostics}")
    Assert.Equal(TyPrimitive "int", result.Typed.Value.Types.["n"])

[<Fact>]
let ``Typecheck gives a lambda argument's parameter a type solved from the arguments before it`` () =
    // `a` in `mapOption`'s signature is only known from the first argument, a `#some(Customer)`.
    let source =
        "type Customer = (name: string)\n" +
        "def mapOption\n" +
        "  : (#some(a) | r), (a -> b) -> (#some(b) | r)\n" +
        "  = { | #some(a), f -> #some(f(a))\n" +
        "      | other, _ -> other }\n" +
        "def c: #some(Customer) | #notFound = #notFound\n" +
        "def name = c \\mapOption { c -> c.name }"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"%A{result.Diagnostics}")
    match result.Typed.Value.Types.["name"] with
    | TyUnion [ TyTag("some", Some (TyPrimitive "string")); TyTag("notFound", None) ] -> ()
    | other -> Assert.True(false, $"Unexpected name type: %A{other}")

[<Fact>]
let ``Typecheck matches a call's result whose union rest is already solved`` () =
    // `mapOption`'s result is `#some(b) | r`, but here `r` is `#notFound`, so naming both tags is exhaustive.
    let mapOption =
        "def mapOption\n" +
        "  : (#some(a) | r), (a -> b) -> (#some(b) | r)\n" +
        "  = { | #some(a), f -> #some(f(a))\n" +
        "      | other, _ -> other }\n" +
        "def x: #some(int) | #notFound = #notFound\n"
    let exhaustive =
        mapOption +
        "def y = {\n" +
        "  def a = x \\mapOption { n -> n + 1 }\n" +
        "  match a\n" +
        "    | #some(n) -> n\n" +
        "    | #notFound -> 0\n" +
        "}\n"
    let result = Compiler.compile exhaustive
    Assert.True(result.Diagnostics.IsEmpty, $"%A{result.Diagnostics}")
    let missing =
        mapOption +
        "def y = {\n" +
        "  def a = x \\mapOption { n -> n + 1 }\n" +
        "  match a\n" +
        "    | #some(n) -> n\n" +
        "}\n"
    let result = Compiler.compile missing
    Assert.False(result.Diagnostics.IsEmpty)
    Assert.Contains("missing tags: notFound", (List.head result.Diagnostics).Message)

[<Fact>]
let ``Typecheck rejects a tag union with a rest where a string is expected`` () =
    // The rest `r` stands for more tags, so it cannot make `#some(string) | r` a `string`.
    let source =
        "def mapOption\n" +
        "  : (#some(a) | r), (a -> b) -> (#some(b) | r)\n" +
        "  = { | #some(a), f -> #some(f(a))\n" +
        "      | other, _ -> other }\n" +
        "def takesString: string -> string = { s -> s }\n" +
        "def x: #some(int) | #notFound = #notFound\n" +
        "def y = takesString(x \\mapOption { n -> \"{n}\" })"
    let result = Compiler.compile source
    Assert.False(result.Diagnostics.IsEmpty)
    Assert.Contains("mismatch", (List.head result.Diagnostics).Message)

[<Fact>]
let ``Desugar workflow bind and return`` () =
    let source =
        "def result = async {\n" +
        "  def x = await! foo()\n" +
        "  return! x.y\n" +
        "}"
    match parseModule source with
    | Result.Error err -> Assert.True(false, $"Parse error: {err}")
    | Result.Ok ast ->
        let desugared = Compiler.desugar ast
        // `async { ... }` is its block, each keyword calling the builder's member through a local of its own.
        match desugared with
        | [Kind (Def (ValueDef(_, "result", _, Kind (Block [Kind (ExprStatement expr)]))))] ->
            match expr.Kind with
            | Block [
                Kind (DefStatement(_, awaitLocal, None, Kind (MemberAccess(Kind (IdentifierExpr "async"), "await", _))))
                Kind (ExprStatement (Kind (FunctionCall(awaitCall, _, [ Kind (FunctionCall("foo", _, [])); Kind (Lambda([("x", None)], continuation)) ]))))
              ] when awaitCall = awaitLocal ->
                match continuation.Kind with
                | Block [
                    Kind (DefStatement(_, returnLocal, None, Kind (MemberAccess(Kind (IdentifierExpr "async"), "return", _))))
                    Kind (ExprStatement (Kind (FunctionCall(returnCall, _, [ Kind (MemberAccess(Kind (IdentifierExpr "x"), "y", _)) ]))))
                  ] when returnCall = returnLocal -> ()
                | other -> Assert.True(false, $"Unexpected workflow continuation: %A{other}")
            | other -> Assert.True(false, $"Unexpected workflow desugar: %A{other}")
        | other -> Assert.True(false, $"Unexpected desugared module: %A{other}")

[<Fact>]
let ``Desugar workflow implicit return`` () =
    let source =
        "def result = async {\n" +
        "  1\n" +
        "}"
    match parseModule source with
    | Result.Error err -> Assert.True(false, $"Parse error: {err}")
    | Result.Ok ast ->
        let desugared = Compiler.desugar ast
        match desugared with
        | [Kind (Def (ValueDef(_, "result", _, Kind (FunctionCall("async", _, [Kind (Lambda([], Kind (Block [Kind (ExprStatement (Kind (LiteralExpr (IntLit 1))))])))])))))] ->
            Assert.True(true)
        | other -> Assert.True(false, $"Unexpected desugared module: %A{other}")

[<Fact>]
let ``Typecheck workflow context members`` () =
    let source =
        "context AsyncWorkflow = (await: (int, (int -> int)) -> int return: int -> int)\n" +
        "def async: AsyncWorkflow = (await = { x, f -> f(x) }, return = { x -> x })\n" +
        "def result = async {\n" +
        "  def x = await! 1\n" +
        "  return! x\n" +
        "}"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)

[<Fact>]
let ``Typecheck workflow requires return`` () =
    let source =
        "context AsyncWorkflow = (await: (int, (int -> int)) -> int)\n" +
        "def async: AsyncWorkflow = (await = { x, f -> f(x) })\n" +
        "def result = async {\n" +
        "  return! 1\n" +
        "}"
    let result = Compiler.compile source
    Assert.False(result.Diagnostics.IsEmpty)
    let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
    Assert.Contains("return", message)

[<Fact>]
let ``A proc block is a result of its return value or of the first error`` () =
    // Each `try` instantiates `proc` afresh, so one block can bind payloads of different types.
    let source =
        "import nanyx/result\n" +
        "def f: int -> #some(int) | #error(#tooBig) = { n -> if n > 5 then #error(#tooBig) else #some(n) }\n" +
        "def g: int -> #some(string) | #error(#tooBig) = { n -> #some(\"{n}\") }\n" +
        "def run = { x -> proc {\n" +
        "  def a = try f(x)\n" +
        "  try f(a)\n" +
        "  def s = try g(a)\n" +
        "  return s\n" +
        "} }"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Assert.Equal("int -> #some(string) | #error(#tooBig)", Unifier.tyToString result.Typed.Value.Types.["run"])

[<Fact>]
let ``A proc block collects the failures of each try`` () =
    let source =
        "import nanyx/result\n" +
        "def name: int -> #some(string) | #nil = { n -> if n > 5 then #nil else #some(\"n\") }\n" +
        "def find: string -> #some(int) | #notFound = { s -> if s == \"n\" then #some(1) else #notFound }\n" +
        "def run = { x -> proc {\n" +
        "  def s = try name(x)\n" +
        "  def i = try find(s)\n" +
        "  return i\n" +
        "} }"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Assert.Equal("int -> #some(int) | #notFound | #nil", Unifier.tyToString result.Typed.Value.Types.["run"])
    // Both failures are in the block's type, so a match on it must handle both.
    let partial =
        source + "\n" +
        "def shown = match run(1)\n" +
        "  | #some(_) -> \"some\"\n" +
        "  | #nil -> \"nil\""
    let partialResult = Compiler.compile partial
    Assert.False(partialResult.Diagnostics.IsEmpty)
    Assert.Contains("notFound", (List.head partialResult.Diagnostics).Message)

[<Fact>]
let ``The prelude is in scope with no imports`` () =
    let source =
        "def f: int -> Result(int, string) = { n -> if n > 5 then #error(\"big\") else #some(n) }\n" +
        "def run = { x -> proc {\n" +
        "  def a = try f(x)\n" +
        "  return a + 1\n" +
        "} }\n" +
        "def name: #some(string) | #empty = #some(\"abc\")\n" +
        "def called = Option.map(name, { s -> 1 })\n" +
        "def piped = name \\ Option.map({ s -> 1 })\n" +
        "def mapped = f(1) \\ Result.mapError({ e -> 0 })"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    let typeOf name = Unifier.tyToString result.Typed.Value.Types.[name]
    Assert.Equal("#some(int) | #empty", typeOf "called")
    Assert.Equal("#some(int) | #empty", typeOf "piped")
    Assert.Equal("Result(int, int)", typeOf "mapped")

[<Fact>]
let ``A pipe finds the prelude's Result helpers by the Result alias`` () =
    let source =
        "def r: Result(int, string) = #error(\"x\")\n" +
        "def mapped = r \\ mapError({ e -> 0 })\n" +
        "def fallback = r \\ defaultValue(3)"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Assert.Equal("Result(int, int)", Unifier.tyToString result.Typed.Value.Types.["mapped"])
    Assert.Equal(TyPrimitive "int", result.Typed.Value.Types.["fallback"])

[<Fact>]
let ``Int.parse is in the prelude`` () =
    let result = Compiler.compile "def n = Int.parse(\"42\")\ndef piped = \"7\" \\ Int.parse"
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Assert.Equal("#some(int) | #invalidInt", Unifier.tyToString result.Typed.Value.Types.["n"])
    Assert.Equal("#some(int) | #invalidInt", Unifier.tyToString result.Typed.Value.Types.["piped"])

[<Fact>]
let ``The prelude's List functions are found by a pipe on a list`` () =
    let source =
        "def xs = [3, 1, 2]\n" +
        "def doubled = xs \\ map({ x -> x * 2 })\n" +
        "def names = xs \\ map({ x -> \"n\" })\n" +
        "def big = xs \\ filter({ x -> x > 1 })\n" +
        "def first = xs \\ head\n" +
        "def total = List.fold(xs, 0, { acc, x -> acc + x })\n" +
        "def pairs = List.zip(xs, names)"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    let typeOf name = Unifier.tyToString result.Typed.Value.Types.[name]
    Assert.Equal("list(int)", typeOf "doubled")
    Assert.Equal("list(string)", typeOf "names")
    Assert.Equal("list(int)", typeOf "big")
    Assert.Equal("#some(int) | #emptyList", typeOf "first")
    Assert.Equal("int", typeOf "total")
    Assert.Equal("list((int, string))", typeOf "pairs")

[<Fact>]
let ``nanyx/math exports int functions with their types`` () =
    let result =
        Compiler.compile (
            "import nanyx/math as math\n" +
            "def a = math.abs(-3)\n" +
            "def b = math.min(1, 2)\n" +
            "def c = math.max(1, 2)\n" +
            "def d = math.clamp(5, 0, 3)\n" +
            "def e = math.pow(2, 3)")
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    for name in [ "a"; "b"; "c"; "d"; "e" ] do
        Assert.Equal(TyPrimitive "int", result.Typed.Value.Types.[name])

[<Fact>]
let ``A module's own definition wins over the prelude's`` () =
    let result = Compiler.compile "def proc = 5\ndef x = proc + 1"
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Assert.Equal(TyPrimitive "int", result.Typed.Value.Types.["x"])

[<Fact>]
let ``An import's definition wins over the prelude's without being ambiguous`` () =
    let modules = [ "mod.nyx", "module mod\nexport def proc = 5" ]
    let result = compileImportingAll modules "module main\nimport \"mod.nyx\"\ndef x = proc + 1"
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Assert.Equal(TyPrimitive "int", result.Typed.Value.Types.["x"])

[<Fact>]
let ``The prelude is not listed among the standard-library modules to import`` () =
    let result = Compiler.compile "import nanyx/nope"
    let message = (List.head result.Diagnostics).Message
    Assert.Contains("nanyx/text", message)
    Assert.DoesNotContain("nanyx/prelude", message)

[<Fact>]
let ``A try nested in an expression of a proc block is bound before its statement`` () =
    let source =
        "import nanyx/result\n" +
        "def f: int -> #some(int) | #error(#tooBig) = { n -> if n > 5 then #error(#tooBig) else #some(n) }\n" +
        "def run = { x -> proc {\n" +
        "  return (a = try f(x), b = try f(try f(x)))\n" +
        "} }"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Assert.Equal("int -> #some(a: int, b: int) | #error(#tooBig)", Unifier.tyToString result.Typed.Value.Types.["run"])
    // The binds are lifted left to right, inner ones first: `f(x)` for `a`, then the inner `f(x)`, then `f` of it.
    match parseModule source with
    | Result.Error err -> Assert.True(false, $"Parse error: {err}")
    | Result.Ok ast ->
        let rec boundCalls (expr: Expression) =
            match expr.Kind with
            | Block statements ->
                statements
                |> List.collect (function
                    | Kind (DefStatement(_, _, _, value))
                    | Kind (ExprStatement value) -> boundCalls value
                    | _ -> [])
            | FunctionCall(_, _, [ Kind (FunctionCall("f", _, [ argument ])); Kind (Lambda(_, rest)) ]) ->
                argument :: boundCalls rest
            | Lambda(_, body) -> boundCalls body
            | _ -> []
        match Compiler.desugar ast |> List.tryPick (function Kind (Def (ValueDef(_, "run", _, body))) -> Some body | _ -> None) with
        | Some body ->
            let arguments = boundCalls body |> List.map (fun argument -> argument.Kind)
            match arguments with
            | [ IdentifierExpr "x"; IdentifierExpr "x"; IdentifierExpr inner ] when inner <> "x" -> ()
            | other -> Assert.True(false, $"Unexpected binds: %A{other}")
        | None -> Assert.True(false, "run not found")

[<Fact>]
let ``Typecheck supports nominal types`` () =
    let source =
        "type $Email = string\n" +
        "def email: $Email = \"a@b.com\"\n" +
        "def addr: string = email\n" +
        "def email2: $Email = addr"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)
    let typed = result.Typed.Value
    match typed.Types.["email"] with
    | TyNominal(name, underlying, false) ->
        Assert.Equal("$Email", name)
        Assert.Equal(TyPrimitive "string", underlying)
    | other -> Assert.True(false, $"Unexpected nominal type: %A{other}")
    Assert.Equal(TyPrimitive "string", typed.Types.["addr"])
    match typed.Types.["email2"] with
    | TyNominal(name, _, false) -> Assert.Equal("$Email", name)
    | other -> Assert.True(false, $"Unexpected nominal type: %A{other}")

[<Fact>]
let ``Typecheck rejects distinct nominal types`` () =
    let source =
        "type $Email = string\n" +
        "type $UserId = string\n" +
        "def email: $Email = \"a@b.com\"\n" +
        "def user: $UserId = email"
    let result = Compiler.compile source
    Assert.False(result.Diagnostics.IsEmpty)
    let message = result.Diagnostics |> List.head |> fun diag -> diag.Message
    Assert.Contains("Type mismatch", message)

[<Fact>]
let ``Typecheck allows local private nominal construction`` () =
    let source =
        "type $Token = private string\n" +
        "def token: $Token = \"secret\""
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)
    let typed = result.Typed.Value
    match typed.Types.["token"] with
    | TyNominal(name, underlying, true) ->
        Assert.Equal("$Token", name)
        Assert.Equal(TyPrimitive "string", underlying)
    | other -> Assert.True(false, $"Unexpected token type: %A{other}")

[<Fact>]
let ``Typecheck match arms agree`` () =
    let source =
        "def result = match 1\n" +
        "  | 1 -> \"one\"\n" +
        "  | _ -> \"other\""
    let result = Compiler.compile source
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "string", typed.Types.["result"])

[<Fact>]
let ``Typecheck populates typed AST items`` () =
    let source = "def message = \"Hello\""
    let result = Compiler.compile source
    let typed = result.Typed.Value
    let hasMessage =
        typed.Items
        |> List.exists (function
            | TypedDef (TypedValueDef(_, name, _, typedExpr)) ->
                name = "message" && typedExpr.Type = TyPrimitive "string"
            | _ -> false)
    Assert.True(hasMessage)

[<Fact>]
let ``Typecheck captures typed block statements in lambda`` () =
    let source =
        "def f = { x ->\n" +
        "  def y = x\n" +
        "  y\n" +
        "}"
    let result = Compiler.compile source
    let typed = result.Typed.Value
    let hasBlockStatements =
        typed.Items
        |> List.exists (function
            | TypedDef (TypedValueDef(_, name, _, typedExpr)) when name = "f" ->
                match typedExpr.Body with
                | Some bodyExpr ->
                    match bodyExpr.Statements with
                    | Some statements ->
                        statements
                        |> List.exists (function
                            | TypedDefStatement(_, defName, _, _) -> defName = "y"
                            | _ -> false)
                    | None -> false
                | None -> false
            | _ -> false)
    Assert.True(hasBlockStatements)

[<Fact>]
let ``Typecheck supports match shorthand lambda`` () =
    let source =
        "def f: string -> int = {\n" +
        "  | \"one\" -> 1\n" +
        "  | \"two\" -> 2\n" +
        "  | _ -> 0\n" +
        "}\n" +
        "def result = f(\"one\")"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty)
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "int", typed.Types.["result"])

[<Fact>]
let ``Typecheck records typed match patterns`` () =
    let source =
        "def result = match 1\n" +
        "  | 1 -> \"one\"\n" +
        "  | _ -> \"other\""
    let result = Compiler.compile source
    let typed = result.Typed.Value
    let matchHasPattern =
        typed.Items
        |> List.exists (function
            | TypedDef (TypedValueDef(_, name, _, typedExpr)) when name = "result" ->
                match typedExpr.MatchArms with
                | Some arms ->
                    arms
                    |> List.exists (fun (patterns, _) ->
                        patterns
                        |> List.exists (fun pattern ->
                            match pattern.Pattern.Kind with
                            | LiteralPattern (IntLit 1) -> pattern.Type = TyPrimitive "int"
                            | _ -> false))
                | None -> false
            | _ -> false)
    Assert.True(matchHasPattern)

[<Fact>]
let ``Typecheck types list and guard patterns`` () =
    let source =
        "def result = match [1, 2]\n" +
        "  | [x, y] -> x\n" +
        "  | _ -> 0"
    let result = Compiler.compile source
    let typed = result.Typed.Value
    let hasListPattern =
        typed.Items
        |> List.exists (function
            | TypedDef (TypedValueDef(_, name, _, typedExpr)) when name = "result" ->
                match typedExpr.MatchArms with
                | Some arms ->
                    arms
                    |> List.exists (fun (patterns, _) ->
                        patterns
                        |> List.exists (fun pattern ->
                            match pattern.Pattern.Kind with
                            | ListPattern _ -> true
                            | _ -> false))
                | None -> false
            | _ -> false)
    Assert.True(hasListPattern)

[<Fact>]
let ``Typecheck joins the tags of if branches into a union`` () =
    let source =
        "def getMessage = {\n" +
        "  def message = \"Hello\"\n" +
        "  if false then #some(message)\n" +
        "  else #noMessage\n" +
        "}"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let typed = result.Typed.Value
    let expected = TyFunc(TyPrimitive "unit", TyUnion [ TyTag("some", Some (TyPrimitive "string")); TyTag("noMessage", None) ])
    Assert.Equal(expected, typed.Types.["getMessage"])

[<Fact>]
let ``Typecheck joins the tags of match arms into a union`` () =
    let source =
        "def classify = { n ->\n" +
        "  match n\n" +
        "    | 0 -> #zero\n" +
        "    | 1 -> #one(n)\n" +
        "    | _ -> #many(n)\n" +
        "}"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let typed = result.Typed.Value
    let expected =
        TyFunc(TyPrimitive "int", TyUnion [ TyTag("zero", None); TyTag("one", Some (TyPrimitive "int")); TyTag("many", Some (TyPrimitive "int")) ])
    Assert.Equal(expected, typed.Types.["classify"])

[<Fact>]
let ``Typecheck joins nested if branches and repeated tags once`` () =
    let source =
        "def pick = { n ->\n" +
        "  if n == 0 then #zero\n" +
        "  else if n == 1 then #small(n)\n" +
        "  else if n == 2 then #small(n)\n" +
        "  else #big\n" +
        "}"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let typed = result.Typed.Value
    let expected = TyFunc(TyPrimitive "int", TyUnion [ TyTag("zero", None); TyTag("small", Some (TyPrimitive "int")); TyTag("big", None) ])
    Assert.Equal(expected, typed.Types.["pick"])

[<Fact>]
let ``Typecheck joins the tags inside tuple branches field by field`` () =
    let source =
        "def splitOnce: (string, string) -> (string, #some(string) | #none) = { s, separator ->\n" +
        "  match s == separator\n" +
        "    | true -> (\"\", #some(s))\n" +
        "    | false -> (s, #none)\n" +
        "}"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

[<Fact>]
let ``Typecheck still rejects tuple branches whose fields disagree`` () =
    let source = "def f = { c -> if c then (1, #a) else (\"one\", #b) }"
    let result = Compiler.compile source
    Assert.False(result.Diagnostics.IsEmpty)

[<Fact>]
let ``Typecheck still rejects same-named tags with different payloads across branches`` () =
    let source = "def f = { c -> if c then #some(1) else #some(\"one\") }"
    let result = Compiler.compile source
    Assert.False(result.Diagnostics.IsEmpty)
    Assert.Contains("mismatch", (List.head result.Diagnostics).Message)

[<Fact>]
let ``Typecheck still rejects branches of different non-tag types`` () =
    let source = "def f = { c -> if c then 1 else \"one\" }"
    let result = Compiler.compile source
    Assert.False(result.Diagnostics.IsEmpty)
    Assert.Contains("Type mismatch", (List.head result.Diagnostics).Message)

[<Fact>]
let ``Typecheck matches on an inferred tag union exhaustively`` () =
    // Exhaustiveness is checked against the scrutinee's known type; an inferred union counts as known.
    // (Scrutinees that are call results or local bindings are not checked today, inferred or annotated.)
    let source =
        "def message = if false then #some(\"hi\") else #noMessage\n" +
        "def describe = {\n" +
        "  match message\n" +
        "    | #some(_) -> \"message\"\n" +
        "    | #noMessage -> \"nothing\"\n" +
        "}\n" +
        "def partial = {\n" +
        "  match message\n" +
        "    | #some(_) -> \"message\"\n" +
        "}"
    let result = Compiler.compile source
    Assert.Equal(1, result.Diagnostics.Length)
    Assert.Contains("Non-exhaustive match", (List.head result.Diagnostics).Message)
    Assert.Contains("noMessage", (List.head result.Diagnostics).Message)

[<Fact>]
let ``Typecheck infers list element type`` () =
    let source = "def numbers = [1, 2, 3]"
    let result = Compiler.compile source
    let typed = result.Typed.Value
    Assert.Equal(TyApply("list", [TyPrimitive "int"]), typed.Types.["numbers"])

[<Fact>]
let ``Typecheck infers operator results`` () =
    let source =
        "def sum = 1 + 2\n" +
        "def lt = 1 < 2\n" +
        "def eq = \"a\" == \"b\""
    let result = Compiler.compile source
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "int", typed.Types.["sum"])
    Assert.Equal(TyPrimitive "bool", typed.Types.["lt"])
    Assert.Equal(TyPrimitive "bool", typed.Types.["eq"])

[<Fact>]
let ``Typecheck ++ concatenates strings and lists`` () =
    let source =
        "def greeting = \"Hello, \" ++ \"World\"\n" +
        "def numbers = [1, 2] ++ [3]\n" +
        "def shout: string -> string = { s -> s ++ \"!\" }\n" +
        "def twice: list(int) -> list(int) = { xs -> xs ++ xs }"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let typed = result.Typed.Value
    Assert.Equal(TyPrimitive "string", typed.Types.["greeting"])
    Assert.Equal(TyApply("list", [ TyPrimitive "int" ]), typed.Types.["numbers"])

[<Fact>]
let ``Typecheck rejects ++ between a string and an int`` () =
    let result = Compiler.compile "def bad = \"a\" ++ 1"
    Assert.False(result.Diagnostics.IsEmpty)

[<Fact>]
let ``Typecheck rejects + on strings`` () =
    let result = Compiler.compile "def bad = \"a\" + \"b\""
    Assert.False(result.Diagnostics.IsEmpty)

[<Fact>]
let ``Compile reports diagnostics on parse error`` () =
    let source = "def message = "
    let result = Compiler.compile source
    Assert.Equal(CompilationPhase.Parsed, result.Phase)
    Assert.False(result.Diagnostics.IsEmpty)

[<Fact>]
let ``Unifier binds type variables`` () =
    let tvar = TyVar { Id = 1; Name = None }
    let constraints: ConstraintSet = [ (tvar, TyPrimitive "string", Equal, SourceSpan.none) ]
    let result = Unifier.unify constraints
    match result with
    | Ok subst -> Assert.Equal(Some (TyPrimitive "string"), subst |> Map.tryFind 1)
    | Error (message, _) -> Assert.True(false, message)

[<Fact>]
let ``Unifier rejects occurs check`` () =
    let tvar = TyVar { Id = 2; Name = None }
    let constraintSet: ConstraintSet = [ (tvar, TyFunc(tvar, TyPrimitive "int"), Equal, SourceSpan.none) ]
    let result = Unifier.unify constraintSet
    match result with
    | Ok _ -> Assert.True(false, "Expected occurs check failure")
    | Error (message, _) -> Assert.Contains("Occurs", message)

[<Fact>]
let ``Unifier allows record width subtyping`` () =
    let narrow = TyRecord (Map.ofList [ ("a", TyPrimitive "int") ])
    let wide = TyRecord (Map.ofList [ ("a", TyPrimitive "int"); ("b", TyPrimitive "string") ])
    let result = Unifier.unify [ (narrow, wide, Equal, SourceSpan.none) ]
    match result with
    | Ok _ -> Assert.True(true)
    | Error (message, _) -> Assert.True(false, message)

[<Fact>]
let ``Unifier matches unions by set and width`` () =
    let unionA = TyUnion [ TyTag("some", Some (TyPrimitive "int")); TyTag("notFound", None) ]
    let unionB = TyUnion [ TyTag("notFound", None); TyTag("some", Some (TyPrimitive "int")) ]
    let unionWide = TyUnion [ TyTag("some", Some (TyPrimitive "int")); TyTag("notFound", None); TyTag("error", Some (TyPrimitive "string")) ]
    let resultOrder = Unifier.unify [ (unionA, unionB, Equal, SourceSpan.none) ]
    let resultWidth = Unifier.unify [ (unionA, unionWide, Equal, SourceSpan.none) ]
    match resultOrder with
    | Ok _ -> Assert.True(true)
    | Error (message, _) -> Assert.True(false, message)
    match resultWidth with
    | Ok _ -> Assert.True(true)
    | Error (message, _) -> Assert.True(false, message)

[<Fact>]
let ``Typecheck allows mutual recursion between annotated top-level defs`` () =
    let source =
        "def isEven: int -> bool = { n -> if n == 0 then true else isOdd(n - 1) }\n" +
        "def isOdd: int -> bool = { n -> if n == 0 then false else isEven(n - 1) }\n" +
        "def result = isEven(10)"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    Assert.Equal(TyPrimitive "bool", result.Typed.Value.Types.["result"])

[<Fact>]
let ``Typecheck allows a forward reference to an unannotated def`` () =
    let source =
        "def result = addOne(41)\n" +
        "def addOne = { x -> x + 1 }"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    Assert.Equal(TyPrimitive "int", result.Typed.Value.Types.["result"])
    Assert.Equal(TyFunc(TyPrimitive "int", TyPrimitive "int"), result.Typed.Value.Types.["addOne"])

[<Fact>]
let ``Typecheck lets an annotation name a type declared later`` () =
    let source =
        "def favourite: Color = #green\n" +
        "type Color = #red | #green | #blue"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

[<Fact>]
let ``Typecheck still generalizes a def used after its definition`` () =
    let source =
        "def id = { x -> x }\n" +
        "def a = id(1)\n" +
        "def b = id(\"s\")"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    Assert.Equal(TyPrimitive "int", result.Typed.Value.Types.["a"])
    Assert.Equal(TyPrimitive "string", result.Typed.Value.Types.["b"])
[<Fact>]
let ``Typecheck types the boolean keywords as bool`` () =
    let source =
        "def a = true and false\n" +
        "def b = a or not a\n" +
        "def c = not (1 < 2)"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let types = result.Typed.Value.Types
    Assert.Equal(TyPrimitive "bool", types.["a"])
    Assert.Equal(TyPrimitive "bool", types.["b"])
    Assert.Equal(TyPrimitive "bool", types.["c"])

[<Fact>]
let ``Typecheck gives a record update the record's type and checks the updated fields`` () =
    let ok = Compiler.compile "def alice = (name = \"Alice\", age = 30)\ndef older = alice with age = 31\ndef n: int = older.age"
    Assert.True(ok.Diagnostics.IsEmpty, sprintf "%A" ok.Diagnostics)
    let wrongType = Compiler.compile "def alice = (name = \"Alice\", age = 30)\ndef older = alice with age = \"old\""
    Assert.False(wrongType.Diagnostics.IsEmpty, "updating age with a string should not type-check")
    let unknownField = Compiler.compile "def alice = (name = \"Alice\", age = 30)\ndef older = alice with height = 1"
    Assert.False(unknownField.Diagnostics.IsEmpty, "updating a field the record lacks should not type-check")
[<Fact>]
let ``Typecheck allows member access on the result of a call`` () =
    // #60: the call's result used to be an unsolved variable when the member access looked for fields.
    let declared =
        Compiler.compile (
            "type Point = (x: int, y: int)\n" +
            "def shift: Point -> Point = { q -> Point(x = q.x + 1, y = q.y + 1) }\n" +
            "def a = shift(Point(x = 3, y = 4)).x\n" +
            "def q = shift(Point(x = 3, y = 4))\n" +
            "def b: int = q.y\n")
    Assert.True(declared.Diagnostics.IsEmpty, sprintf "%A" declared.Diagnostics)
    let inferred =
        Compiler.compile (
            "def make = { n -> (value = n, label = \"v\") }\n" +
            "def m = make(1)\n" +
            "def s: string = m.label\n" +
            "def t = (1, \"two\")\n" +
            "def pick = { p -> p }\n" +
            "def u: string = pick(t).1\n")
    Assert.True(inferred.Diagnostics.IsEmpty, sprintf "%A" inferred.Diagnostics)
    let missing = Compiler.compile "type Point = (x: int, y: int)\ndef id: Point -> Point = { p -> p }\ndef z = id(Point(x = 1, y = 2)).z"
    Assert.False(missing.Diagnostics.IsEmpty, "a field the result lacks should still be an error")
[<Fact>]
let ``Typecheck leaves the scrutinee union open when a match has a catch-all arm`` () =
    // `| #some(v) -> ... | _ -> ...` names only one tag; the scrutinee may still be a wider union.
    let result =
        Compiler.compile (
            "def get: int -> #some(int) | #none = { n -> #some(n) }\n" +
            "def d = { n -> match get(n)\n" +
            "  | #some(v) -> v\n" +
            "  | _ -> 0\n" +
            "}\n" +
            "def e = if get(1) is #some(v) then v else 0\n")
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
[<Fact>]
let ``Typecheck accepts arithmetic and comparison on floats`` () =
    // #36: `+ - * /` and `< > <= >=` used to pin their operands to int.
    let ok =
        Compiler.compile (
            "def pi = 3.14\n" +
            "def d = pi * 2.0\n" +
            "def e: float = 1.5 + 2.5\n" +
            "def half = { x -> x / 2.0 }\n" +
            "def big = 2.5 > 1.0\n" +
            "def sum: (float, float) -> float = { a, b -> a + b }\n")
    Assert.True(ok.Diagnostics.IsEmpty, sprintf "%A" ok.Diagnostics)
    let mixed = Compiler.compile "def m = 1 + 2.0"
    Assert.False(mixed.Diagnostics.IsEmpty, "int + float should not type-check")
    let stillInt = Compiler.compile "def n: float = 1 + 2"
    Assert.False(stillInt.Diagnostics.IsEmpty, "int arithmetic is still int")
[<Fact>]
let ``Typecheck types for loops and ranges`` () =
    let ok =
        Compiler.compile (
            "def r = 1..3\n" +
            "def total = { xs -> for xs | x -> x + 1 }\n" +
            "def f = for 1..3 | i -> i * 2\n" +
            "def g: () = for [\"a\"] | s -> s ++ \"!\"\n")
    Assert.True(ok.Diagnostics.IsEmpty, sprintf "%A" ok.Diagnostics)
    let wrong = Compiler.compile "def h = for 1..3 | i -> i ++ \"x\""
    Assert.False(wrong.Diagnostics.IsEmpty, "a range element is an int")
    let badRange = Compiler.compile "def h = \"a\"..\"z\""
    Assert.False(badRange.Diagnostics.IsEmpty, "ranges are over ints")

[<Fact>]
let ``Typecheck rejects boolean keywords on non-booleans`` () =
    let andResult = Compiler.compile "def a = 1 and 2"
    Assert.False(andResult.Diagnostics.IsEmpty, "`1 and 2` should not type-check")
    let notResult = Compiler.compile "def b = not \"text\""
    Assert.False(notResult.Diagnostics.IsEmpty, "`not \"text\"` should not type-check")

// Hover: the type of the expression at a position.

/// The offset of the `occurrence`th (zero-based) appearance of `text` in `source`, plus `within` characters.
let private offsetOf (source: string) (text: string) (occurrence: int) (within: int) =
    let rec find from remaining =
        let index = source.IndexOf(text, from, StringComparison.Ordinal)
        if index < 0 then failwith $"'{text}' not found"
        elif remaining = 0 then index
        else find (index + 1) (remaining - 1)
    find 0 occurrence + within

let private hoverWith (names: TypeVarNames) (source: string) (offset: int) =
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Hover.typeAt names result offset

let private hoverAt source offset = hoverWith Greek source offset

let private hoverType source offset =
    hoverAt source offset |> Option.map (fun info -> info.Type)

let private latinHoverType source offset =
    hoverWith Latin source offset |> Option.map (fun info -> info.Type)

[<Fact>]
let ``Reading fields of a parameter makes it a record with at least those fields`` () =
    let source =
        "def name = { p -> p.name }\n" +
        "def label = { p -> p.name ++ \" \" ++ p.city }\n"
    Assert.Equal(Some "(name: α, ...ω) -> α", hoverType source (offsetOf source "{ p -> p.name }" 0 0))
    Assert.Equal(Some "(city: string, name: string, ...ω) -> string", hoverType source (offsetOf source "{ p -> p.name ++" 0 0))

[<Fact>]
let ``A function reading fields takes any record that has them`` () =
    let source =
        "def label = { p -> p.name ++ p.city }\n" +
        "def show = {\n" +
        "  | #some(p), n -> p.name ++ n\n" +
        "  | _, _ -> \"none\"\n" +
        "}\n" +
        "def a = label((name = \"a\", city = \"b\"))\n" +
        "def b = label((city = \"b\", name = \"a\", age = 3))\n" +
        "def c = show(#some((name = \"pat\", age = 3)), \"!\")\n"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Assert.Equal(Some "string", hoverType source (offsetOf source "a = " 0 0))
    Assert.Equal(Some "string", hoverType source (offsetOf source "c = " 0 0))

[<Fact>]
let ``A function reading a field rejects a record without it`` () =
    let result = Compiler.compile "def name = { p -> p.name }\ndef a = name((age = 3))\n"
    Assert.False(result.Diagnostics.IsEmpty)
    Assert.Contains("Record field mismatch: {name} vs {age}", (result.Diagnostics |> List.head).Message)

[<Fact>]
let ``Expressions at a position are innermost first`` () =
    let source = "def value = 1 + 2 * 3"
    let syntax =
        match Parser.Program.parseModule source with
        | Ok syntax -> syntax
        | Error message -> failwith message
    let found = Hover.expressionsAt (offsetOf source "2" 0 0) syntax
    match found |> List.map (fun expr -> expr.Kind) with
    | [ LiteralExpr (IntLit 2); BinaryOp("*", _, _); BinaryOp("+", _, _) ] -> ()
    | kinds -> failwithf "Unexpected expressions: %A" kinds
    // Each one encloses the one before it.
    found
    |> List.pairwise
    |> List.iter (fun (inner, outer) ->
        Assert.True(outer.Span.Start <= inner.Span.Start && inner.Span.End <= outer.Span.End))

[<Fact>]
let ``Nothing is found outside every expression`` () =
    let source = "def value = 1 + 2"
    Assert.Equal(None, hoverType source 0)

[<Fact>]
let ``Hover shows the type of literals, locals, parameters, calls and lambdas`` () =
    let source =
        "def double = { n -> n * 2 }\n" +
        "def answer = {\n" +
        "  def half = 21\n" +
        "  double(half)\n" +
        "}\n"
    // The innermost expression wins: the literal, not the multiplication around it.
    Assert.Equal(Some "int", hoverType source (offsetOf source "2" 0 0))
    Assert.Equal(Some "int", hoverType source (offsetOf source "n *" 0 0))
    Assert.Equal(Some "int -> int", hoverType source (offsetOf source "{ n" 0 0))
    Assert.Equal(Some "int", hoverType source (offsetOf source "half)" 0 0))
    Assert.Equal(Some "int", hoverType source (offsetOf source "(half)" 0 0))
    // On the call's function name, the function's type.
    Assert.Equal(Some "int -> int", hoverType source (offsetOf source "double(" 0 1))

[<Fact>]
let ``Hover on a declaration's name shows the type of what it declares`` () =
    let source =
        "def id = { x -> x }\n" +
        "def answer = {\n" +
        "  def half = 21\n" +
        "  half * 2\n" +
        "}\n" +
        "def describe = { o ->\n" +
        "  match o\n" +
        "  | #some(n) -> n + 1\n" +
        "  | _ -> 0\n" +
        "}\n"
    // Top-level defs, generalised.
    Assert.Equal(Some "α -> α", hoverType source (offsetOf source "id" 0 1))
    Assert.Equal(Some "() -> int", hoverType source (offsetOf source "answer" 0 0))
    // A parameter, a local def and a pattern variable.
    Assert.Equal(Some "α", hoverType source (offsetOf source "x ->" 0 0))
    Assert.Equal(Some "int", hoverType source (offsetOf source "half" 0 2))
    Assert.Equal(Some "int", hoverType source (offsetOf source "(n)" 0 1))
    Assert.Equal(Some "#some(int) | ω -> int", hoverType source (offsetOf source "describe" 0 0))
    // The range is the declared name.
    let info = (hoverAt source (offsetOf source "half" 0 2)).Value
    Assert.Equal(offsetOf source "half" 0 0, info.Span.Start)
    Assert.Equal(offsetOf source "half" 0 4, info.Span.End)

[<Fact>]
let ``Hover range is the span of the expression`` () =
    let source = "def value = 1 + 23"
    let info = (hoverAt source (offsetOf source "23" 0 1)).Value
    Assert.Equal("int", info.Type)
    Assert.Equal(offsetOf source "23" 0 0, info.Span.Start)
    Assert.Equal(offsetOf source "23" 0 2, info.Span.End)

[<Fact>]
let ``Hover on a polymorphic function shows it instantiated at the call`` () =
    let source =
        "def id = { x -> x }\n" +
        "def n = id(5)\n" +
        "def s = id(\"a\")\n"
    Assert.Equal(Some "α -> α", hoverType source (offsetOf source "{ x" 0 0))
    Assert.Equal(Some "α", hoverType source (offsetOf source "-> x" 0 3))
    Assert.Equal(Some "int -> int", hoverType source (offsetOf source "id(" 0 0))
    Assert.Equal(Some "string -> string", hoverType source (offsetOf source "id(" 1 0))
    Assert.Equal(Some "string", hoverType source (offsetOf source "id(" 1 4))

[<Fact>]
let ``Type variables are shown as α, β in order of appearance`` () =
    let source = "def pair = { a, b -> (b, a) }"
    Assert.Equal(Some "α, β -> (β, α)", hoverType source (offsetOf source "{" 0 0))

[<Fact>]
let ``Type variables are shown as a, b when Latin names are asked for`` () =
    let source = "def pair = { a, b -> (b, a) }"
    Assert.Equal(Some "a, b -> (b, a)", latinHoverType source (offsetOf source "{" 0 0))

[<Fact>]
let ``Greek type variable names go through the alphabet, then start again with a subscript`` () =
    let vars = [ for id in 1..26 -> TyVar { Id = id; Name = None } ]
    let shown = Unifier.tyToDisplayString Greek (TyApply("f", vars))
    Assert.Equal("f(α, β, γ, δ, ε, ζ, η, θ, ι, κ, λ, μ, ν, ξ, ο, π, ρ, σ, τ, υ, φ, χ, ψ, ω, α₁, β₁)", shown)

[<Fact>]
let ``A union's rest variable is named from the end of the alphabet`` () =
    // The catch-all arm leaves the union open for the tags the arms do not name.
    let source =
        "def mapOption = { o, f ->\n" +
        "  match o\n" +
        "  | #some(a) -> #some(f(a))\n" +
        "  | other -> other\n" +
        "}\n"
    Assert.Equal(Some "#some(α) | ω, (α -> β) -> #some(β) | ω", hoverType source (offsetOf source "{" 0 0))
    Assert.Equal(Some "#some(a) | z, (a -> b) -> #some(b) | z", latinHoverType source (offsetOf source "{" 0 0))

[<Fact>]
let ``A def's type is resolved before it is generalised, so a use of it has the inferred type`` () =
    let source = "def increment = { x -> x + 1 }\ndef f = increment\n"
    Assert.Equal(Some "int -> int", hoverType source (offsetOf source "increment" 1 0))

[<Fact>]
let ``A shorthand match lambda shows its own type, not that of the parameters it makes up`` () =
    let source =
        "def mapOption = { | #some(a), f -> #some(f(a)) | other, _ -> other }\n" +
        "def g = mapOption\n"
    Assert.Equal(Some "#some(α) | ω, (α -> β) -> #some(β) | ω", hoverType source (offsetOf source "{" 0 0))
    Assert.Equal(Some "#some(α) | ω, (α -> β) -> #some(β) | ω", hoverType source (offsetOf source "mapOption" 1 0))

[<Fact>]
let ``A rest variable nothing can add tags to is empty`` () =
    // `#some(5)` has no other tags, so neither has what `mapOption` gives back.
    let source =
        "def mapOption = { | #some(a), f -> #some(f(a)) | other, _ -> other }\n" +
        "def main = {\n" +
        "  def a = #some(5) \\mapOption { n -> \"{n}\" }\n" +
        "  a\n" +
        "}\n"
    Assert.Equal(Some "#some(string)", hoverType source (offsetOf source "a\n}" 0 0))
    Assert.Equal(Some "#some(int), (int -> string) -> #some(string)", hoverType source (offsetOf source "mapOption {" 0 0))
    // A rest variable that the input still shares stays: what the function is given more of, it gives back.
    Assert.Equal(Some "#some(α) | ω, (α -> β) -> #some(β) | ω", hoverType source (offsetOf source "{" 0 0))
    // So does one shared with a parameter in scope, which a later use could still give tags to.
    let inLambda =
        "def mapOption = { | #some(a), f -> #some(f(a)) | other, _ -> other }\n" +
        "def g = { o ->\n" +
        "  def a = o \\mapOption { n -> n }\n" +
        "  a\n" +
        "}\n"
    Assert.Equal(Some "#some(α) | ω -> #some(α) | ω", hoverType inLambda (offsetOf inLambda "{ o" 0 0))

[<Fact>]
let ``A rest variable a closed union widened into is empty`` () =
    // `getCustomer` gives out three tags and no more, so `mapOption` gives back those three and no more.
    let source =
        "def mapOption = { | #some(a), f -> #some(f(a))\n" +
        "                  | other, _ -> other }\n" +
        "type Customer = (name: string)\n" +
        "def getCustomer = { name ->\n" +
        "  if true then #some(Customer(name = name))\n" +
        "  else if false then #none\n" +
        "  else #err\n" +
        "}\n" +
        "def main: () -> () = {\n" +
        "  def a = getCustomer(\"foo\") \\mapOption { c: Customer -> \"Mapped: {c.name}\" }\n" +
        "  dbg(a)\n" +
        "  ()\n" +
        "}\n"
    Assert.Equal(Some "#some(string) | #err | #none", hoverType source (offsetOf source "a =" 0 0))

[<Fact>]
let ``A record type is written in brackets, as in source`` () =
    let fields = [ "name", TyPrimitive "string"; "age", TyPrimitive "int" ] |> Map.ofList
    Assert.Equal("(age: int, name: string)", Unifier.tyToString (TyRecord fields))
    Assert.Equal("#some(name: string)",Unifier.tyToString (TyTag("some", Some (TyRecord (Map.ofList [ "name", TyPrimitive "string" ])))))

[<Fact>]
let ``The empty union is the bottom type`` () =
    Assert.Equal("undefined", Unifier.tyToString (TyUnion []))

[<Fact>]
let ``Where the two ends of the alphabet meet, each goes round its own part again`` () =
    let var id = TyVar { Id = id; Name = None }
    let open' id = TyUnion [ TyTag("x", None); var id ]
    // 20 variables and 4 rests take the whole alphabet the first time, so the front keeps α to υ and the back
    // φ to ω; the rests and variables after that go round their own parts again.
    let ty = TyApply("f", [ for id in 1..20 -> var id ] @ [ for id in 21..26 -> open' id ] @ [ var 27; var 28 ])
    Assert.Equal(
        "f(α, β, γ, δ, ε, ζ, η, θ, ι, κ, λ, μ, ν, ξ, ο, π, ρ, σ, τ, υ, "
        + "#x | ω, #x | ψ, #x | χ, #x | φ, #x | ω₁, #x | ψ₁, α₁, β₁)",
        Unifier.tyToDisplayString Greek ty)

[<Fact>]
let ``Hover inside a for loop's arm shows the arm's expression, not the loop element`` () =
    let source = "def f = for [\"a\"] | s -> s ++ \"!\""
    let info = (hoverAt source (offsetOf source "\"!\"" 0 1)).Value
    Assert.Equal("string", info.Type)
    Assert.Equal(offsetOf source "\"!\"" 0 0, info.Span.Start)
    Assert.Equal(Some "int", hoverType "def g = for 1..3 | i -> i * 2" (offsetOf "def g = for 1..3 | i -> i * 2" "2" 0 0))

[<Fact>]
let ``Hover inside a match arm shows the innermost expression`` () =
    let source =
        "def describe = { n ->\n" +
        "  match n\n" +
        "  | 0 -> \"zero\"\n" +
        "  | _ -> \"many\"\n" +
        "}\n"
    let info = (hoverAt source (offsetOf source "\"many\"" 0 2)).Value
    Assert.Equal("string", info.Type)
    Assert.Equal(offsetOf source "\"many\"" 0 0, info.Span.Start)

[<Fact>]
let ``A workflow bind is not given the type of the rest of the workflow`` () =
    let source =
        "context AsyncWorkflow = (await: (string, (string -> int)) -> int return: int -> int)\n" +
        "def async: AsyncWorkflow = (await = { x, f -> f(x) }, return = { x -> x })\n" +
        "def result = async {\n" +
        "  def x = await! \"a\"\n" +
        "  return! 1\n" +
        "}"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    let bind =
        Hover.expressionsAt (offsetOf source "await!" 0 0) result.Syntax.Value
        |> List.find (fun expr -> match expr.Kind with WorkflowBindExpr _ -> true | _ -> false)
    Assert.False(result.Typed.Value.ExprTypes.ContainsKey bind.Id)
    Assert.Equal(Some "string", hoverType source (offsetOf source "\"a\"" 0 1))

[<Fact>]
let ``Type variables named in an annotation keep their names, written as in the annotation`` () =
    let source = "def first: (t, a) -> t = { x, y -> x }"
    Assert.Equal(Some "t, a -> t", hoverType source (offsetOf source "{" 0 0))
    let mixed =
        "def f: a -> int = { x ->\n" +
        "  def g = { y -> (y, x) }\n" +
        "  1\n" +
        "}\n"
    Assert.Equal(Some "α -> (α, a)", hoverType mixed (offsetOf mixed "{ y" 0 0))
    // A made-up Latin name skips the names an annotation took.
    Assert.Equal(Some "b -> (b, a)", latinHoverType mixed (offsetOf mixed "{ y" 0 0))

[<Fact>]
let ``Hover shows tags with their hash`` () =
    let source = "def found = #some(1)\ndef missing = #notFound"
    Assert.Equal(Some "#some(int)", hoverType source (offsetOf source "#some" 0 1))
    Assert.Equal(Some "#notFound", hoverType source (offsetOf source "#notFound" 0 1))
    let union =
        "def mapOption: (#some(a) | r), (a -> b) -> (#some(b) | r) = { | #some(a), f -> #some(f(a)) | other, _ -> other }\n" +
        "def result = mapOption(#some(1), { n -> \"Mapped: {n}\" })"
    Assert.Equal(
        Some "#some(int), (int -> string) -> #some(string)",
        hoverType union (offsetOf union "mapOption(" 0 0))

[<Fact>]
let ``Printed types have the fewest brackets the grammar allows`` () =
    let a = TyVar { Id = 1; Name = Some "a" }
    let b = TyVar { Id = 2; Name = Some "b" }
    let c = TyVar { Id = 3; Name = Some "c" }
    let union = TyUnion [ TyTag("some", Some a); TyTag("none", None) ]
    let pair x y = TyRecord(Map.ofList [ "0", x; "1", y ])
    // `->` groups to the right: a function input is bracketed, a function result is not.
    Assert.Equal("(a -> b) -> c", Unifier.tyToString (TyFunc(TyFunc(a, b), c)))
    Assert.Equal("a -> b -> c", Unifier.tyToString (TyFunc(a, TyFunc(b, c))))
    // `|` binds tighter than `->`: a union is never bracketed next to an arrow.
    Assert.Equal("a -> #some(a) | #none", Unifier.tyToString (TyFunc(a, union)))
    Assert.Equal("#some(a) | #none -> a", Unifier.tyToString (TyFunc(union, a)))
    // A signature's parameters are bare; a function among them, or anywhere items are separated by commas,
    // has brackets of its own.
    Assert.Equal("int, (a -> b) -> c", Unifier.tyToString (TyFunc(pair (TyPrimitive "int") (TyFunc(a, b)), c)))
    Assert.Equal("(int, (a -> b))", Unifier.tyToString (pair (TyPrimitive "int") (TyFunc(a, b))))
    Assert.Equal("map(string, (a -> b))", Unifier.tyToString (TyApply("map", [ TyPrimitive "string"; TyFunc(a, b) ])))
    // Only the signature itself writes parameters bare: a function nested in it, as its input or its result,
    // keeps its brackets, since a bare list can only come before the arrow.
    Assert.Equal("((a, b) -> c) -> (a, b) -> c", Unifier.tyToString (TyFunc(TyFunc(pair a b, c), TyFunc(pair a b, c))))
    Assert.Equal("a -> (a, b) -> c", Unifier.tyToString (TyFunc(a, TyFunc(pair a b, c))))
    // A lone argument or payload needs no brackets, and a tuple payload's items are the payload's.
    Assert.Equal("list(a -> b)", Unifier.tyToString (TyApply("list", [ TyFunc(a, b) ])))
    Assert.Equal("#some(int)", Unifier.tyToString (TyTag("some", Some (TyPrimitive "int"))))
    Assert.Equal("#pair(int, (a -> b))", Unifier.tyToString (TyTag("pair", Some (pair (TyPrimitive "int") (TyFunc(a, b))))))
    Assert.Equal("#apply((int, a) -> b)",Unifier.tyToString (TyTag("apply", Some (TyFunc(pair (TyPrimitive "int") a, b)))))
    // The unit type is written `()`.
    Assert.Equal("() -> ()", Unifier.tyToString (TyFunc(TyPrimitive "unit", TyPrimitive "unit")))

// Symbols and references: what each name refers to.

/// The symbol that `table` records for the innermost expression at `offset` that it has an entry for.
let private symbolAt (table: TypedModule -> Map<NodeId, SymbolId>) (result: CompileResult) (offset: int) =
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    let typed = result.Typed.Value
    Hover.expressionsAt offset result.Syntax.Value
    |> List.tryPick (fun expr -> table typed |> Map.tryFind expr.Id)
    |> Option.map (fun id -> typed.Symbols.[id])

/// The kind of what the name at `offset` refers to, and where that is declared.
let private resolvesTo (source: string) (offset: int) =
    symbolAt (fun typed -> typed.References) (Compiler.compile source) offset
    |> Option.map (fun symbol -> symbol.Kind, symbol.Declaration.Start)

[<Fact>]
let ``Names refer to parameters and locals`` () =
    let source =
        "def scale = { factor ->\n" +
        "  def offset = 1\n" +
        "  factor * 2 + offset\n" +
        "}\n"
    Assert.Equal(Some (Parameter, offsetOf source "factor" 0 0), resolvesTo source (offsetOf source "factor *" 0 0))
    Assert.Equal(Some (LocalValue, offsetOf source "offset" 0 0), resolvesTo source (offsetOf source "offset" 1 0))
    // A declaration spans its name.
    let symbol = (symbolAt (fun typed -> typed.References) (Compiler.compile source) (offsetOf source "offset" 1 0)).Value
    Assert.Equal(offsetOf source "offset" 0 6, symbol.Declaration.End)

[<Fact>]
let ``Names refer to the variables patterns bind`` () =
    let source =
        "def rest = { items -> match items\n" +
        "  | [_, ...tail] -> tail\n" +
        "  | _ -> items }\n" +
        "def either = { pair -> match pair\n" +
        "  | (a, 0) | (0, a) -> a\n" +
        "  | _ -> 0 }\n" +
        "def unwrap = if #some(1) is #some(v) then v else 0\n"
    Assert.Equal(Some (PatternVariable, offsetOf source "tail" 0 0), resolvesTo source (offsetOf source "tail" 1 0))
    // Each alternative of an or-pattern binds the same variable, declared where it is first written.
    Assert.Equal(Some (PatternVariable, offsetOf source "a, 0" 0 0), resolvesTo source (offsetOf source "-> a" 0 3))
    Assert.Equal(Some (PatternVariable, offsetOf source "v)" 0 0), resolvesTo source (offsetOf source "then v" 0 5))

[<Fact>]
let ``The nearest declaration of a name wins`` () =
    let source =
        "def x = 1\n" +
        "def f = { x -> x }\n" +
        "def g = {\n" +
        "  def x = 2\n" +
        "  x\n" +
        "}\n" +
        "def h = x\n"
    Assert.Equal(Some (Parameter, offsetOf source "{ x" 0 2), resolvesTo source (offsetOf source "-> x" 0 3))
    Assert.Equal(Some (LocalValue, offsetOf source "def x = 2" 0 4), resolvesTo source (offsetOf source "  x\n" 0 2))
    Assert.Equal(Some (TopLevelValue, offsetOf source "def x = 1" 0 4), resolvesTo source (offsetOf source "def h = x" 0 8))

[<Fact>]
let ``Names refer to top-level definitions declared later`` () =
    let source =
        "def a = b + 1\n" +
        "def c = twice(b)\n" +
        "def d = b \\twice\n" +
        "def b = 2\n" +
        "def twice = { n -> n * 2 }\n" +
        "def (p, q) = (1, 2)\n" +
        "def r = q\n"
    let b = offsetOf source "def b" 0 4
    let twice = offsetOf source "def twice" 0 4
    Assert.Equal(Some (TopLevelValue, b), resolvesTo source (offsetOf source "b + 1" 0 0))
    // A call and a pipe refer to the function by its name.
    Assert.Equal(Some (TopLevelValue, twice), resolvesTo source (offsetOf source "twice(" 0 0))
    Assert.Equal(Some (TopLevelValue, twice), resolvesTo source (offsetOf source "\\twice" 0 1))
    // A destructuring def declares each name where the pattern writes it.
    Assert.Equal(Some (TopLevelValue, offsetOf source "q)" 0 0), resolvesTo source (offsetOf source "= q" 0 2))

[<Fact>]
let ``Every declaration is a symbol, referred to or not`` () =
    let result = Compiler.compile "def f = { used, unused -> used }\ndef g = 1\n"
    let typed = result.Typed.Value
    // The prelude's exports are symbols too, declared in its own file.
    let declared =
        typed.Symbols
        |> Map.values
        |> Seq.filter (fun symbol -> symbol.Declaration.File = "")
        |> Seq.map (fun symbol -> symbol.Name, symbol.Kind)
        |> Set.ofSeq
    Assert.Equal<Set<string * SymbolKind>>(
        set [ "f", TopLevelValue; "used", Parameter; "unused", Parameter; "g", TopLevelValue ],
        declared)
    let referred = typed.References |> Map.values |> Seq.map (fun id -> typed.Symbols.[id].Name) |> Set.ofSeq
    Assert.Equal<Set<string>>(set [ "used" ], referred)

[<Fact>]
let ``Imported names refer to the imported definition and the import`` () =
    withTempDir (fun dir ->
        let modulePath = Path.Combine(dir, "mod.nyx")
        let moduleSource = "module mod\nexport def foo = 1"
        File.WriteAllText(modulePath, moduleSource)
        let check (mainSource: string) (useText: string) (importName: string) =
            let mainPath = Path.Combine(dir, "main.nyx")
            File.WriteAllText(mainPath, mainSource)
            let result = Compiler.compileFile mainPath
            let offset = offsetOf mainSource useText 0 0
            let definition = (symbolAt (fun typed -> typed.References) result offset).Value
            Assert.Equal(TopLevelValue, definition.Kind)
            Assert.Equal(modulePath, definition.Declaration.File)
            Assert.Equal(offsetOf moduleSource "foo" 0 0, definition.Declaration.Start)
            let import = (symbolAt (fun typed -> typed.ImportReferences) result offset).Value
            Assert.Equal(ImportedModule, import.Kind)
            Assert.Equal(importName, import.Name)
            Assert.Equal(offsetOf mainSource "\"mod.nyx\"" 0 0, import.Declaration.Start)
        check "module main\nimport \"mod.nyx\" as m1\ndef value = m1.foo" "m1.foo" "m1"
        check "module main\nimport \"mod.nyx\"\ndef value = foo" "foo" "mod.nyx"
        // A standard-library definition is declared in the library's own module.
        let source = "import nanyx/text as t\ndef shout = t.String.uppercase(\"hi\")"
        let toUpper = (symbolAt (fun typed -> typed.References) (Compiler.compile source) (offsetOf source "t.String.uppercase" 0 2)).Value
        Assert.Equal("nanyx/text.nyx", toUpper.Declaration.File))

/// What the name at `offset` goes to, as go-to-definition finds it: its kind, file and where it is declared.
let private definitionOf (result: CompileResult) (offset: int) =
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Definition.symbolAt "" result offset
    |> Option.map (fun symbol -> symbol.Kind, symbol.Declaration.File, symbol.Declaration.Start)

[<Fact>]
let ``Type names refer to the type definition`` () =
    let source =
        "def id: Point -> Point = { p -> p }\n" +
        "type Point = (x: int, y: int)\n" +
        "type Line = (start: Point, finish: Point)\n" +
        "type Box(a) = (item: a)\n" +
        "def boxed: Box(Point) -> Point = { b -> b.item }\n" +
        "def g = {\n" +
        "  def q: Point = (x = 1, y = 2)\n" +
        "  def r: Line = (start = q, finish = q)\n" +
        "  r.start\n" +
        "}\n"
    let result = Compiler.compile source
    let point = Some (TypeDefinition, "", offsetOf source "type Point" 0 5)
    // In an annotation, before the type is declared and after.
    Assert.Equal(point, definitionOf result (offsetOf source "Point ->" 0 2))
    Assert.Equal(point, definitionOf result (offsetOf source "-> Point =" 0 3))
    // In another type's definition.
    Assert.Equal(point, definitionOf result (offsetOf source "start: Point" 0 7))
    // An applied type by its name, and the types it is applied to.
    Assert.Equal(Some (TypeDefinition, "", offsetOf source "Box(a)" 0 0), definitionOf result (offsetOf source "Box(Point)" 0 1))
    Assert.Equal(point, definitionOf result (offsetOf source "Box(Point)" 0 4))
    // A local def's annotation.
    Assert.Equal(point, definitionOf result (offsetOf source "q: Point" 0 3))
    Assert.Equal(Some (TypeDefinition, "", offsetOf source "Line =" 0 0), definitionOf result (offsetOf source "r: Line" 0 3))
    // A type definition's own name is its declaration, and a built-in type has none.
    Assert.Equal(point, definitionOf result (offsetOf source "type Point" 0 6))
    Assert.Equal(None, definitionOf result (offsetOf source "x: int" 0 3))

[<Fact>]
let ``Type names refer to imported types in their own file`` () =
    withTempDir (fun dir ->
        let modulePath = Path.Combine(dir, "shapes.nyx")
        let moduleSource = "module shapes\nexport type Shape = #circle(int) | #square(int)\n"
        File.WriteAllText(modulePath, moduleSource)
        let mainPath = Path.Combine(dir, "main.nyx")
        let mainSource = "module main\nimport \"shapes.nyx\"\ndef unit: Shape = #circle(1)\ndef ok: Result(int, string) = #some(1)\n"
        File.WriteAllText(mainPath, mainSource)
        let result = Compiler.compileFile mainPath
        Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
        let at text = Definition.symbolAt mainPath result (offsetOf mainSource text 0 1) |> Option.map (fun symbol -> symbol.Kind, symbol.Declaration.File, symbol.Declaration.Start)
        Assert.Equal(Some (TypeDefinition, modulePath, offsetOf moduleSource "Shape" 0 0), at "Shape =")
        // The prelude's types are declared in the prelude.
        let preludeSource = (StandardLibrary.tryLoadSource "prelude").Value.Replace("\r\n", "\n")
        Assert.Equal(Some (TypeDefinition, "nanyx/prelude.nyx", offsetOf preludeSource "Result(a, e) =" 0 0), at "Result("))

[<Fact>]
let ``Context members are declared in the context and referred to where it is required`` () =
    let source =
        "context Store(a) = (get: () -> a, put: a -> ())\n" +
        "def save: [Store(int)] int -> () = { n -> put(n) }\n" +
        "def peek = Store(int).get\n"
    let result = Compiler.compile source
    let put = Some (ContextMember, "", offsetOf source "put:" 0 0)
    // A member called in a def that requires the context.
    Assert.Equal(put, definitionOf result (offsetOf source "put(n)" 0 1))
    // A member's own name is its declaration.
    Assert.Equal(put, definitionOf result (offsetOf source "put:" 0 1))
    // A qualified member by its name, and the context it is qualified by.
    Assert.Equal(Some (ContextMember, "", offsetOf source "get:" 0 0), definitionOf result (offsetOf source "Store(int).get" 0 12))
    let store = Some (TypeDefinition, "", offsetOf source "Store(a)" 0 0)
    Assert.Equal(store, definitionOf result (offsetOf source "Store(int).get" 0 1))
    // The context a signature requires.
    Assert.Equal(store, definitionOf result (offsetOf source "[Store" 0 2))

[<Fact>]
let ``Context members of an imported context are declared in its module`` () =
    let source = "import nanyx/console\ndef hello: [Console] () -> () = { println(\"hi\") }\n"
    let consoleSource = (StandardLibrary.tryLoadSource "console").Value.Replace("\r\n", "\n")
    Assert.Equal(
        Some (ContextMember, "nanyx/console.nyx", offsetOf consoleSource "println:" 0 0),
        definitionOf (Compiler.compile source) (offsetOf source "println(" 0 2))
    Assert.Equal(
        Some (TypeDefinition, "nanyx/console.nyx", offsetOf consoleSource "Console =" 0 0),
        definitionOf (Compiler.compile source) (offsetOf source "[Console]" 0 2))

// Names in scope: what completion offers at a position.

/// The names in scope at `offset` in `result`, as each name's kind and the file it is declared in.
let private namesIn (result: CompileResult) (offset: int) =
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Completion.namesInScope result offset |> List.map (fun symbol -> symbol.Name, (symbol.Kind, symbol.Declaration.File))

/// The names in scope at `offset` that `source` declares itself, with their kinds: the prelude's are left out.
let private ownNamesAt (source: string) (offset: int) =
    namesIn (Compiler.compile source) offset
    |> List.filter (fun (_, (_, file)) -> file = "")
    |> List.map (fun (name, (kind, _)) -> name, kind)

[<Fact>]
let ``Locals are in scope from their def, parameters in their lambda and top-level defs everywhere`` () =
    let source =
        "def scale = { factor ->\n" +
        "  def before = 1\n" +
        "  def offset = before\n" +
        "  factor * 2 + offset\n" +
        "}\n" +
        "def later = 3\n"
    // A top-level def written later is in scope; a local written later is not.
    Assert.Equal<(string * SymbolKind) list>(
        [ "before", LocalValue; "factor", Parameter; "later", TopLevelValue; "scale", TopLevelValue ],
        ownNamesAt source (offsetOf source "def offset" 0 0))
    Assert.Equal<(string * SymbolKind) list>(
        [ "before", LocalValue; "factor", Parameter; "later", TopLevelValue; "offset", LocalValue; "scale", TopLevelValue ],
        ownNamesAt source (offsetOf source "factor * 2" 0 0))
    // Outside the lambda, only the top-level defs.
    Assert.Equal<(string * SymbolKind) list>(
        [ "later", TopLevelValue; "scale", TopLevelValue ],
        ownNamesAt source (offsetOf source "def later" 0 0))

[<Fact>]
let ``A pattern variable is in scope in its own arm only`` () =
    let source =
        "def rest = { items -> match items\n" +
        "  | [_, ...tail] -> tail\n" +
        "  | _ -> items }\n"
    let inArm = ownNamesAt source (offsetOf source "-> tail" 0 3)
    Assert.Contains(("tail", PatternVariable), inArm)
    let inNextArm = ownNamesAt source (offsetOf source "-> items" 0 3)
    Assert.DoesNotContain(("tail", PatternVariable), inNextArm)
    Assert.Contains(("items", Parameter), inNextArm)

[<Fact>]
let ``The nearest declaration of a name is the one in scope`` () =
    let source =
        "def x = 1\n" +
        "def f = { x -> x }\n" +
        "def g = {\n" +
        "  def x = 2\n" +
        "  x\n" +
        "}\n" +
        "def h = x\n"
    let x offset = ownNamesAt source offset |> List.filter (fun (name, _) -> name = "x")
    Assert.Equal<(string * SymbolKind) list>([ "x", Parameter ], x (offsetOf source "-> x" 0 3))
    Assert.Equal<(string * SymbolKind) list>([ "x", LocalValue ], x (offsetOf source "  x\n" 0 2))
    Assert.Equal<(string * SymbolKind) list>([ "x", TopLevelValue ], x (offsetOf source "def h = x" 0 8))

[<Fact>]
let ``A qualified import is in scope by its alias, an unqualified one by the names it exports`` () =
    withTempDir (fun dir ->
        let modulePath = Path.Combine(dir, "mod.nyx")
        File.WriteAllText(modulePath, "module mod\nexport def foo = 1\ndef hidden = 2")
        let mainPath = Path.Combine(dir, "main.nyx")
        let namesWith (mainSource: string) =
            File.WriteAllText(mainPath, mainSource)
            namesIn (Compiler.compileFile mainPath) (offsetOf mainSource "def value" 0 0)
            |> List.filter (fun (_, (_, file)) -> not (file.StartsWith "nanyx/"))
        Assert.Equal<(string * (SymbolKind * string)) list>(
            [ "m", (ImportedModule, mainPath); "value", (TopLevelValue, mainPath) ],
            namesWith "module main\nimport \"mod.nyx\" as m\ndef value = m.foo")
        Assert.Equal<(string * (SymbolKind * string)) list>(
            [ "foo", (TopLevelValue, modulePath); "value", (TopLevelValue, mainPath) ],
            namesWith "module main\nimport \"mod.nyx\"\ndef value = foo"))

[<Fact>]
let ``The prelude's names are in scope until the module declares its own`` () =
    let proc (source: string) (offset: int) =
        namesIn (Compiler.compile source) offset |> List.filter (fun (name, _) -> name = "proc")
    let fromPrelude = proc "def value = 1\n" 0
    Assert.Equal<(string * (SymbolKind * string)) list>([ "proc", (TopLevelValue, "nanyx/prelude.nyx") ], fromPrelude)
    Assert.Equal<(string * (SymbolKind * string)) list>([ "proc", (TopLevelValue, "") ], proc "def proc = 1\n" 0)
    let source = "def value = {\n  def proc = 2\n  proc\n}\n"
    Assert.Equal<(string * (SymbolKind * string)) list>([ "proc", (LocalValue, "") ], proc source (offsetOf source "  proc\n" 0 2))

// Members: what completion offers after a dot.

/// What can follow `path` and a dot at the start of `at` in `result`, as each member's name, kind and type.
let private membersIn (result: CompileResult) (source: string) (at: string) (path: string list) =
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Completion.membersAt Greek result (offsetOf source at 0 0) path
    |> List.map (fun item -> item.Name, item.Kind, item.Type)

[<Fact>]
let ``After a value's name come the fields of its record, followed through the path`` () =
    let source =
        "def p = (x = 1, name = \"a\", inner = (y = true))\n" +
        "def t = (1, 2)\n" +
        "def f = { v -> v }\n" +
        "def q = p\n"
    let members = membersIn (Compiler.compile source) source
    Assert.Equal<(string * Completion.MemberKind * string option) list>(
        [ "inner", Completion.Field, Some "(y: bool)"; "name", Completion.Field, Some "string"; "x", Completion.Field, Some "int" ],
        members "def q" [ "p" ])
    Assert.Equal<(string * Completion.MemberKind * string option) list>(
        [ "y", Completion.Field, Some "bool" ], members "def q" [ "p"; "inner" ])
    // A tuple's numbered items are not names, and a value not yet known to be a record has no fields.
    Assert.Empty(members "def q" [ "t" ])
    Assert.Empty(members "v }" [ "v" ])

[<Fact>]
let ``After an import's alias come its exports, and after a prefix the names under it`` () =
    withTempDir (fun dir ->
        let modulePath = Path.Combine(dir, "mod.nyx")
        File.WriteAllText(modulePath, "module mod\nexport def foo = 1\nexport def twice = { n -> n * 2 }\ndef hidden = 2")
        let mainPath = Path.Combine(dir, "main.nyx")
        let mainSource = "module main\nimport \"mod.nyx\" as m\ndef value = m.foo"
        File.WriteAllText(mainPath, mainSource)
        Assert.Equal<(string * Completion.MemberKind * string option) list>(
            [ "foo", Completion.Definition, Some "int"; "twice", Completion.Definition, Some "int -> int" ],
            membersIn (Compiler.compileFile mainPath) mainSource "def value" [ "m" ]))
    let source = "import nanyx/text as t\ndef shout = t.String.uppercase(\"hi\")\n"
    let members = membersIn (Compiler.compile source) source "def shout"
    // A dotted export is offered a segment at a time.
    Assert.Contains(("String", Completion.Prefix, None), members [ "t" ])
    Assert.Contains("uppercase", members [ "t"; "String" ] |> List.map (fun (name, _, _) -> name))
    // The prelude's definitions attached to a type.
    Assert.Contains("map", members [ "Result" ] |> List.map (fun (name, _, _) -> name))

[<Fact>]
let ``A module block offers only what it exports`` () =
    let source = "module inner =\n  def secret = 1\n  export def open = secret + 1\ndef value = inner.open\n"
    Assert.Equal<(string * Completion.MemberKind * string option) list>(
        [ "open", Completion.Definition, Some "int" ],
        membersIn (Compiler.compile source) source "def value" [ "inner" ])

// Pipe targets: what completion offers after a pipe's `\`.

/// What a pipe can call when `path` is piped at the start of `at`, as each target's name and whether it is attached.
let private pipeTargetsIn (source: string) (at: string) (path: string list) =
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Completion.pipeTargetsAt Greek result (offsetOf source at 0 0) path

[<Fact>]
let ``After a pipe come the definitions attached to the piped value's type, then the functions in scope`` () =
    let source =
        "import nanyx/text\n" +
        "def s = \"abc\"\n" +
        "def double = { n -> n * 2 }\n" +
        "def r = s\n"
    let targets = pipeTargetsIn source "def r" [ "s" ]
    let attached = targets |> List.takeWhile (fun target -> target.IsAttached)
    Assert.Contains("startsWith", attached |> List.map (fun target -> target.Name))
    Assert.Contains("length", attached |> List.map (fun target -> target.Name))
    Assert.Equal<string list>(attached |> List.map (fun target -> target.Name) |> List.sort, attached |> List.map (fun target -> target.Name))
    let startsWith = attached |> List.find (fun target -> target.Name = "startsWith")
    Assert.True(startsWith.IsFunction)
    // The functions in scope follow, and nothing attached comes after them.
    let rest = targets |> List.skip attached.Length
    let double: Completion.PipeTarget = { Name = "double"; Type = Some "int -> int"; IsFunction = true; IsAttached = false }
    Assert.Contains(double, rest)
    Assert.All(rest, fun target -> Assert.False target.IsAttached)

[<Fact>]
let ``After a pipe come the module's own definitions attached to a type`` () =
    let source =
        "type Point = (x: int, y: int)\n" +
        "def Point.norm = { p -> p.x + p.y }\n" +
        "def p = Point(x = 1, y = 2)\n" +
        "def q = p\n"
    let attached = pipeTargetsIn source "def q" [ "p" ] |> List.filter (fun target -> target.IsAttached)
    Assert.Equal<string list>([ "norm" ], attached |> List.map (fun target -> target.Name))

[<Fact>]
let ``After a pipe on a value of unknown type come only the functions in scope`` () =
    let source = "def f = { v -> v }\n"
    Assert.All(pipeTargetsIn source "v }" [ "v" ], fun target -> Assert.False target.IsAttached)
    Assert.Contains("f", pipeTargetsIn source "v }" [] |> List.map (fun target -> target.Name))

// Type hints: the inferred types shown for declarations that do not write theirs.

/// Each hint as its name, type and where its name starts.
let private typeHints (hintsOf: TypeVarNames -> string -> CompileResult -> TypeHints.TypeHint list) (source: string) =
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    hintsOf Greek "" result |> List.map (fun hint -> hint.Name, hint.Type, hint.Span.Start)

[<Fact>]
let ``Lenses show the types of top-level defs that do not write theirs`` () =
    let source =
        "def id = { x -> x }\n" +
        "def answer: int = 42\n" +
        "def pair = { a, b -> (a, b) }\n" +
        "def (p, q) = (1, \"s\")\n"
    Assert.Equal<(string * string * int) list>(
        [ "id", "α -> α", offsetOf source "id" 0 0
          "pair", "α, β -> (α, β)", offsetOf source "pair" 0 0
          "p", "int", offsetOf source "p," 0 0
          "q", "string", offsetOf source "q)" 0 0 ],
        typeHints TypeHints.typeLenses source)

[<Fact>]
let ``Inlay hints show the types of locals, parameters and pattern variables`` () =
    let source =
        "def scale = { factor ->\n" +
        "  def offset = 1\n" +
        "  def written: int = 2\n" +
        "  factor * written + offset\n" +
        "}\n" +
        "def rest = { items -> match items\n" +
        "  | [_, ...tail] -> tail\n" +
        "  | _ -> items }\n"
    Assert.Equal<(string * string * int) list>(
        [ "factor", "int", offsetOf source "factor" 0 0
          "offset", "int", offsetOf source "offset" 0 0
          "items", "list(α)", offsetOf source "items" 0 0
          "tail", "list(α)", offsetOf source "tail" 0 0 ],
        typeHints TypeHints.inlayHints source)

[<Fact>]
let ``Hints name type variables as their top-level def's type does`` () =
    let source =
        "def pair = { a, b -> (a, b) }\n" +
        "def second = { a, b -> b }\n"
    Assert.Equal<(string * string * int) list>(
        [ "a", "α", offsetOf source "{ a" 0 2
          "b", "β", offsetOf source "b ->" 0 0
          "a", "α", offsetOf source "{ a" 1 2
          "b", "β", offsetOf source "b ->" 1 0 ],
        typeHints TypeHints.inlayHints source)
    Assert.Equal<(string * string * int) list>(
        [ "pair", "α, β -> (α, β)", 4; "second", "α, β -> β", offsetOf source "second" 0 0 ],
        typeHints TypeHints.typeLenses source)

[<Fact>]
let ``Inlay hints with top-level defs have the lenses' types too, in source order`` () =
    let source =
        "def pair = { a, b -> (a, b) }\n" +
        "def answer: int = 42\n" +
        "def second = { a, b -> b }\n"
    Assert.Equal<(string * string * int) list>(
        [ "pair", "α, β -> (α, β)", 4
          "a", "α", offsetOf source "{ a" 0 2
          "b", "β", offsetOf source "b ->" 0 0
          "second", "α, β -> β", offsetOf source "second" 0 0
          "a", "α", offsetOf source "{ a" 1 2
          "b", "β", offsetOf source "b ->" 1 0 ],
        typeHints TypeHints.inlayHintsWithTopLevel source)

[<Fact>]
let ``Hints keep the names of type aliases`` () =
    let source =
        "type Customer = (name: string)\n" +
        "def getCustomer = { name -> match name\n" +
        "  | \"\" -> #customerNotFound\n" +
        "  | _ -> #some(Customer(name = name)) }\n" +
        "type Box(a) = (value: a)\n" +
        "def unbox: Box(int) -> int = { b -> b.value }\n" +
        "def rename: Customer -> Customer = { c -> c with name = \"x\" }\n" +
        "def firstName = { c -> rename(c).name }\n"
    Assert.Equal<(string * string * int) list>(
        [ "getCustomer", "string -> #customerNotFound | #some(Customer)", offsetOf source "getCustomer" 0 0
          "firstName", "Customer -> string", offsetOf source "firstName" 0 0 ],
        typeHints TypeHints.typeLenses source)
    Assert.Equal<(string * string * int) list>(
        [ "name", "string", offsetOf source "name ->" 0 0
          "b", "Box(int)", offsetOf source "b ->" 0 0
          "c", "Customer", offsetOf source "c ->" 0 0
          "c", "Customer", offsetOf source "c ->" 1 0 ],
        typeHints TypeHints.inlayHints source)

[<Fact>]
let ``A match on an alias of a tag union is checked for exhaustiveness`` () =
    let source =
        "type Lookup = #found(int) | #missing\n" +
        "def describe: Lookup -> int = { l -> match l\n" +
        "  | #found(n) -> n }\n"
    let result = Compiler.compile source
    Assert.Contains(result.Diagnostics, fun diag -> diag.Message.Contains "missing tags: missing")

[<Fact>]
let ``A call to a def that matches on a tag union keeps the argument's payload type`` () =
    // Regression: the def's type was generalized before its constraints were solved, so its input came out
    // as a fresh variable unrelated to its result, and `y` had a payload of no particular type.
    let source =
        "def mapId = { | #some(a) -> #some(a) | other -> other }\n" +
        "def y = mapId(#some(5))"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    // `#some(5)` has no other tags, so neither has `y`.
    Assert.Equal(TyTag("some", Some (TyPrimitive "int")), result.Typed.Value.Types.["y"])

[<Fact>]
let ``typeof gives a def the type of another value`` () =
    let source =
        "def defaults = (host = \"localhost\", port = 8080)\n" +
        "def override: typeof defaults = (host = \"example.com\", port = 443)\n" +
        "def port = override.port"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    Assert.Equal(TyPrimitive "int", result.Typed.Value.Types.["port"])

[<Fact>]
let ``typeof rejects a value of another type`` () =
    let source =
        "def defaults = (host = \"localhost\", port = 8080)\n" +
        "def override: typeof defaults = (host = \"example.com\", port = \"443\")"
    let result = Compiler.compile source
    Assert.False(result.Diagnostics.IsEmpty)
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, (List.head result.Diagnostics).Code)

[<Fact>]
let ``typeof can name a value declared later`` () =
    let source =
        "def bad: typeof limit = \"many\"\n" +
        "def limit = 10"
    let result = Compiler.compile source
    Assert.False(result.Diagnostics.IsEmpty)
    let good = Compiler.compile "def other: typeof limit = 20\ndef limit = 10"
    Assert.True(good.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{good.Diagnostics}")
    Assert.Equal(TyPrimitive "int", good.Typed.Value.Types.["other"])

[<Fact>]
let ``typeof a polymorphic value is a fresh instance of it`` () =
    let source =
        "def id = { x -> x }\n" +
        "def same: typeof id = { y -> y }\n" +
        "def a = same(1)\n" +
        "def b = same(\"s\")"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    Assert.Equal(TyPrimitive "int", result.Typed.Value.Types.["a"])
    Assert.Equal(TyPrimitive "string", result.Typed.Value.Types.["b"])

[<Fact>]
let ``typeof takes a path of fields`` () =
    let source =
        "def config = (server = (port = 8080))\n" +
        "def port: typeof config.server.port = 443"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    Assert.Equal(TyPrimitive "int", result.Typed.Value.Types.["port"])

[<Fact>]
let ``typeof works in a function type, a lambda parameter and a local def`` () =
    let source =
        "def defaults = (host = \"localhost\", port = 8080)\n" +
        "def portOf: typeof defaults -> int = { c -> c.port }\n" +
        "def add = { c: typeof defaults, n -> c.port + n }\n" +
        "def local = {\n" +
        "  def limit = 3\n" +
        "  def other: typeof limit = 4\n" +
        "  other\n" +
        "}"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    Assert.Equal("(host: string, port: int) -> int", Unifier.tyToString result.Typed.Value.Types.["portOf"])
    Assert.Equal("(host: string, port: int), int -> int", Unifier.tyToString result.Typed.Value.Types.["add"])
    Assert.Equal(TyFunc(TyPrimitive "unit", TyPrimitive "int"), result.Typed.Value.Types.["local"])

[<Fact>]
let ``typeof reports a name that is not in scope`` () =
    let source = "def x: typeof missing = 1"
    let result = Compiler.compile source
    let diagnostic = List.head result.Diagnostics
    Assert.Equal(DiagnosticCodes.UnknownIdentifier.Code, diagnostic.Code)
    // A def's own name is not in scope in its annotation.
    let own = Compiler.compile "def x: typeof x = 1"
    Assert.Equal(DiagnosticCodes.UnknownIdentifier.Code, (List.head own.Diagnostics).Code)

[<Fact>]
let ``typeof cannot be used in a type definition`` () =
    let source =
        "def defaults = (port = 8080)\n" +
        "type Config = typeof defaults"
    let result = Compiler.compile source
    Assert.Equal(DiagnosticCodes.MisplacedTypeof.Code, (List.head result.Diagnostics).Code)