module Nanyx.Compiler.Tests.Features.Contexts

open System
open System.IO
open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

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
    let diagnostic = result.Diagnostics |> List.head
    // The context is named, as a type alias is, not listed by its members.
    Assert.Equal("Missing context", diagnostic.Title)
    Assert.Equal("'g' requires the context 'Console', which is not available here", diagnostic.Message)

[<Fact>]
let ``Missing contexts are named, not listed by their members`` () =
    let message source =
        let diagnostic = (Compiler.compile source).Diagnostics |> List.head
        Assert.True(diagnostic.Code = DiagnosticCodes.MissingContextMembers.Code, diagnostic.Message)
        diagnostic.Message
    let contexts =
        "context Log = (log: string -> ())\n" +
        "context Clock = (now: () -> int)\n"
    Assert.Equal(
        "'g' requires the contexts 'Log' and 'Clock', which are not available here",
        message (contexts + "def g: [Log + Clock] () -> () = { log(\"hi\") }\ndef h = { g() }"))
    // A context the caller has is not listed.
    Assert.Equal(
        "'g' requires the context 'Clock', which is not available here",
        message (contexts + "def g: [Log + Clock] () -> () = { log(\"hi\") }\ndef h: [Log] () -> () = { g() }"))

/// A parameterised context, a type, and a function that requires the context, for the tests of where a call finds an
/// instance of it (#328).
let private showContext =
    "context Show(a) = (show: a -> string)\n" +
    "type Point = (x: int, y: int)\n" +
    "def describe: [Show(a)] a -> string = { value -> Show(a).show(value) }\n"

/// Where each call in `source` that requires a parameterised context finds its instances, in the order written. The
/// interpolation holes of built-in types, here and in the prelude, are left out.
let private instancesIn (source: string) =
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    result.Typed.Value.ContextInstances
    |> Map.toList
    |> List.sortBy fst
    |> List.map snd
    |> List.filter ((<>) [ Some InstanceBuiltIn ])

[<Fact>]
let ``A call synthesizes a context's instance from the type's attached definitions`` () =
    let source =
        showContext +
        "def Point.show: Point -> string = { p -> \"{p.x}\" }\n" +
        "def s = describe(Point(x = 1, y = 2))\n" +
        "def t = Point(x = 3, y = 4) \\describe\n"
    let synthesized = [ Some (InstanceSynthesized [ "show", "Point.show" ]) ]
    Assert.Equal<ContextInstance option list list>([ synthesized; synthesized ], instancesIn source)

[<Fact>]
let ``A loaded instance wins over synthesis, and a generic function passes its own on`` () =
    let source =
        showContext +
        "def Point.show: Point -> string = { p -> \"{p.x}\" }\n" +
        "def twice: [Show(a)] a -> string = { v -> describe(v) ++ describe(v) }\n" +
        "def main = {\n" +
        "  use Show(Point) = ( show = { p -> \"p\" } )\n" +
        "  describe(Point(x = 1, y = 2))\n" +
        "}\n"
    match instancesIn source with
    | [ [ Some (InstanceInScope (Kind (TypeApply ("Show", [ Kind (TypeVar "a") ])))) ]
        [ Some (InstanceInScope (Kind (TypeApply ("Show", [ Kind (TypeVar "a") ])))) ]
        [ Some (InstanceInScope (Kind (TypeApply ("Show", [ Kind (TypeName "Point") ])))) ] ] -> ()
    | other -> failwith $"Unexpected instances: %A{other}"

[<Fact>]
let ``A loaded instance is only used for the type it is for`` () =
    // `use Show(Point)` does not make a `Show($Money)`: that one is still synthesized.
    let source =
        showContext +
        "type $Money = private int\n" +
        "def $Money.show: $Money -> string = { m -> \"money\" }\n" +
        "def main = {\n" +
        "  use Show(Point) = ( show = { p -> \"p\" } )\n" +
        "  describe($Money(5))\n" +
        "}\n"
    Assert.Equal<ContextInstance option list list>([ [ Some (InstanceSynthesized [ "show", "$Money.show" ]) ] ], instancesIn source)

