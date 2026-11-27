module TranspilerWasmTests.Features.Types.RunMutuallyRecursiveTypes

open Xunit
open NyxCompiler
open Transpiler.Wasm.CoreIr
open TranspilerWasmTestHelpers

// Types that name each other, closed together as a group (#3186). Each program runs with its memory checked, so a value
// that is not freed, or freed twice, fails the test.

let private syntax = """
type Expr = (id: int, kind: #num(int) | #name(string) | #lambda(list(Pattern), Expr) | #block(list(Statement)))

type Pattern = (id: int, kind: #bind(string) | #wild | #guard(Expr) | #pair(Pattern, Pattern))

type Statement = (id: int, kind: #def(Pattern, Expr) | #expr(Expr))

def count: Expr -> int = { e ->
  match e.kind
    | #num(_) -> 1
    | #name(_) -> 1
    | #lambda(ps, body) -> 1 + list.sum(list.map(ps, countPattern)) + count(body)
    | #block(ss) -> 1 + list.sum(list.map(ss, countStatement))
}

def countPattern: Pattern -> int = { p ->
  match p.kind
    | #bind(_) -> 1
    | #wild -> 1
    | #guard(e) -> 1 + count(e)
    | #pair(a, b) -> 1 + countPattern(a) + countPattern(b)
}

def countStatement: Statement -> int = { s ->
  match s.kind
    | #def(p, e) -> 1 + countPattern(p) + count(e)
    | #expr(e) -> 1 + count(e)
}

def x: Expr = (id = 1, kind = #name("x"))

def p: Pattern = (id = 2, kind = #pair((id = 3, kind = #bind("a")), (id = 4, kind = #guard(x))))

def tree: Expr = (id = 5, kind = #block([(id = 6, kind = #def(p, x)), (id = 7, kind = #expr((id = 8, kind = #lambda([p], x))))]))
"""

[<Fact>]
let ``Run - a syntax tree of types that name each other is built, walked and freed`` () =
    runSource ((syntax + "println(count(tree))\n").ReplaceLineEndings "\n") |> assertRan "14\n"

[<Fact>]
let ``Run - values of types that name each other are compared, written and used as keys`` () =
    let source = syntax + """
println(tree == tree)
println(tree == x)
println(x)
def keyed = map.fromList([(x, "x"), (tree, "tree")])
println(map.get(keyed, tree))
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "true\nfalse\n(id = 1, kind = #name(x))\n#some(tree)\n"

[<Fact>]
let ``Run - generic aliases that name each other are mapped`` () =
    let source = """
type Tree(a) = #leaf(a) | #forest(Forest(a))

type Forest(a) = (trees: list(Tree(a)))

def labels: Tree(a), (a -> b) -> Tree(b) = { t, f ->
  match t
    | #leaf(x) -> #leaf(f(x))
    | #forest(forest) -> #forest((trees = list.map(forest.trees, { tree -> labels(tree, f) })))
}

def numbers: Tree(int) = #forest((trees = [#leaf(1), #forest((trees = [#leaf(2), #leaf(3)]))]))
println(labels(numbers, { n -> "<{n}>" }))
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "#forest((trees = [#leaf(<1>), #forest((trees = [#leaf(<2>), #leaf(<3>)]))]))\n"

[<Fact>]
let ``Run - a nominal type and an alias that name each other`` () =
    let source = """
type Node = protected (value: int, children: Children)

type Children = #none | #some(list(Node))

def total: Node -> int = { n ->
  match n.children
    | #none -> n.value
    | #some(ns) -> n.value + list.sum(list.map(ns, total))
}

println(total(Node(value = 1, children = #some([Node(value = 2, children = #none), Node(value = 3, children = #none)]))))
"""
    runSource (source.ReplaceLineEndings "\n") |> assertRan "6\n"

[<Fact>]
let ``Run - the descriptions of types that name each other are walked and built`` () =
    let source = """
type Expr = (id: int, body: #num(int) | #block(list(Statement)))

type Statement = (expr: Expr, label: string)

def describe: a, Type(a) -> string = { value, t ->
  match t
    | #int -> "{value}"
    | #string -> "'" ++ value ++ "'"
    | #list(elem) -> "[" ++ (value \list.map { v -> describe(v, elem) } \string.join(", ")) ++ "]"
    | #record(r) ->
      r.name ++ "(" ++ (r.fields \list.map { f -> f.name ++ " = " ++ describe(f.get(value), f.type) } \string.join(", ")) ++ ")"
    | #union(u) -> u.name ++ " of " ++ (u.cases \list.map { c -> c.name } \string.join(" or "))
    | else -> "?"
}

def zeroOf: Type(a) -> #some(a) | #error(string) = { t ->
  match t
    | #int -> #some(0)
    | #string -> #some("none")
    | #list(elem) -> #some([])
    | #record(r) -> r.build { f -> zeroOf(f.type) }
    | #union(u) ->
      match u.cases
        | [c, ..._] ->
          match zeroOf(c.type)
            | #some(payload) -> #some(c.build(payload))
            | #error(e) -> #error(e)
        | [] -> #error("a union with no cases")
    | else -> #error("no zero")
}

def e: Expr = (id = 1, body = #block([(expr = (id = 2, body = #num(3)), label = "x")]))
println(describe((expr = e, label = "top"), type Statement))
match zeroOf(type Statement)
  | #some(zero) -> println(zero)
  | #error(message) -> println(message)
"""
    runSource (source.ReplaceLineEndings "\n")
    |> assertRan "Statement(expr = Expr(id = 1, body =  of num or block), label = 'top')\n(expr = (body = #num(0), id = 0), label = none)\n"

/// The number of parts `ty` is made of, itself included.
let rec private irSize (ty: IrType) : int =
    1
    + match ty with
      | IrRecursive(_, bodies) -> bodies |> List.sumBy irSize
      | IrTags tags -> tags |> List.sumBy (snd >> Option.map irSize >> Option.defaultValue 0)
      | IrRecord fields -> fields |> List.sumBy (snd >> irSize)
      | IrList element
      | IrArray element -> irSize element
      | IrFunction(input, output, members, _) -> irSize input + irSize output + (members |> List.sumBy (snd >> irSize))
      | _ -> 0

// Each type of a group was lowered again inside each other one it holds, so a group's IR type grew with the number of
// paths through it (#3209).
[<Fact>]
let ``A group of types that each hold all the others lowers to an IR type the size of their definitions`` () =
    let names = [ "A"; "B"; "C"; "D"; "E"; "F"; "G"; "H" ]
    let definition name =
        let others = names |> List.filter ((<>) name) |> List.map (sprintf "list(%s)") |> String.concat ", "
        $"type {name} = #{name.ToLowerInvariant()} | #holds({others})"
    let source = (names |> List.map definition |> String.concat "\n\n") + "\n\ndef a: A = #a\n"
    let result = Compiler.compile source
    Assert.Empty(result.Diagnostics)
    let layouts: Transpiler.Wasm.ContextLayouts = { Plain = Map.empty; Parameterised = Map.empty; Waiting = Set.empty }
    let size = irSize (Transpiler.Wasm.CoreIrLowerTypes.toIrTypeIn layouts result.Typed.Value.Types.["a"])
    Assert.True(size < 200, $"The IR type of 'A' has {size} parts")
