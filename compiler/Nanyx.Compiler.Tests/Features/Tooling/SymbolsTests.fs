module Nanyx.Compiler.Tests.Features.Tooling.Symbols

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

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
let ``Names in a guard pattern's predicate refer to its parameter and to what is in scope at the match`` () =
    let source =
        "def size = { n, limit -> match n\n" +
        "  | { x -> x > limit * 2 } -> 2\n" +
        "  | _ -> 0 }\n"
    Assert.Equal(Some (Parameter, offsetOf source "{ x" 0 2), resolvesTo source (offsetOf source "x >" 0 0))
    Assert.Equal(Some (Parameter, offsetOf source "limit" 0 0), resolvesTo source (offsetOf source "limit *" 0 0))

[<Fact>]
let ``Names refer to the variables a pattern parameter binds`` () =
    let source =
        "type $Email = string\n" +
        "def value = { $Email(s) -> s }\n" +
        "def add = { (a, b), c -> a + b + c }\n"
    Assert.Equal(Some (PatternVariable, offsetOf source "s)" 0 0), resolvesTo source (offsetOf source "-> s" 0 3))
    Assert.Equal(Some (PatternVariable, offsetOf source "b)" 0 0), resolvesTo source (offsetOf source "b +" 0 0))
    Assert.Equal(Some (Parameter, offsetOf source "c ->" 0 0), resolvesTo source (offsetOf source "+ c" 0 2))
    // The parameter that stands in the pattern's place is made up, so it is no symbol.
    let declared =
        (Compiler.compile source).Typed.Value.Symbols
        |> Map.values
        |> Seq.filter (fun symbol -> symbol.Declaration.File = "")
        |> Seq.map (fun symbol -> symbol.Name)
        |> Set.ofSeq
    for name in [ "value"; "s"; "add"; "a"; "b"; "c" ] do
        Assert.Contains(name, declared)
    Assert.DoesNotContain(declared, fun name -> name.StartsWith "<")

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

/// The text of each name in `source` that refers to what the name at `offset` declares or refers to, with where
/// it starts.
let private referencesAt (source: string) (offset: int) =
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    let symbol = (Definition.symbolAt "" result offset).Value
    Definition.referencesTo result symbol |> List.map (fun span -> source.Substring(span.Start, span.End - span.Start), span.Start)

[<Fact>]
let ``References are every name that refers to a declaration, in order`` () =
    let source =
        "type Point = (x: int, y: int)\n" +
        "def origin: Point = (x = 0, y = 0)\n" +
        "def shift = { p, d -> (x = p.x + d, y = p.y + d) }\n" +
        "def twice = shift(shift(origin, 1), 1)\n" +
        "def piped = origin \\shift(2)\n"
    let at text occurrence = text, offsetOf source text occurrence 0
    // A top-level def, from its declaration or from a use: its calls by name, and a pipe's target.
    let shift = [ at "shift" 1; at "shift" 2; at "shift" 3 ]
    Assert.Equal<(string * int) list>(shift, referencesAt source (offsetOf source "def shift" 0 4))
    Assert.Equal<(string * int) list>(shift, referencesAt source (offsetOf source "\\shift" 0 1))
    Assert.Equal<(string * int) list>([ at "origin" 1; at "origin" 2 ], referencesAt source (offsetOf source "def origin" 0 4))
    // A parameter, and a type.
    Assert.Equal<(string * int) list>([ "d", offsetOf source "+ d" 0 2; "d", offsetOf source "+ d" 1 2 ], referencesAt source (offsetOf source "p, d" 0 3))
    Assert.Equal<(string * int) list>([ at "Point" 1 ], referencesAt source (offsetOf source "type Point" 0 5))

[<Fact>]
let ``References to an import are the alias its names are written with`` () =
    let source = "import nanyx/text as t\ndef a = t.String.uppercase(\"x\")\ndef b = t.String.uppercase(\"y\")\n"
    Assert.Equal<(string * int) list>(
        [ "t", offsetOf source "t.String" 0 0; "t", offsetOf source "t.String" 1 0 ],
        referencesAt source (offsetOf source "as t" 0 3))

/// What hovering the type name at `offset` shows, and the text of the name it covers.
let private typeDefinitionHover (file: string) (result: CompileResult) (source: string) (offset: int) =
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    Definition.typeDefinitionAt Greek file result offset
    |> Option.map (fun info -> info.Type, source.Substring(info.Span.Start, info.Span.End - info.Span.Start))

[<Fact>]
let ``Hovering a type's name shows its definition`` () =
    let source =
        "type Point = (x: int, y: int)\n" +
        "type Box(a) = (item: a)\n" +
        "type Shape = #circle(int) | #square(int)\n" +
        "context Logger = (log: string -> ())\n" +
        "def origin: Point = (x = 0, y = 0)\n" +
        "def boxed: Box(Point) = (item = origin)\n" +
        "def greet: [Logger] string -> () = { s -> log(s) }\n" +
        "def local = {\n" +
        "  def p: Point = (x = 1, y = 2)\n" +
        "  p.x\n" +
        "}\n"
    let result = Compiler.compile source
    let hover text within = typeDefinitionHover "" result source (offsetOf source text 0 within)
    let point = Some ("type Point = (x: int, y: int)", "Point")
    // In an annotation, on the type's own name, and in a local def's annotation inside a block.
    Assert.Equal(point, hover "origin: Point" 9)
    Assert.Equal(point, hover "type Point" 6)
    Assert.Equal(point, hover "p: Point" 4)
    // A type's parameters keep their names, and an applied type is shown as it is defined.
    Assert.Equal(Some ("type Box(a) = (item: a)", "Box"), hover "Box(Point)" 1)
    Assert.Equal(point, hover "Box(Point)" 5)
    Assert.Equal(Some ("type Shape = #circle(int) | #square(int)", "Shape"), hover "type Shape" 6)
    // A context, in a signature. A function-typed member is bracketed, as the type printer writes any field's.
    Assert.Equal(Some ("context Logger = (log: (string -> ()))", "Logger"), hover "[Logger]" 2)
    // Anything else is not a type's name.
    Assert.Equal(None, hover "def origin" 5)
    Assert.Equal(None, hover "x: int" 3)

[<Fact>]
let ``Hovering an imported type's name shows its definition`` () =
    let source = "def ok: Result(int, string) = #some(1)\n"
    Assert.Equal(
        Some ("type Result(a, e) = #some(a) | #error(e)", "Result"),
        typeDefinitionHover "" (Compiler.compile source) source (offsetOf source "Result" 0 1))