[<Fact>]
let ``A loaded instance is checked against the context's members at the type it is for`` () =
    let source = showContext + "def s = {\n  use Show(int) = ( show = { n -> n } )\n  1\n}\n"
    match (Compiler.compile source).Diagnostics with
    | [ diagnostic ] ->
        Assert.Equal(DiagnosticCodes.TypeMismatch.Code, diagnostic.Code)
        Assert.Equal("Type mismatch: int vs string.", diagnostic.Message)
    | other -> failwith $"Expected one diagnostic, got %A{other}"

[<Fact>]
let ``A context that cannot be synthesized says which definition is missing or has the wrong type`` () =
    let message source =
        match (Compiler.compile (showContext + source)).Diagnostics with
        | [ diagnostic ] ->
            Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, diagnostic.Code)
            diagnostic.Message
        | other -> failwith $"Expected one diagnostic, got %A{other}"
    Assert.Equal(
        "'describe' requires the context 'Show(Point)', which is not loaded here, and 'Point.show' is not defined: define it, or load an instance with `use`",
        message "def s = describe(Point(x = 1, y = 2))\n")
    Assert.Equal(
        "'describe' requires the context 'Show(int)', which is not loaded here, and 'Int.show' is not defined: define it, or load an instance with `use`",
        message "def s = describe(3)\n")
    Assert.Equal(
        "'describe' requires the context 'Show(Point)', but 'Point.show' has the type 'Point -> int', where 'show' must be 'Point -> string'",
        message "def Point.show: Point -> int = { p -> p.x }\ndef s = describe(Point(x = 1, y = 2))\n")

[<Fact>]
let ``A context and the function that requires it can come from an import`` () =
    withTempDir (fun dir ->
        File.WriteAllText(
            Path.Combine(dir, "shows.nyx"),
            "module shows\n" +
            "export context Show(a) = (show: a -> string)\n" +
            "export def describe: [Show(a)] a -> string = { value -> Show(a).show(value) }\n")
        let mainPath = Path.Combine(dir, "main.nyx")
        File.WriteAllText(
            mainPath,
            "module main\n" +
            "import \"shows.nyx\"\n" +
            "type Point = (x: int, y: int)\n" +
            "def Point.show: Point -> string = { p -> \"{p.x}\" }\n" +
            "def s = describe(Point(x = 1, y = 2))\n")
        let result = Compiler.compileFile mainPath
        Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
        Assert.Contains(Some (InstanceSynthesized [ "show", "Point.show" ]), result.Typed.Value.ContextInstances |> Map.values |> Seq.concat))

/// `showContext` with a `Point.show` and a function that calls `describe` without writing its type (#544).
let private inferredShow =
    showContext +
    "def Point.show: Point -> string = { p -> \"({p.x}, {p.y})\" }\n" +
    "def twice = { v -> describe(v) ++ describe(v) }\n"

[<Fact>]
let ``A def that does not write its type requires the parameterised context it calls a function with at its own type`` () =
    // `twice` passes its own instance of `Show(a)` on, and the call to it builds one for `Point`.
    let source = inferredShow + "def s = twice(Point(x = 1, y = 2))\n"
    match instancesIn source with
    | [ [ Some (InstanceInScope (Kind (TypeApply ("Show", [ Kind (TypeVar "a") ])))) ]
        [ Some (InstanceInScope (Kind (TypeApply ("Show", [ Kind (TypeVar "a") ])))) ]
        [ Some (InstanceSynthesized [ "show", "Point.show" ]) ] ] -> ()
    | other -> failwith $"Unexpected instances: %A{other}"

