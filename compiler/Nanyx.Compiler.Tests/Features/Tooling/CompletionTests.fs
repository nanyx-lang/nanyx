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
        normalized """def scale = { factor ->
  def before = 1
  def offset = before
  factor * 2 + offset
}
def later = 3
"""
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
        normalized """def rest = { items -> match items
  | [_, ...tail] -> tail
  | _ -> items }
"""
    let inArm = ownNamesAt source (offsetOf source "-> tail" 0 3)
    Assert.Contains(("tail", PatternVariable), inArm)
    let inNextArm = ownNamesAt source (offsetOf source "-> items" 0 3)
    Assert.DoesNotContain(("tail", PatternVariable), inNextArm)
    Assert.Contains(("items", Parameter), inNextArm)

[<Fact>]
let ``The nearest declaration of a name is the one in scope`` () =
    let source =
        normalized """def x = 1
def f = { x -> x }
def g = {
  def x = 2
  x
}
def h = x
"""
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
            namesWith "module main\nimport mod as m\ndef value = m.foo")
        Assert.Equal<(string * (SymbolKind * string)) list>(
            [ "foo", (TopLevelValue, modulePath); "value", (TopLevelValue, mainPath) ],
            namesWith "module main\nimport mod\ndef value = foo"))

[<Fact>]
let ``The prelude's names are in scope until the module declares its own`` () =
    let proc (source: string) (offset: int) =
        namesIn (Compiler.compile source) offset |> List.filter (fun (name, _) -> name = "proc")
    let fromPrelude = proc "def value = 1\n" 0
    Assert.Equal<(string * (SymbolKind * string)) list>([ "proc", (TopLevelValue, "nanyx/prelude-results.nyx") ], fromPrelude)
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
        normalized """def p = (x = 1, name = "a", inner = (y = true))
def t = (1, 2)
def f = { v -> v }
def q = p
"""
    let members = membersIn (Compiler.compile source) source
    Assert.Equal<(string * Completion.MemberKind * string option) list>(
        [ "inner", Completion.Field, Some "(y: bool)"; "name", Completion.Field, Some "string"; "x", Completion.Field, Some "int" ],
        members "def q" [ "p" ])
    Assert.Equal<(string * Completion.MemberKind * string option) list>(
        [ "y", Completion.Field, Some "bool" ], members "def q" [ "p"; "inner" ])
    // A tuple's numbered items are not names, and a value not yet known to be a record has no fields.
    Assert.Empty(members "def q" [ "t" ])
    // A string's built-in property, but never what is attached to `String`: that follows a pipe.
    let withText = withFinalNewline """import nanyx/text
def s = "abc"
def q = s"""
    Assert.Equal<(string * Completion.MemberKind * string option) list>(
        [ "length", Completion.Property, Some "int" ], membersIn (Compiler.compile withText) withText "def q" [ "s" ])
    Assert.Empty(members "v }" [ "v" ])

[<Fact>]
let ``After the dot that starts a shorthand lambda come the members of its argument`` () =
    let source =
        normalized """def name: (name: string, age: int) -> string = { .name }
def size: string -> int = { .length }
def person = (name = "a", age = 3)
"""
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
        normalized """def size: (name: string, age: int) -> string = { p ->
  match p
    | { .age > 17 } -> "adult"
    | _ -> "minor"
}
"""
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
        let mainSource = "module main\nimport mod as m\ndef value = m.foo"
        File.WriteAllText(mainPath, mainSource)
        Assert.Equal<(string * Completion.MemberKind * string option) list>(
            [ "foo", Completion.Definition, Some "int"; "twice", Completion.Definition, Some "int -> int" ],
            membersIn (Compiler.compileFile mainPath) mainSource "def value" [ "m" ]))
    let source = withFinalNewline """import nanyx/text as t
def shout = t.string.uppercase("hi")"""
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
        normalized """import nanyx/text
def s = "abc"
def double = { n -> n * 2 }
def r = s
"""
    let targets = pipeTargetsIn source "def r" [ "s" ]
    let attached = targets |> List.takeWhile (fun target -> target.IsAttached)
    Assert.Contains("startsWith", attached |> List.map (fun target -> target.Name))
    Assert.Contains("length", attached |> List.map (fun target -> target.Name))
    Assert.Equal<string list>(attached |> List.map (fun target -> target.Name) |> List.sort, attached |> List.map (fun target -> target.Name))
    let startsWith = attached |> List.find (fun target -> target.Name = "startsWith")
    Assert.True(startsWith.IsFunction)
    // The functions in scope follow, and nothing attached comes after them.
    let rest = targets |> List.skip attached.Length
    Assert.Contains(
        ("double", Some "int -> int", true, false),
        rest |> List.map (fun target -> target.Name, target.Type, target.IsFunction, target.IsAttached))
    Assert.All(rest, fun target -> Assert.False target.IsAttached)

