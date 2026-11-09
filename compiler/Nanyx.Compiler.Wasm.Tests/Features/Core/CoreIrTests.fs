module TranspilerWasmTests.Features.Core.CoreIr

open System.Text.RegularExpressions
open Xunit
open NyxCompiler
open Parser.Program
open Transpiler.Wasm
open Transpiler.Wasm.CodeGen

let private typedModule (source: string) =
    let result = Compiler.compile source
    let errors = result.Diagnostics |> List.filter (fun d -> d.Severity = Severity.Error)
    Assert.True(errors.IsEmpty, $"Unexpected errors: %A{errors}")
    result.Typed.Value

let private compileWithReport (source: string) = transpileTypedModuleWithReport (typedModule source)

let private count (text: string) (wat: string) = Regex.Matches(wat, Regex.Escape text).Count

/// The diagnostic the backend gives `source`, which type-checks but which it cannot compile.
let private backendError (source: string) =
    match tryTranspileTypedModuleToWat (typedModule source) with
    | Ok _ -> failwith "Expected the backend to fail"
    | Error diagnostic ->
        Assert.Equal(DiagnosticCodes.UnsupportedByBackend.Code, diagnostic.Code)
        diagnostic.Message

[<Fact>]
let ``Every function goes through the core IR`` () =
    let source =
        "def add = { x, y -> x + y }\n" +
        "\n" +
        "def classify = { n ->\n" +
        "  def doubled = add(n, n)\n" +
        "  if doubled > 10 then #big(doubled) else #small\n" +
        "}\n" +
        "\n" +
        "def size = { n ->\n" +
        "  match classify(n)\n" +
        "    | #big(d) -> d\n" +
        "    | #small -> 0\n" +
        "}\n"
    let _, report = compileWithReport source
    Assert.Equal<string list>([ "add"; "classify"; "size" ], report.Functions)

[<Fact>]
let ``Comparing or printing a record is a compile error`` () =
    // `==` is built in only for numbers, strings and tags, and `dbg` prints no record.
    Assert.Equal(
        "The WebAssembly backend cannot compile `dbg` of a value of type (0: int, 1: int) yet",
        backendError "def shout: (int, int) -> (int, int) = { t -> dbg(t) }\n")
    Assert.Equal(
        "The WebAssembly backend cannot compile the operator == on a value of type (a: int) yet",
        backendError "def same: (a: int), (a: int) -> bool = { x, y -> x == y }\n")
    Assert.Equal(
        "The WebAssembly backend cannot compile the operator == on a value of type list(int) yet",
        backendError "def same: list(int), list(int) -> bool = { x, y -> x == y }\n")

[<Fact>]
let ``A generic function is emitted once per instantiation`` () =
    let source =
        "def id = { x -> x }\n" +
        "\n" +
        "def main = {\n" +
        "  def a = id(1)\n" +
        "  def b = id(a + 1)\n" +
        "  def s = id(\"text\")\n" +
        "  def t = id(#ok)\n" +
        "  b\n" +
        "}\n"
    let wat, report = compileWithReport source
    Assert.Equal<string list>([ "id__int"; "id__string"; "id__tags_ok"; "main" ], report.Functions)
    Assert.Equal(1, count "(func $id__int " wat)
    Assert.Equal(1, count "(func $id__string " wat)
    Assert.Equal(1, count "(func $id__tags_ok " wat)
    Assert.Equal(2, count "call $id__int\n" wat)
    // A generic definition exists only as its specialisations.
    Assert.Equal(0, count "(func $id " wat)
    Assert.Equal(0, count "(export \"id\"" wat)

[<Fact>]
let ``A specialisation specialises the generic functions it calls`` () =
    let source =
        "def wrap = { x -> #some(x) }\n" +
        "\n" +
        "def orElse = { o, fallback ->\n" +
        "  match o\n" +
        "    | #some(v) -> v\n" +
        "    | #none -> fallback\n" +
        "}\n" +
        "\n" +
        "def roundTrip = { x, fallback -> orElse(wrap(x), fallback) }\n" +
        "\n" +
        "def main = {\n" +
        "  def n = roundTrip(1, 2)\n" +
        "  def s = roundTrip(\"a\", \"b\")\n" +
        "  n\n" +
        "}\n"
    let wat, report = compileWithReport source
    for name in [ "wrap__int"; "wrap__string"; "orElse__int"; "orElse__string"; "roundTrip__int"; "roundTrip__string" ] do
        Assert.Contains(name, report.Functions)
        Assert.Equal(1, count $"(func ${name} " wat)
    Assert.Contains("call $wrap__string\n    local.get $fallback\n    return_call $orElse__string", wat)

