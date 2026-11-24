module Nanyx.Compiler.Tests.Features.ConstructorPatterns

open Xunit
open NyxCompiler
open Nanyx.Compiler.Tests.TestSupport

// A value taken apart by its type's constructor (#2504): one pattern matches the whole of what the type is made from;
// several, a trailing `...` or named fields take a record's fields, as a record pattern does.

/// The type of each local the compiled `source` declares by `name`.
let private localHintsOf (source: string) =
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
    localInlayHints Unicode "" result |> List.map (fun hint -> hint.Name, hint.Type)

[<Fact>]
let ``One pattern takes the whole value, and several or named ones its fields`` () =
    let source =
        normalized """type Point = protected (x: int, y: int)

def f: Point -> int = { p -> match p
  | Point(whole) -> whole.x
}
def g: Point -> int = { p -> match p
  | Point(x, y) -> x + y
}
def h: Point -> int = { p -> match p
  | Point(y = b, ...) -> b
}
"""
    Assert.Equal<(string * string) list>(
        [ "p", "Point"; "whole", "(x: int, y: int)"; "p", "Point"; "x", "int"; "y", "int"; "p", "Point"; "b", "int" ],
        localHintsOf source)

[<Fact>]
let ``A type that names a record is taken apart by its name too`` () =
    let source =
        normalized """type Email = (address: string)

def f: Email -> string = { e -> match e
  | Email(address, ...) -> address
}
"""
    Assert.Equal<(string * string) list>([ "e", "Email"; "address", "string" ], localHintsOf source)

[<Fact>]
let ``Arms that take a value apart by its fields and by name together cover it`` () =
    let source =
        normalized """type Point = protected (x: int, y: int)

def describe: Point -> string = {
  | Point(x = 0, ...) -> "on the y axis"
  | Point(x, y) -> "at {x}, {y}"
}
"""
    compileClean source |> ignore

[<Fact>]
let ``A field by position on a record whose fields are named is a type mismatch`` () =
    // As in a record pattern, a pattern other than a field's name takes the field at its position, which a record
    // of named fields doesn't have.
    let source =
        normalized """type Point = protected (x: int, y: int)

def f: Point -> int = { Point(a, b) -> a + b }
"""
    let code, _ = singleError source
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
