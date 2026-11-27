module Nanyx.Compiler.Tests.Features.Types.UnionAliasNames

open Xunit
open NyxCompiler
open Parser.Program
open Nanyx.Compiler.Tests.TestSupport

// A union that holds every tag of a union alias in scope, with the same payloads, is written with the alias's name:
// `#some(string) | FsError`. The type is the same; only how it is written changes (#2339).

/// The type hover writes for the def `name` in `result`, compiled from `source` with no diagnostics.
let private hoveredIn (result: CompileResult) (source: string) (name: string) =
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    Hover.typeAt Unicode result (offsetOf source $"def {name}" 0 4)
    |> Option.map (fun hover -> hover.Type.Substring($"def {name}: ".Length))

let private hovered (source: string) (name: string) =
    let source = normalized source
    hoveredIn (Compiler.compile source) source name

/// `ty` written by the type printer with the type definitions of `source`, once it is compiled, in scope.
let private writtenIn (source: string) (ty: Ty) =
    let result = Compiler.compile (normalized source)
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    Unifier.tyToDisplayStringIn result.Typed.Value.TypeDefs Unicode ty

let private errTypes = """type Err = #notFound | #other(string)
"""

[<Fact>]
let ``The printer writes a union holding all of an alias's tags with its name`` () =
    let union =
        TyUnion [ TyTag("some", Some (TyPrimitive "int")); TyTag("notFound", None); TyTag("other", Some (TyPrimitive "string")) ]
    Assert.Equal("#some(int) | Err", writtenIn errTypes union)
    Assert.Equal("#some(int) | #notFound | #other(string)", Unifier.tyToDisplayString Unicode union)

[<Fact>]
let ``A union holding only some of an alias's tags is written out`` () =
    let partial = TyUnion [ TyTag("some", Some (TyPrimitive "int")); TyTag("notFound", None) ]
    Assert.Equal("#some(int) | #notFound", writtenIn errTypes partial)

[<Fact>]
let ``A tag with another payload is not the alias's`` () =
    let union = TyUnion [ TyTag("notFound", None); TyTag("other", Some (TyPrimitive "int")) ]
    Assert.Equal("#notFound | #other(int)", writtenIn errTypes union)

[<Fact>]
let ``Hover writes a def's result with the alias's name`` () =
    let source =
        errTypes
        + """def find: string -> #some(int) | Err = { name -> if name == "" then #notFound else #some(1) }
def first = { name: string -> find(name) }
"""
    Assert.Equal(Some "string -> #some(int) | Err", hovered source "find")
    Assert.Equal(Some "string -> #some(int) | Err", hovered source "first")

[<Fact>]
let ``The union an alias stands for is written by the alias's name`` () =
    let source =
        errTypes
        + """def fail: Err = #notFound
def failed = { x: int -> if x > 0 then #notFound else #other("no") }
"""
    Assert.Equal(Some "int -> Err", hovered source "failed")

[<Fact>]
let ``Of two aliases that hold the same tags, the one of more tags is written`` () =
    let source =
        normalized """type Small = #a | #b
type Large = #a | #b | #c
def all = { x: int -> if x > 1 then #a else if x > 0 then #b else #c }
def some = { x: int -> if x > 0 then #a else #b }
def more = { x: int -> if x > 1 then #a else if x > 0 then #b else #d }
"""
    Assert.Equal(Some "int -> Large", hovered source "all")
    Assert.Equal(Some "int -> Small", hovered source "some")
    Assert.Equal(Some "int -> Small | #d", hovered source "more")

[<Fact>]
let ``Of two aliases of the same tags, the shorter name is written, then the first by name`` () =
    let source =
        normalized """type Second = #a | #b
type First = #a | #b
type Third = #a | #b
type Fourth = #c | #d
type Fifth = #c | #d
def either = { x: int -> if x > 0 then #a else #b }
def other = { x: int -> if x > 0 then #c else #d }
"""
    Assert.Equal(Some "int -> First", hovered source "either")
    Assert.Equal(Some "int -> Fifth", hovered source "other")

[<Fact>]
let ``An alias imported under a module's alias is written as a type of it is`` () =
    let library = "module mod\nexport type Answer = #yes | #no\n"
    let source =
        normalized """module main

import mod as lib

def given: lib.Answer = #yes
def answer = { x: int -> if x > 0 then #yes else #no }
"""
    let result = compileImporting library source
    Assert.Equal(hoveredIn result source "given" |> Option.map (sprintf "int -> %s"), hoveredIn result source "answer")

[<Fact>]
let ``An alias of one tag does not rename it`` () =
    let source =
        normalized """type Done = #done
def finish = { _: int -> #done }
"""
    Assert.Equal(Some "int -> #done", hovered source "finish")

[<Fact>]
let ``A type's definition is not written with its own name`` () =
    let source = normalized (errTypes + "def fail: Err = #notFound\n")
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let hover = Definition.typeDefinitionAt Unicode "" result (offsetOf source "Err = #" 0 1)
    Assert.Equal(Some "type Err = #notFound | #other(string)", hover |> Option.map _.Type)

[<Fact>]
let ``A type mismatch writes a union with the alias's name`` () =
    let source =
        errTypes
        + """def find: int -> #some(int) | Err = { x -> #some(x) }
def take: (int -> #some(int) | #notFound) -> () = { _ -> () }
def main = take(find)
"""
    let code, message, _ = onlyDiagnostic source
    Assert.Equal(DiagnosticCodes.TypeMismatch.Code, code)
    Assert.Equal("'#some(int) | Err' vs '#notFound | #some(int)'", message)

[<Fact>]
let ``The info view writes a local's type with the alias's name`` () =
    let source =
        normalized (
            errTypes
            + """def find: int -> #some(int) | Err = { x -> #some(x) }
def main = {
  def found = find(1)
  found
}
"""
        )
    let result = Compiler.compile source
    Assert.True(result.Diagnostics.IsEmpty, $"Unexpected diagnostics: %A{result.Diagnostics}")
    let info = InfoView.at Unicode "" source result (offsetOf source "found\n}" 0 0)
    let found = info.Scopes |> List.collect _.Names |> List.find (fun name -> name.Name = "found")
    Assert.Equal(Some "#some(int) | Err", found.Type)