[<Fact>]
let ``Records, tuples and nominal values go through the core IR`` () =
    let source =
        "type Person = (name: string, age: int)\n" +
        "\n" +
        "type $Money = private int\n" +
        "\n" +
        "def make = { n -> Person(name = \"Ada\", age = n) }\n" +
        "\n" +
        "def older: Person -> Person = { p -> p with age = p.age + 1 }\n" +
        "\n" +
        "def swapped: (int, int) -> (int, int) = { t ->\n" +
        "  match t\n" +
        "    | (a, b) -> (b, a)\n" +
        "}\n" +
        "\n" +
        "def pence: $Money -> int = { m ->\n" +
        "  def $Money(p) = m\n" +
        "  p\n" +
        "}\n" +
        "\n" +
        "def add: (int, int) -> int = { a, b -> a + b }\n" +
        "\n" +
        "def stepped: (int, step: int) -> int = { a, step -> a * 10 + step }\n" +
        "\n" +
        "def main = {\n" +
        "  def pair = (1, 2)\n" +
        "  def named = stepped(1, step = 2)\n" +
        "  older(make(3)).age + pence($Money(250)) + add(pair) + named + swapped(pair).0\n" +
        "}\n"
    let _, report = compileWithReport source
    Assert.Equal<string list>([ "make"; "older"; "swapped"; "pence"; "add"; "stepped"; "main" ], report.Functions)

[<Fact>]
let ``Strings, lists and intrinsics go through the core IR`` () =
    let source =
        "def greet: string -> string = { name -> \"Hello, \" ++ name }\n" +
        "\n" +
        "def differ: (string, string) -> bool = { a, b -> a != b }\n" +
        "\n" +
        "def isNone = { o -> o == #none }\n" +
        "\n" +
        "def heads: list(int) -> int = { xs ->\n" +
        "  match xs\n" +
        "    | [] -> 0\n" +
        "    | [a] -> a\n" +
        "    | [a, b, ...rest] -> a + b\n" +
        "}\n" +
        "\n" +
        "def word = { s ->\n" +
        "  match s\n" +
        "    | \"yes\" -> 1\n" +
        "    | _ -> 0\n" +
        "}\n" +
        "\n" +
        "def size: string -> int = { s -> s.length }\n" +
        "\n" +
        "def main = heads([1, 2] ++ [3]) + word(\"yes\") + size(greet(\"Ada\"))\n"
    let wat, report = compileWithReport source
    Assert.Equal<string list>([ "greet"; "differ"; "isNone"; "heads"; "word"; "size"; "main" ], report.Functions)
    Assert.Contains("call $__nanyx_string_equals\n    i32.eqz", wat)
    Assert.Contains("call $__nanyx_list_concat", wat)
    Assert.Contains("call $__nanyx_tag_equals", wat)

[<Fact>]
let ``Interpolated strings and dbg go through the core IR`` () =
    let source =
        "def pick = { n -> if n > 0 then #some(\"yes\") else #none }\n" +
        "\n" +
        "def describe: int -> string = { n -> \"n is {n}, positive {n > 0}, pick {pick(n)}\" }\n" +
        "\n" +
        "def nested: string -> string = { s -> \"outer {\"inner {s}\"} end\" }\n" +
        "\n" +
        "def debugs: int -> int = { n ->\n" +
        "  dbg(n)\n" +
        "  dbg(\"text\")\n" +
        "  dbg(#ok)\n" +
        "  dbg(#some(n))\n" +
        "  dbg(pick(n))\n" +
        "  n\n" +
        "}\n"
    let wat, report = compileWithReport source
    Assert.Equal<string list>([ "pick"; "describe"; "nested"; "debugs" ], report.Functions)
    // A string inside another's hole keeps to its own hole locals.
    Assert.Contains("local.set $__interp_hole_1_0", wat)
    Assert.Contains("call $dbg_tag_payload_some", wat)