[<Fact>]
let ``After a pipe come the module's own definitions attached to a type`` () =
    let source =
        normalized """type Point = (x: int, y: int)
def Point.norm = { p -> p.x + p.y }
def p = Point(x = 1, y = 2)
def q = p
"""
    let attached = pipeTargetsIn source "def q" [ "p" ] |> List.filter (fun target -> target.IsAttached)
    Assert.Equal<string list>([ "norm" ], attached |> List.map (fun target -> target.Name))

[<Fact>]
let ``After a pipe on a value of unknown type come only the functions in scope`` () =
    let source = "def f = { v -> v }\n"
    Assert.All(pipeTargetsIn source "v }" [ "v" ], fun target -> Assert.False target.IsAttached)
    Assert.Contains("f", pipeTargetsIn source "v }" [] |> List.map (fun target -> target.Name))

// Reserved names: the standard library's `__` names are never offered outside it.

/// Those of `names` that start with `__`.
let private reserved (names: string list) = names |> List.filter (fun name -> name.StartsWith ReservedNames.prefix)

[<Fact>]
let ``The standard library's reserved names are never offered, at the start of an expression, after a pipe or after a dot`` () =
    let written =
        """import nanyx/text
import nanyx/console
import nanyx/math
def s = "abc"
def r = s
"""
    let source = written.ReplaceLineEndings "\n"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    // The prelude's reserved names are in scope, since what `==` and `**` are desugared to calls them.
    Assert.Contains("__equals", result.Typed.Value.Symbols |> Map.values |> Seq.map _.Name)
    let at = offsetOf source "def r" 0 0
    Assert.Empty(reserved (Completion.namesInScope result at |> List.map _.Name))
    Assert.Empty(reserved (Completion.typedNamesInScope Unicode result at |> List.map _.Symbol.Name))
    Assert.Empty(reserved (Completion.pipeTargetsAt Unicode result at [ "s" ] |> List.map _.Name))
    for path in [ [ "s" ]; [ "string" ]; [ "String" ]; [ "int" ]; [ "float" ]; [ "list" ]; [ "Result" ] ] do
        Assert.Empty(reserved (Completion.membersAt Unicode result at path |> List.map _.Name))

[<Fact>]
let ``A module's own reserved names are offered in it`` () =
    let source = "def __twice = { n -> n * 2 }\ndef four = __twice(2)\n"
    Assert.Equal<string list>([ "__twice" ], reserved (ownNamesAt source (offsetOf source "def four" 0 0) |> List.map fst))

[<Fact>]
let ``After a value of an open record type come the fields its type lists`` () =
    let source = "def g: (x: int, name: string, ...) -> int = { p -> p.x }\n"
    Assert.Equal<(string * Completion.MemberKind * string option) list>(
        [ "name", Completion.Field, Some "string"; "x", Completion.Field, Some "int" ],
        membersIn (Compiler.compile source) source "p.x }" [ "p" ])
