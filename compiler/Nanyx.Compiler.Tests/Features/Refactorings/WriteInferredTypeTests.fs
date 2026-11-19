module Nanyx.Compiler.Tests.Features.Refactorings.WriteInferredType

open System.IO
open Xunit
open NyxCompiler
open NyxCompiler.Refactorings
open Nanyx.Compiler.Tests.TestSupport

// Write the inferred type: a def or a lambda parameter that does not write its type gets the one inferred for it.

/// The refactoring at `$$` in `marked`, the module compiled as `main.nyx` beside `modules`, each a file's name and
/// text, and what compiling the text after it gives, if it is available.
let private offerAt (modules: (string * string) list) (marked: string) =
    let source, offset = unmarked marked
    withTempDir (fun dir ->
        for fileName, text in modules do
            File.WriteAllText(Path.Combine(dir, fileName), normalized text)
        let main = Path.Combine(dir, "main.nyx")
        File.WriteAllText(main, source)
        let result = Compiler.compileFile main
        Assert.True(result.Diagnostics.IsEmpty, sprintf "%A" result.Diagnostics)
        match WriteInferredType.at main source result offset with
        | Some (Available refactoring) ->
            let after = applyEdits source refactoring.Edits
            File.WriteAllText(main, after)
            Some (Available refactoring), Some (after, Compiler.compileFile main)
        | offer -> offer, None)

/// The text after the refactoring at `$$`, checked to compile as it did before.
let private rewrittenWith (modules: (string * string) list) (marked: string) =
    match offerAt modules marked with
    | _, Some (after, result) ->
        Assert.True(result.Diagnostics.IsEmpty, sprintf "%s\n%A" after result.Diagnostics)
        after
    | offer, None -> failwithf "expected the refactoring, got %A" offer

let private rewritten (marked: string) = rewrittenWith [] marked

/// A module that exports a nominal type, an alias and a context, and a def of each.
let private shapes =
    """module shapes
export type Circle = protected (radius: int)
export type Size = (width: int, height: int)
export context Draw(a) = (draw: a -> string)
export def unit: Circle = Circle(radius = 1)
export def square: Size = (width = 1, height = 1)
export def drawn: [Draw(a)] a -> string = { value -> Draw(a).draw(value) }
"""

[<Fact>]
let ``A generic def gets its type with type variables as source names them`` () =
    Assert.Equal("def id: a -> a = { x -> x }\n", rewritten "def id$$ = { x -> x }\n")

[<Fact>]
let ``A def that requires a context gets the context before its type`` () =
    Assert.Equal("def add: [Add(a)] a, a -> a = { x, y -> x + y }\n", rewritten "def add$$ = { x, y -> x + y }\n")

[<Fact>]
let ``A def that writes only its contexts gets the rest of its type after them`` () =
    Assert.Equal(
        """context Log = (log: string -> ())
def greet: [Log] string -> () = { name -> log(name) }
""",
        rewritten
            """context Log = (log: string -> ())
def greet$$: [Log] = { name -> log(name) }
""")

[<Fact>]
let ``A type alias keeps its name`` () =
    let after =
        rewritten
            """type Point = (x: int, y: int)
def origin: () -> Point = { () -> (x = 0, y = 0) }
def start$$ = origin()
"""
    Assert.Contains("def start: Point = origin()", after)

[<Fact>]
let ``An imported type is written as the file names it`` () =
    let after =
        rewrittenWith
            [ "shapes.nyx", shapes ]
            """import shapes
def circle$$ = unit
"""
    Assert.Contains("def circle: Circle = unit", after)

[<Fact>]
let ``A type imported under an alias is written through the alias`` () =
    let after =
        rewrittenWith
            [ "shapes.nyx", shapes ]
            """import shapes as s
def circle$$ = s.unit
"""
    Assert.Contains("def circle: s.Circle = s.unit", after)

[<Fact>]
let ``An alias imported under an alias is written through the alias`` () =
    let after =
        rewrittenWith
            [ "shapes.nyx", shapes ]
            """import shapes as s
def size$$ = s.square
"""
    Assert.Contains("def size: s.Size = s.square", after)

[<Fact>]
let ``A context imported under an alias is written through the alias`` () =
    let after =
        rewrittenWith
            [ "shapes.nyx", shapes ]
            """import shapes as s
def show$$ = { value -> s.drawn(value) }
"""
    Assert.Contains("def show: [s.Draw(a)] a -> string = ", after)

[<Fact>]
let ``A local def gets its type`` () =
    let after =
        rewritten
            """def area = { s: int ->
  def side$$ = s * 2
  side * side
}
"""
    Assert.Contains("def side: int = s * 2", after)

[<Fact>]
let ``A lambda's parameter gets its type`` () =
    Assert.Equal("def toInt = { b: bool -> if b then 1 else 0 }\n", rewritten "def toInt = { b$$ -> if b then 1 else 0 }\n")

[<Fact>]
let ``A lambda's parameter that is a function gets its type bracketed`` () =
    Assert.Equal("def apply = { f: ([c] int -> a) -> f(1) }\n", rewritten "def apply = { f$$ -> f(1) }\n")

[<Fact>]
let ``A pattern variable's type can't be written`` () =
    match offerAt [] "def first = { o -> match o\n  | #some(n$$) -> n\n  | #none -> 0 }\n" with
    | Some (Refused(_, RefactoringKind.Rewrite, reason)), _ -> Assert.Equal("A pattern variable's type can't be written", reason)
    | offer, _ -> failwithf "expected a refusal, got %A" offer

[<Fact>]
let ``Nothing is offered on a def that writes its type`` () =
    Assert.True((fst (offerAt [] "def one$$: int = 1\n")).IsNone)

[<Fact>]
let ``A type that requires a scoped context at two scopes can't be written`` () =
    let marked = """def run2$$ = { f, g ->
  def a = memory { f() }
  memory { g() }
}
"""
    match offerAt [] marked with
    | Some (Refused(_, RefactoringKind.Rewrite, reason)), _ ->
        Assert.Equal("Its type requires a scoped context at two scopes, which a type can't be written with yet", reason)
    | offer, _ -> failwithf "expected a refusal, got %A" offer

[<Fact>]
let ``A def that requires a context with an associated type gets it written by name`` () =
    let after =
        rewritten
            """context Iter(c) = (elem: type, next: c -> #some(elem, c) | #done)

def headOr: [Iter(c)] c, elem -> elem = { items, fallback ->
  match next(items)
    | #some(value, _) -> value
    | #done -> fallback
}

def firstOr$$ = { items, fallback -> headOr(items, fallback) }
"""
    Assert.Contains("def firstOr: [Iter(a, elem = b)] a, b -> b = { items, fallback -> headOr(items, fallback) }", after)