[<Fact>]
let ``Lambdas, closures and function values go through the core IR`` () =
    let source =
        "def apply = { f, x -> f(x) }\n" +
        "\n" +
        "def double: int -> int = { n -> n * 2 }\n" +
        "\n" +
        "def compose = { f, g -> { x -> g(f(x)) } }\n" +
        "\n" +
        "def countdown: int -> int = { n ->\n" +
        "  rec loop = { i, acc -> if i == 0 then acc else loop(i - 1, acc + i) }\n" +
        "  loop(n, 0)\n" +
        "}\n" +
        "\n" +
        "def main = {\n" +
        "  def offset = 10\n" +
        "  def both = compose(double, { x -> x + offset })\n" +
        "  apply({ x -> x + offset }, 1) + apply(double, 4) + both(1) + countdown(4)\n" +
        "}\n"
    let wat, report = compileWithReport source
    // A lambda written in a generic function is specialised with it; one written in `main` keeps its own name.
    for name in [ "compose__int__int__int"; "__lambda_0__int__int__int"; "__rec_loop_1"; "__lambda_2"; "__fn_double"; "main" ] do
        Assert.Contains(name, report.Functions)
    // Calls through function values, and closure blocks holding a table slot and what they capture.
    Assert.Contains("call_indirect (type $ctx_fn_2)", wat)
    Assert.Contains("call $__nanyx_alloc", wat)

[<Fact>]
let ``Functions that require contexts, and their callers, go through the core IR`` () =
    let source =
        "context Ask = (ask: () -> int)\n" +
        "\n" +
        "context Show(a) = (show: a -> string)\n" +
        "\n" +
        "type Point = (x: int, y: int)\n" +
        "\n" +
        "def Point.show: Point -> string = { p -> \"point\" }\n" +
        "\n" +
        "def twice: [Ask] () -> int = { ask() + ask() }\n" +
        "\n" +
        "def plain: [Show(a)] a -> string = { value -> show(value) }\n" +
        "\n" +
        "def main = {\n" +
        "  use Ask(ask = { 21 })\n" +
        "  def p = Point(x = 1, y = 2)\n" +
        "  def s = plain(p)\n" +
        "  twice()\n" +
        "}\n"
    let wat, report = compileWithReport source
    // `plain` is specialised for `Point`, with the instance built from `Point.show` passed as its member.
    for name in [ "twice"; "plain__rec_x_int_y_int"; "main"; "__fn_Point_show" ] do
        Assert.Contains(name, report.Functions)
    // Each member arrives in a parameter of its own, after the function's own parameters.
    Assert.Contains("(func $twice (param $__ctx_ask i32) (result i32)", wat)

[<Fact>]
let ``Floats, pipes, guards and for loops go through the core IR`` () =
    let source =
        "def area: float -> float = { r -> r * r * 3.14 }\n" +
        "\n" +
        "def size = { n ->\n" +
        "  match n\n" +
        "    | > 100 -> 2\n" +
        "    | < 0 -> 0\n" +
        "    | _ -> 1\n" +
        "}\n" +
        "\n" +
        "def double: int -> int = { n -> n * 2 }\n" +
        "\n" +
        "def total: list(int) -> int = { xs ->\n" +
        "  for xs | n -> size(n \\double)\n" +
        "  for 1..3 | i -> dbg(area(1.5))\n" +
        "  0\n" +
        "}\n"
    let wat, report = compileWithReport source
    Assert.Equal<string list>([ "area"; "size"; "double"; "total" ], report.Functions)
    Assert.Contains("loop $__for_next_0", wat)
    Assert.Contains("f64.mul\n    call $__nanyx_float_box", wat)

[<Fact>]
let ``The start function and errors deferred to run time go through the core IR`` () =
    // A module with a type error is still compiled: the expression that failed writes the error and stops the program
    // when it is reached.
    let source =
        "def broken: int -> string = { n -> n ++ \"x\" }\n" +
        "\n" +
        "def fine: int -> int = { n -> n + 1 }\n" +
        "\n" +
        "dbg(fine(1))\n" +
        "dbg(broken(2))\n"
    let result = Compiler.compile source
    let wat, report = transpileTypedModuleWithReport result.Typed.Value
    Assert.Equal<string list>([ "broken"; "fine"; "__nanyx_start" ], report.Functions)
    Assert.Contains("call $proc_exit\n    unreachable", wat)
    Assert.Contains("(start $__nanyx_start)", wat)

[<Fact>]
let ``A generic function imported from another module is specialised where it is called`` () =
    // `List.map` comes from the prelude, inlined into the module with the types its own module gave its nodes.
    let source =
        "def doubled: list(int) -> list(int) = { xs -> List.map(xs, { x -> x * 2 }) }\n" +
        "\n" +
        "def positives: list(int) -> list(bool) = { xs -> List.map(xs, { x -> x > 0 }) }\n"
    let _, report = compileWithReport source
    let mapSpecialisations = report.Specialisations |> List.filter (snd >> (=) "List_map") |> List.map fst
    Assert.Equal<string list>([ "List_map__int__int"; "List_map__bool__int" ], mapSpecialisations)
    Assert.Contains("doubled", report.Functions)
    Assert.Contains("positives", report.Functions)

