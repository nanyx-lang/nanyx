module TranspilerWasmTests.Features.Memory.DropFunctions

open Xunit
open Transpiler.Wasm.CoreIr
open TranspilerWasmTestHelpers

// Each test calls a function the program exports, and gives up what it returns with the drop function of its type.

/// Builds a value with `make`, drops it, and builds it again: when the drop freed every object, the second build takes
/// all its memory from the free lists and the heap does not grow.
let private assertDropFreesEverything (program: WasmProgram) (make: string) (ty: IrType) =
    program.Drop(ty, program.Call(make, 20))
    let heapAfterFirst = program.HeapPointer
    program.Drop(ty, program.Call(make, 20))
    Assert.Equal(heapAfterFirst, program.HeapPointer)

[<Fact>]
let ``Drop - a list of strings frees every cell and every string`` () =
    let source = withFinalNewline """def build: int -> list(string) = { n -> ["a {n}", "b {n}", "c {n}"] }"""
    use program = instantiateWasm source [ IrList IrString ]
    assertDropFreesEverything program "build" (IrList IrString)

[<Fact>]
let ``Drop - a record frees the strings in its fields`` () =
    let source = withFinalNewline """def pair: int -> (string, int) = { n -> ("pair {n}", n) }"""
    let ty = IrRecord [ "0", IrString; "1", IrInt ]
    use program = instantiateWasm source [ ty ]
    assertDropFreesEverything program "pair" ty

[<Fact>]
let ``Drop - a tag's cell is freed with its payload, and a tag without one is left alone`` () =
    let source =
        normalized """def wrap: int -> #some(string) | #none = { n ->
  match n
  | 0 -> #none
  | _ -> #some("wrapped {n}")
}
"""
    let ty = IrTags [ "none", None; "some", Some IrString ]
    use program = instantiateWasm source [ ty ]
    assertDropFreesEverything program "wrap" ty
    program.Drop(ty, program.Call("wrap", 0))

[<Fact>]
let ``Drop - a tag's payload is dropped by the type its code gives it`` () =
    let source =
        normalized """def describe: int -> #name(string) | #age(int) = { n ->
  match n
  | 0 -> #age(n)
  | _ -> #name("name {n}")
}
"""
    let ty = IrTags [ "age", Some IrInt; "name", Some IrString ]
    use program = instantiateWasm source [ ty ]
    assertDropFreesEverything program "describe" ty
    let heap = program.HeapPointer
    program.Drop(ty, program.Call("describe", 0))
    program.Drop(ty, program.Call("describe", 0))
    Assert.Equal(heap, program.HeapPointer)

[<Fact>]
let ``Drop - a value of a recursive alias frees every node it holds`` () =
    let source = """
type Tree = #leaf | #node(Tree, string, Tree)

def build: int -> Tree = { n -> if n == 0 then #leaf else #node(build(n - 1), "node {n}", build(n // 2)) }
"""
    let ty = IrRecursive(0, [ IrTags [ "leaf", None; "node", Some(IrRecord [ "0", IrSelf(0, 0); "1", IrString; "2", IrSelf(0, 0) ]) ] ])
    use program = instantiateWasm (source.ReplaceLineEndings "\n") [ ty ]
    assertDropFreesEverything program "build" ty

[<Fact>]
let ``Drop - a value of a recursive type nested 100,000 deep takes no stack to drop, and frees every node`` () =
    let source = """
type Chain = #end | #link(string, Chain)

def grow: int, Chain -> Chain = { n, chain -> if n == 0 then chain else grow(n - 1, #link("link {n}", chain)) }

def build: int -> Chain = { n -> grow(n, #end) }
"""
    let ty = IrRecursive(0, [ IrTags [ "end", None; "link", Some(IrRecord [ "0", IrString; "1", IrSelf(0, 0) ]) ] ])
    use program = instantiateWasm (source.ReplaceLineEndings "\n") [ ty ]
    let live = program.LiveBlocks
    program.Drop(ty, program.Call("build", 100000))
    Assert.Equal(live, program.LiveBlocks)

[<Fact>]
let ``Run - a value nested 100,000 deep through a list and a record is dropped, and memory balances`` () =
    let source = """
type Rose = #rose(int, list(Rose))

def grow: int, Rose -> Rose = { n, rose -> if n == 0 then rose else grow(n - 1, #rose(n, [rose])) }

def depth: int, Rose -> int = { d, rose ->
  match rose
  | #rose(_, [inner]) -> depth(d + 1, inner)
  | _ -> d
}

@entry
def main = {
  def rose = grow(100000, #rose(0, []))
  println(depth(0, rose))
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "100000\n"

[<Fact>]
let ``Drop - a chain of closures that capture closures, 100,000 deep, takes no stack to drop, and frees every one`` () =
    let source = """
def wrap: int, (int -> int) -> (int -> int) = { n, f -> if n == 0 then f else wrap(n - 1, { x -> f(x) }) }

def build: int -> (int -> int) = { n ->
  def step = n % 7
  wrap(n, { x -> x + step })
}
"""
    let ty = IrFunction(IrInt, IrInt, [], false)
    use program = instantiateWasm (source.ReplaceLineEndings "\n") [ ty ]
    let live = program.LiveBlocks
    program.Drop(ty, program.Call("build", 100000))
    Assert.Equal(live, program.LiveBlocks)

[<Fact>]
let ``Run - a chain of closures 100,000 deep is dropped, and memory balances`` () =
    let source = """
def wrap: int, (int -> int) -> (int -> int) = { n, f -> if n == 0 then f else wrap(n - 1, { x -> f(x) }) }

@entry
def main = {
  def f = wrap(100000, { x -> x + 1 })
  println("built")
}
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "built\n"

[<Fact>]
let ``Drop - a closure frees what it captures`` () =
    let source =
        normalized """def prefixer: int -> (string -> string) = { n ->
  def prefix = "{n}: "
  { s -> prefix ++ s }
}
"""
    let ty = IrFunction(IrString, IrString, [], false)
    use program = instantiateWasm source [ ty ]
    assertDropFreesEverything program "prefixer" ty

[<Fact>]
let ``Drop - a string literal is never freed`` () =
    let source = withFinalNewline """def greeting: int -> string = { _n -> "hello" }"""
    use program = instantiateWasm source [ IrString ]
    let literal = program.Call("greeting", 0)
    program.Drop(IrString, literal)
    program.Drop(IrString, literal)
    Assert.Equal(0, program.Word(literal - 8))
    Assert.Equal("hello", program.Text literal)
