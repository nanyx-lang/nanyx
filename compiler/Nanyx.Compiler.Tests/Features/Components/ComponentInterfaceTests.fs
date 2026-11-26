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
    let wit = ComponentInterface.render "acme:shapes@0.1.0" "nanyx:console/command" functions []
    let expected =
        """/// Written by `nanyx build` from the program's definitions marked `@component` and `@import`.
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
    let wit = ComponentInterface.render "acme:numbers@1.0.0" "nanyx:console/command" functions []
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
    let export = { Module = "geo.shapes"; Name = "areaOf"; Definition = "areaOf"; Waits = false }
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

[<Fact>]
let ``An export whose type writes the contexts it requires runs under the host's, with no error`` () =
    let typed = compileClean (normalized "@component\nexport def log: [Console] string -> () = { s -> println(s) }\n")
    let export = Assert.Single(typed.ComponentExports)
    Assert.Equal(Hosts.providedExportName "log", export.Definition)
    let func = Assert.Single(ComponentInterface.functions typed)
    Assert.Equal<(string * WitType) list>([ "s", WitType.String ], func.Parameters)

[<Fact>]
let ``An export that requires contexts its type doesn't write is an error that asks for them`` () =
    let diagnostic = diagnosticOf (normalized "@component\nexport def log = { s: string -> println(s) }\n")
    Assert.Equal(DiagnosticCodes.UnsupportedByBackend.Code, diagnostic.Code)
    Assert.Contains("it requires contexts its type doesn't write. To fix it, write them in its type", diagnostic.Message)

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
        """/// Written by `nanyx build` from the program's definitions marked `@component` and `@import`.
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
    Assert.Equal(expected, ComponentInterface.render "acme:shapes@0.1.0" "nanyx:console/command" functions [])

/// A module whose defs are imported from another component, two of which the program calls.
let private geometry =
    """module geometry

export type Point = (x: int, y: int)

@import("acme:geometry/shapes@1.0.0", "area-of")
export def area: Point -> float

@import("acme:geometry/shapes@1.0.0")
export def originOf: () -> Point

@import("acme:geometry/shapes@1.0.0")
export def unused: () -> int
"""

[<Fact>]
let ``A def marked @import is imported from the interface it names, whose package's WIT is written from the declarations`` () =
    let result = compileImporting (normalized geometry) "module main\n\nimport mod as m\n\ndef main = m.area(m.originOf())\n"
    Assert.Empty(result.Diagnostics)
    let imports = ComponentInterface.imports result.Typed.Value
    Assert.Equal<string list>([ "area-of"; "origin-of" ], imports |> List.map _.Export.Name)
    Assert.Contains("  import acme:geometry/shapes@1.0.0;", ComponentInterface.render "acme:app@0.1.0" "nanyx:console/command" [] imports)
    let expected =
        """/// Written by `nanyx build` from the program's definitions marked `@import`.
package acme:geometry@1.0.0;

interface shapes {
  record point {
    x: s64,
    y: s64,
  }

  area-of: func(p0: point) -> f64;
  origin-of: func() -> point;
}
"""
    Assert.Equal<(string * string) list>([ "acme:geometry@1.0.0", expected ], ComponentInterface.importedPackages imports)

[<Fact>]
let ``An import's third argument names its function's parameters in the WIT`` () =
    let source =
        "module geometry\n\n@import(\"acme:geometry/shapes@1.0.0\", \"area\", [\"width\", \"heightOf\"])\nexport def area: float, float -> float\n"
    let result = compileImporting (normalized source) "module main\n\nimport mod as m\n\ndef main = m.area(1.0, 2.0)\n"
    Assert.Empty(result.Diagnostics)
    let imports = ComponentInterface.imports result.Typed.Value
    Assert.Equal<(string * WitType) list>([ "width", WitType.F64; "height-of", WitType.F64 ], imports.Head.Parameters)

[<Theory>]
[<InlineData("@import(\"acme:geometry/shapes\", \"area\", [\"width\"])\ndef area: float, float -> float", "The import 'area' names 1 parameter, and its function takes 2")>]
[<InlineData("@import(\"acme:geometry/shapes\", \"area\", [\"width\", 2])\ndef area: float, float -> float", "named by a list of strings")>]
[<InlineData("@import(\"acme:geometry/shapes\", \"area\", [\"width\", \"width\"])\ndef area: float, float -> float", "Two of the function's parameters are named 'width'")>]
[<InlineData("@import(\"acme:geometry/shapes\", \"area\", [\"width\", \"?\"])\ndef area: float, float -> float", "'?' isn't a parameter's name")>]
let ``An import names each of its function's parameters, once, by a WIT name`` (source: string, message: string) =
    let diagnostic = diagnosticOf (normalized source)
    Assert.Equal(DiagnosticCodes.UnexpectedAttributeArgument.Code, diagnostic.Code)
    Assert.Contains(message, diagnostic.Message)

[<Theory>]
[<InlineData("@import(\"acme:geometry/shapes\")\ndef area = 1", "has a value")>]
[<InlineData("@import(\"acme:geometry/shapes\")\ndef area", "needs its type written")>]
[<InlineData("@import(\"shapes\")\ndef area: float -> float", "isn't a WIT interface's full name")>]
[<InlineData("@import(1)\ndef area: float -> float", "It takes the WIT interface")>]
let ``@import declares a def written with a type and no value, from a WIT interface named in full`` (source: string, message: string) =
    let diagnostic = diagnosticOf (normalized source)
    Assert.Contains(message, diagnostic.Message)

[<Fact>]
let ``An import's type must cross a component's boundary too`` () =
    let diagnostic = diagnosticOf (normalized "@import(\"acme:geometry/shapes\")\ndef apply: (int -> int) -> int\n")
    Assert.Equal(DiagnosticCodes.ComponentBoundaryType.Code, diagnostic.Code)
    Assert.Contains("A component can't import 'apply': a function can't cross", diagnostic.Message)

[<Fact>]
let ``A type named through an import's alias is named in the WIT by its own name`` () =
    let result =
        compileImportingAll
            [ "units.nyx", "module units\n\nexport type Length = (value: float)\n"
              "mod.nyx", "module shapes\n\nimport units as u\n\n@component\nexport def grow: u.Length -> u.Length = { l -> l }\n" ]
            "module main\n\nimport mod as m\n\ndef main = 1\n"
    Assert.Empty(result.Diagnostics)
    let wit = ComponentInterface.render "acme:shapes@0.1.0" "nanyx:console/command" (ComponentInterface.functions result.Typed.Value) []
    Assert.Contains("  record length {", wit)
    Assert.Contains("  grow: func(l: length) -> length;", wit)