[<Fact>]
let ``A generic function over an open union, and a list's length, go through the core IR`` () =
    // `Option.map` takes any union with `#some`: its rest is the tags of the union it is called with. `xs.length` is a
    // call to `List.length` the type checker recorded as a property.
    let source =
        "def bump: #some(int) | #none -> #some(int) | #none = { o -> Option.map(o, { v -> v + 1 }) }\n" +
        "\n" +
        "def count: list(int) -> int = { xs -> xs.length }\n"
    let _, report = compileWithReport source
    Assert.Contains("bump", report.Functions)
    Assert.Contains("count", report.Functions)
    Assert.Contains("Option_map", report.Specialisations |> List.map snd)
    Assert.Contains("List_length", report.Specialisations |> List.map snd)

[<Fact>]
let ``A type variable that nothing constrains is unit in the core IR`` () =
    // The element of `[]` is fixed by nothing: no value of it can be made or inspected.
    let source = "def none: () -> int = { List.length([]) }\n"
    let _, report = compileWithReport source
    Assert.Contains("none", report.Functions)
    Assert.Contains("List_length__unit", report.Functions)

[<Fact>]
let ``A guard compares to what any expression computes, in the core IR`` () =
    let source =
        "def limit = 10\n" +
        "\n" +
        "def size: int -> int = { n ->\n" +
        "  match n\n" +
        "    | { >= limit * 2 } -> 2\n" +
        "    | { >= limit } -> 1\n" +
        "    | else -> 0\n" +
        "}\n"
    let wat, report = compileWithReport source
    Assert.Contains("size", report.Functions)
    Assert.Contains("call $limit\n    i32.const 2\n    i32.mul\n      i32.ge_s", wat)

[<Fact>]
let ``A function that reads a field of any record is specialised for each layout it is given`` () =
    let source =
        "def nOf = { r -> r.n }\n" +
        "\n" +
        "def main = {\n" +
        "  def small = (n = 1)\n" +
        "  nOf(small) + nOf((a = 2, n = 3)) + nOf((a = 4, b = 5, n = 6)) + nOf((n = 7))\n" +
        "}\n"
    let wat, report = compileWithReport source
    // One specialisation per layout, named by the record's other fields.
    Assert.Equal<string list>([ "nOf__int__rec_"; "nOf__int__rec_a_int"; "nOf__int__rec_a_int_b_int"; "main" ], report.Functions)
    // Each reads `n` from where its layout puts it, rather than looking the field up at run time.
    for name, offset in [ "nOf__int__rec_", 0; "nOf__int__rec_a_int", 4; "nOf__int__rec_a_int_b_int", 8 ] do
        Assert.Contains($"(func ${name} (param $r i32) (result i32)\n    local.get $r\n    i32.const {offset}\n    i32.add\n    i32.load\n  )", wat)
    let specialisations = wat.Substring(wat.IndexOf "(func $nOf__")
    Assert.DoesNotContain("call $__nanyx_open_field", specialisations)

[<Fact>]
let ``Every intermediate value in the core IR is named`` () =
    let typed = typedModule "def f = { x, y -> (x + 1) * (y - 2) }\n"
    let input: CoreIrLower.LowerInput =
        { Definitions =
            typed.Module |> List.choose (function Kind (Def (ValueDef(_, name, _, expr))) -> Some (name, expr) | _ -> None)
          ExprTypes = typed.ExprTypes
          CalleeTypes = typed.CalleeTypes
          DefinitionTypes = typed.Types
          ContextParameters = Map.empty
          ContextArguments = fun _ _ _ -> Ok []
          RecordConstructors = Set.empty
          NominalWrappers = Set.empty
          Lifted = Map.empty
          TopLevel = []
          NamedTypes = Map.empty }
    let lowered = CoreIrLower.lowerModule input id
    let func, _ = lowered.Roots.["f"]
    let printed = CoreIr.Print.func func
    let expected =
        "fn f(x: int, y: int): int =\n" +
        "  let __t3: int = x + 1\n" +
        "  let __t4: int = y - 2\n" +
        "  __t3 * __t4"
    Assert.Equal(expected, printed)
