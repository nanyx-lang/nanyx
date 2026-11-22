/// The WIT interface a program gives a component (#2241): the definitions marked `@component`, the WIT types of their
/// parameters and results, the names WIT gives them, and the types that can't cross a component's boundary.
module Nanyx.Compiler.Tests.Features.Components.ComponentInterface

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

/// The functions a component built from `source`, a module `shapes` that `main` imports, exports.
let private exported (source: string) =
    let result = compileImporting (normalized source) "module main\n\nimport mod as m\n\ndef main = 1\n"
    Assert.Empty(result.Diagnostics)
    ComponentInterface.functions result.Typed.Value

[<Fact>]
let ``A def marked @component is exported in the interface its module names, even when the program doesn't call it`` () =
    let functions =
        exported
            """module shapes

@component
export def areaOf: float, float -> float = { width, height -> width * height }

@component
export def greet: string -> string = { name -> "Hello, " ++ name }

export def notExported: int -> int = { n -> n }
"""
    Assert.Equal<string list>([ "areaOf"; "greet" ], functions |> List.map _.Export.Name)
    Assert.Equal<string list>([ "shapes"; "shapes" ], functions |> List.map _.Export.Module)
    Assert.Equal<string list>([ "m.areaOf"; "m.greet" ], functions |> List.map _.Export.Definition)
    let wit = ComponentInterface.render "acme:shapes@0.1.0" "nanyx:console/command" functions
    let expected =
        """/// Written by `nanyx build` from the program's definitions marked `@component`.
package acme:shapes@0.1.0;

interface shapes {
  area-of: func(width: f64, height: f64) -> f64;
  greet: func(name: string) -> string;
}

world component {
  include nanyx:console/command;
  export shapes;
}
"""
    Assert.Equal(expected, wit)

[<Fact>]
let ``Each number type is the WIT type of its width and sign, and a function of () takes nothing and gives nothing`` () =
    let functions =
        exported
            """module numbers

@component
export def all: I8, I16, I32, int, U8, U16, U32, U64, F32, float, bool -> int = { a, b, c, d, e, f, g, h, i, j, k -> d }

@component
export def tick: () -> () = { -> () }
"""
    let wit = ComponentInterface.render "acme:numbers@1.0.0" "nanyx:console/command" functions
    Assert.Contains("  all: func(a: s8, b: s16, c: s32, d: s64, e: u8, f: u16, g: u32, h: u64, i: f32, j: f64, k: bool) -> s64;", wit)
    Assert.Contains("  tick: func();", wit)

[<Theory>]
[<InlineData("areaOf", "area-of")>]
[<InlineData("toHTTPServer", "to-http-server")>]
[<InlineData("geo.shapes", "geo-shapes")>]
[<InlineData("point2d", "point2d")>]
[<InlineData("type", "%type")>]
let ``A name is written in kebab case, and escaped when it is one of WIT's keywords`` (name: string, expected: string) =
    Assert.Equal(expected, ComponentInterface.witName name)

[<Theory>]
[<InlineData("acme.shapes", "acme:shapes@0.1.0")>]
[<InlineData("greeter", "greeter:greeter@0.1.0")>]
[<InlineData("acme.geo.shapes", "acme:geo-shapes@0.1.0")>]
let ``A package's WIT package is named from its manifest's name and version`` (name: string, expected: string) =
    Assert.Equal(expected, ComponentInterface.packageName name "0.1.0")

[<Fact>]
let ``An export's core name is its interface's and its own, as wit-component expects`` () =
    let export = { Module = "geo.shapes"; Name = "areaOf"; Definition = "areaOf" }
    Assert.Equal("acme:shapes/geo-shapes@0.1.0#area-of", ComponentInterface.exportName "acme:shapes@0.1.0" export)

[<Theory>]
[<InlineData("export def apply: (int -> int) -> int = { f -> f(1) }", "a function can't cross")>]
[<InlineData("export def first: list(a) -> int = { xs -> 1 }", "a type variable can't cross")>]
[<InlineData("export def answer: int = 42", "a value that isn't a function can't cross")>]
[<InlineData("export def origin: () -> (x: int, y: int) = { -> (x = 0, y = 0) }", "a record with no name can't cross")>]
[<InlineData("export def flip: () -> #heads | #tails = { -> #heads }", "a tag union with no name can't cross")>]
[<InlineData("export def pick: int | string -> int = { x -> 1 }", "a union of types other than tags can't cross")>]
let ``A type that can't cross a component's boundary is an error at the def`` (def: string, message: string) =
    let source = normalized $"@component\n{def}\n"
    let code, text = singleError source
    Assert.Equal(DiagnosticCodes.ComponentBoundaryType.Code, code)
    Assert.Equal(def, text)
    Assert.Contains(message, (diagnosticOf source).Message)

[<Theory>]
[<InlineData("@component\nexport def log: [Console] string -> () = { s -> println(s) }", "a function that requires contexts")>]
let ``A type the compiler can't take across a component's boundary yet is an error that says so`` (source: string, what: string) =
    let diagnostic = diagnosticOf (normalized source)
    Assert.Equal(DiagnosticCodes.UnsupportedByBackend.Code, diagnostic.Code)
    Assert.Contains($"{what} can't cross a component's boundary yet", diagnostic.Message)

[<Fact>]
let ``@component marks only an exported def, and takes no argument`` () =
    let code, text = singleError (normalized "@component\ndef area: float -> float = { r -> r }\n")
    Assert.Equal(DiagnosticCodes.MisplacedAttribute.Code, code)
    Assert.Equal("@component", text)
    let code, _ = singleError (normalized "@component(1)\nexport def area: float -> float = { r -> r }\n")
    Assert.Equal(DiagnosticCodes.UnexpectedAttributeArgument.Code, code)


[<Fact>]
let ``A named record or tag union is a WIT record or variant of its name, with its fields and cases as they are written`` () =
    let functions =
        exported
            """module shapes

export type Point = (y: float, x: float)

export type Shape = #circle(float) | #rect(Point, Point) | #empty

export type Pair(a) = (left: a, right: a)

@component
export def area: Shape -> float = { s -> 0.0 }

@component
export def corners: list(Pair(Point)) -> (int, list(string)) = { xs -> (0, []) }
"""
    let expected =
        """/// Written by `nanyx build` from the program's definitions marked `@component`.
package acme:shapes@0.1.0;

interface shapes {
  record point {
    y: f64,
    x: f64,
  }

  variant shape {
    circle(f64),
    rect(tuple<point, point>),
    empty,
  }

  record pair-point {
    left: point,
    right: point,
  }

  area: func(s: shape) -> f64;
  corners: func(xs: list<pair-point>) -> tuple<s64, list<string>>;
}

world component {
  include nanyx:console/command;
  export shapes;
}
"""
    Assert.Equal(expected, ComponentInterface.render "acme:shapes@0.1.0" "nanyx:console/command" functions)