[<Fact>]
let ``An inferred parameterised context passes through another def that does not write its type`` () =
    let source =
        inferredShow +
        "def thrice = { v -> twice(v) ++ describe(v) }\n" +
        "def s = thrice(Point(x = 1, y = 2))\n"
    match instancesIn source with
    | [ _; _; [ Some (InstanceInScope (Kind (TypeApply ("Show", [ Kind (TypeVar "a") ])))) ]
        [ Some (InstanceInScope (Kind (TypeApply ("Show", [ Kind (TypeVar "a") ])))) ]
        [ Some (InstanceSynthesized [ "show", "Point.show" ]) ] ] -> ()
    | other -> failwith $"Unexpected instances: %A{other}"

[<Fact>]
let ``A def with an inferred parameterised context can be used at two types`` () =
    let source =
        inferredShow +
        "type Size = (w: int)\n" +
        "def Size.show: Size -> string = { s -> \"{s.w}px\" }\n" +
        "def s = twice(Point(x = 1, y = 2)) ++ twice(Size(w = 3))\n"
    match instancesIn source with
    | [ _; _; [ Some (InstanceSynthesized [ "show", "Point.show" ]) ]; [ Some (InstanceSynthesized [ "show", "Size.show" ]) ] ] -> ()
    | other -> failwith $"Unexpected instances: %A{other}"

[<Fact>]
let ``A context needed at a type the call leaves open is found once the rest of the def settles it`` () =
    let source =
        inferredShow +
        "def onlyPoint: Point -> int = { p -> p.x }\n" +
        "def settled = { v ->\n" +
        "  def s = describe(v)\n" +
        "  s ++ Int.toString(onlyPoint(v))\n" +
        "}\n"
    match instancesIn source with
    | [ _; _; [ Some (InstanceSynthesized [ "show", "Point.show" ]) ] ] -> ()
    | other -> failwith $"Unexpected instances: %A{other}"

[<Fact>]
let ``A context needed at a type that is not one of the def's own is still missing`` () =
    // `v.x` is a variable of the def's type, but of an open record, which no annotation can write.
    let result = Compiler.compile (showContext + "def bad = { v -> describe(v.x) }\n")
    match result.Diagnostics with
    | [ diagnostic ] ->
        Assert.Equal(DiagnosticCodes.MissingContextMembers.Code, diagnostic.Code)
        Assert.Equal("'describe' requires the context 'Show', which is not available here", diagnostic.Message)
    | other -> failwith $"Expected one diagnostic, got %A{other}"

[<Fact>]
let ``Hover and the type lens show the parameterised contexts a def was inferred to require`` () =
    let source = inferredShow
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let hover = Hover.typeAt Unicode result (offsetOf source "twice" 0 0)
    Assert.Equal(Some "[Show(α)] α -> string", hover |> Option.map _.Type)
    let lenses = TypeHints.typeLenses Unicode "" result |> List.map (fun hint -> hint.Name, hint.Type)
    Assert.Equal<(string * string) list>([ "twice", "[Show(α)] α -> string" ], lenses)

/// A context whose member is an operator, and a generic function that requires it.
let private addContext =
    "context Add(a) = (`+`: (a, a) -> a)\n"

[<Fact>]
let ``An operator in a generic function calls the member of that name of the context the function requires`` () =
    let source =
        addContext +
        "context Sum(a) = (zero: () -> a, `+`: (a, a) -> a)\n" +
        "def add: [Add(a)] a, a -> a = { x, y -> x + y }\n" +
        "def sum: [Sum(a)] list(a) -> a = { items -> items \\fold(zero()) { + } }\n"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

[<Fact>]
let ``An operator on ints is the built-in one in a function that requires a context with that operator`` () =
    let source =
        addContext +
        "def addCounting: [Add(a)] a, a, int -> (a, int) = { x, y, n -> (x + y, n + 1) }\n"
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")

[<Fact>]
let ``An operator that calls a context member checks its operands against the member's type`` () =
    let source =
        addContext +
        "def addOne: [Add(a)] a -> a = { x -> x + 1 }\n"
    let result = Compiler.compile source
    match result.Diagnostics with
    | [ diagnostic ] -> Assert.Equal(DiagnosticCodes.AnnotationTooGeneral.Code, diagnostic.Code)
    | other -> failwith $"Expected one diagnostic, got %A{other}"
