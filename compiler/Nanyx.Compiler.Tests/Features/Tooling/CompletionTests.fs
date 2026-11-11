module Nanyx.Compiler.Tests.Features.Tooling.Completion

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

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
    Completion.membersAt Unicode result (offsetOf source at 0 0) path
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
    // A string's built-in property, but never what is attached to `String`: that follows a pipe.
    let withText = "import nanyx/text\ndef s = \"abc\"\ndef q = s\n"
    Assert.Equal<(string * Completion.MemberKind * string option) list>(
        [ "length", Completion.Property, Some "int" ], membersIn (Compiler.compile withText) withText "def q" [ "s" ])
    Assert.Empty(members "v }" [ "v" ])

[<Fact>]
let ``After the dot that starts a shorthand lambda come the members of its argument`` () =
    let source =
        "def name: (name: string, age: int) -> string = { .name }\n" +
        "def size: string -> int = { .length }\n" +
        "def person = (name = \"a\", age = 3)\n"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    let members at =
        Completion.implicitArgumentMembersAt Unicode result (offsetOf source at 0 0)
        |> List.map (fun item -> item.Name, item.Kind, item.Type)
    Assert.Equal<(string * Completion.MemberKind * string option) list>(
        [ "age", Completion.Field, Some "int"; "name", Completion.Field, Some "string" ], members "name }")
    Assert.Equal<(string * Completion.MemberKind * string option) list>(
        [ "length", Completion.Property, Some "int" ], members "length }")
    // Outside a shorthand lambda there is no argument to complete.
    Assert.Empty(members "person")

[<Fact>]
let ``After the dot that starts a guard pattern come the members of the value matched`` () =
    let source =
        "def size: (name: string, age: int) -> string = { p ->\n" +
        "  match p\n" +
        "    | { .age > 17 } -> \"adult\"\n" +
        "    | _ -> \"minor\"\n" +
        "}\n"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Assert.Equal<(string * Completion.MemberKind * string option) list>(
        [ "age", Completion.Field, Some "int"; "name", Completion.Field, Some "string" ],
        Completion.implicitArgumentMembersAt Unicode result (offsetOf source "age >" 0 0)
        |> List.map (fun item -> item.Name, item.Kind, item.Type))

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
    let source = "import nanyx/text as t\ndef shout = t.string.uppercase(\"hi\")\n"
    let members = membersIn (Compiler.compile source) source "def shout"
    // A dotted export is offered a segment at a time.
    Assert.Contains(("string", Completion.Prefix, None), members [ "t" ])
    Assert.Contains("uppercase", members [ "t"; "string" ] |> List.map (fun (name, _, _) -> name))
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
    Completion.pipeTargetsAt Unicode result (offsetOf source at 0 0) path

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
