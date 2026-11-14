module TranspilerWasmTests.Features.Core.CoreIr

open System.Text.RegularExpressions
open Xunit
open NyxCompiler
open Parser.Program
open Transpiler.Wasm
open Transpiler.Wasm.CodeGen
open TranspilerWasmTestHelpers

let private typedModule (source: string) =
    let result = Compiler.compile source
    let errors = result.Diagnostics |> List.filter (fun d -> d.Severity = Severity.Error)
    Assert.True(errors.IsEmpty, $"Unexpected errors: %A{errors}")
    result.Typed.Value

let private compileWithReport (source: string) = transpileTypedModuleWithReport (typedModule source)

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
        normalized """def add: int, int -> int = { x, y -> x + y }

def classify = { n ->
  def doubled = add(n, n)
  if doubled > 10 then #big(doubled) else #small
}

def size = { n ->
  match classify(n)
    | #big(d) -> d
    | #small -> 0
}
"""
    let _, report = compileWithReport source
    Assert.Equal<string list>([ "add"; "classify"; "size" ], report.Functions)

[<Fact>]
let ``A type that holds itself at other type arguments is a compile error`` () =
    let source =
        """
type Nest(a) = #leaf(a) | #deeper(Nest(list(a)))

def depth: Nest(int) -> int = { t -> 1 }
"""
    Assert.Equal(
        "The WebAssembly backend cannot compile a recursive type that holds itself at other type arguments yet",
        backendError (source.ReplaceLineEndings "\n"))

/// The functions in `report` but the printers `dbg` prints through.
let private withoutPrinters (functions: string list) = functions |> List.filter (fun name -> not (name.StartsWith "__dbg_"))

[<Fact>]
let ``A generic function is emitted once per instantiation`` () =
    let source =
        normalized """def id = { x -> x }

def main = {
  def a = id(1)
  def b = id(a + 1)
  def s = id("text")
  def t = id(#ok)
  b
}
"""
    let wat, report = compileWithReport source
    Assert.Equal<string list>([ "id__int"; "id__string"; "id__tags_ok"; "main" ], report.Functions)
    Assert.Equal(1, countOccurrences "(func $id__int " wat)
    Assert.Equal(1, countOccurrences "(func $id__string " wat)
    Assert.Equal(1, countOccurrences "(func $id__tags_ok " wat)
    Assert.Equal(2, countOccurrences "call $id__int\n" wat)
    // A generic definition exists only as its specialisations.
    Assert.Equal(0, countOccurrences "(func $id " wat)
    Assert.Equal(0, countOccurrences "(export \"id\"" wat)

[<Fact>]
let ``A specialisation specialises the generic functions it calls`` () =
    let source =
        normalized """def wrap = { x -> #some(x) }

def orElse = { o, fallback ->
  match o
    | #some(v) -> v
    | #none -> fallback
}

def roundTrip = { x, fallback -> orElse(wrap(x), fallback) }

def main = {
  def n = roundTrip(1, 2)
  def s = roundTrip("a", "b")
  n
}
"""
    let wat, report = compileWithReport source
    for name in [ "wrap__int"; "wrap__string"; "orElse__int"; "orElse__string"; "roundTrip__int"; "roundTrip__string" ] do
        Assert.Contains(name, report.Functions)
        Assert.Equal(1, countOccurrences $"(func ${name} " wat)
    Assert.Contains("call $wrap__string\n    local.get $fallback\n    return_call $orElse__string", wat)

[<Fact>]
let ``Records, tuples and nominal values go through the core IR`` () =
    let source =
        normalized """type Person = (name: string, age: int)

type Money = private int

def make = { n -> Person(name = "Ada", age = n) }

def older: Person -> Person = { p -> p with age = p.age + 1 }

def swapped: (int, int) -> (int, int) = { t ->
  match t
    | (a, b) -> (b, a)
}

def pence: Money -> int = { m ->
  def Money(p) = m
  p
}

def add: (int, int) -> int = { a, b -> a + b }

def stepped: (int, step: int) -> int = { a, step -> a * 10 + step }

def main = {
  def pair = (1, 2)
  def named = stepped(1, step = 2)
  older(make(3)).age + pence(Money(250)) + add(pair) + named + swapped(pair).0
}
"""
    let _, report = compileWithReport source
    Assert.Equal<string list>([ "make"; "older"; "swapped"; "pence"; "add"; "stepped"; "main" ], report.Functions)

[<Fact>]
let ``Strings, lists and intrinsics go through the core IR`` () =
    // In `main`, a list literal in front of `++` is put in front of the rest cell by cell; a list that is not one is
    // copied.
    let source =
        normalized """def greet: string -> string = { name -> "Hello, " ++ name }

def differ: (string, string) -> bool = { a, b -> a != b }

def isNone: #none -> bool = { o -> o == #none }

def heads: list(int) -> int = { xs ->
  match xs
    | [] -> 0
    | [a] -> a
    | [a, b, ...rest] -> a + b
}

def word = { s ->
  match s
    | "yes" -> 1
    | _ -> 0
}

def size: string -> int = { s -> s.length }

def main = heads(([1, 2] ++ []) ++ [3]) + word("yes") + size(greet("Ada"))
"""
    let wat, report = compileWithReport source
    Assert.Equal<string list>([ "greet"; "differ"; "isNone"; "heads"; "word"; "size"; "main" ], report.Functions)
    Assert.Contains("call $__nanyx_string_equals\n    i32.eqz", wat)
    Assert.Contains("call $__nanyx_list_concat", wat)
    Assert.Contains("call $__nanyx_equals_tags_none", wat)

[<Fact>]
let ``Interpolated strings and dbg go through the core IR`` () =
    let source =
        normalized """def pick = { n -> if n > 0 then #yes else #no }

def describe: int -> string = { n -> "n is {n}, positive {n > 0}, pick {pick(n)}" }

def nested: string -> string = { s -> "outer {"inner {s}"} end" }

def debugs: int -> int = { n ->
  dbg(n)
  dbg("text")
  dbg(#ok)
  dbg(#some(n))
  dbg(pick(n))
  n
}
"""
    let wat, report = compileWithReport source
    Assert.Equal<string list>([ "pick"; "describe"; "nested"; "debugs" ], withoutPrinters report.Functions)
    // A string inside another's hole keeps to its own hole locals.
    Assert.Contains("local.set $__interp_hole_1_0", wat)
    // Each value is printed through the printer of its type.
    for printer in [ "__dbg_show_int"; "__dbg_show_string"; "__dbg_show_tag_ok"; "__dbg_show_tag_some_int"; "__dbg_show_tags_yes_no" ] do
        Assert.Contains(printer, report.Functions)

[<Fact>]
let ``Lambdas, closures and function values go through the core IR`` () =
    let source =
        normalized """def apply = { f, x -> f(x) }

def double: int -> int = { n -> n * 2 }

def compose = { f, g -> { x -> g(f(x)) } }

def countdown: int -> int = { n ->
  rec loop = { i, acc -> if i == 0 then acc else loop(i - 1, acc + i) }
  loop(n, 0)
}

def main = {
  def offset = 10
  def both = compose(double, { x -> x + offset })
  apply({ x -> x + offset }, 1) + apply(double, 4) + both(1) + countdown(4)
}
"""
    let wat, report = compileWithReport source
    // A lambda written in a generic function is specialised with it; one written in `main` keeps its own name.
    for name in [ "compose__int__int__int"; "__lambda_0__int__int__int"; "__rec_loop_1"; "__lambda_2"; "__fn_double"; "main" ] do
        Assert.Contains(name, report.Functions)
    // Calls through function values, and closure blocks holding a table slot and what they capture.
    Assert.Contains("call_indirect (type $ctx_fn_2_i32_i64_to_i64)", wat)
    Assert.Contains("call $__nanyx_alloc", wat)

[<Fact>]
let ``Functions that require contexts, and their callers, go through the core IR`` () =
    let source =
        normalized """context Ask = (ask: () -> int)

context Show(a) = (show: a -> string)

type Point = (x: int, y: int)

def Point.show: Point -> string = { p -> "point" }

def twice: [Ask] () -> int = { ask() + ask() }

def plain: [Show(a)] a -> string = { value -> show(value) }

def main = {
  use Ask(ask = { 21 })
  def p = Point(x = 1, y = 2)
  def s = plain(p)
  twice()
}
"""
    let wat, report = compileWithReport source
    // `plain` is specialised for `Point`, and on the instance built from `Point.show`, which it calls directly.
    for name in [ "twice"; "plain__rec_x_int_y_int__known"; "main" ] do
        Assert.Contains(name, report.Functions)
    Assert.DoesNotContain("__fn_Point_show", report.Functions)
    Assert.Contains("""(func $plain__rec_x_int_y_int__known (param $value i32) (result i32)""", wat)
    Assert.Contains("""return_call $Point_show""", wat)
    // A member that is not known, such as a `use` of a lambda, arrives in a parameter of its own, after the function's
    // own parameters.
    Assert.Contains("""(func $twice (param $__ctx_ask i32) (result i64)""", wat)

[<Fact>]
let ``Floats, pipes, guards and for loops go through the core IR`` () =
    let source =
        normalized """def area: float -> float = { r -> r * r * 3.14 }

def size = { n ->
  match n
    | > 100 -> 2
    | < 0 -> 0
    | _ -> 1
}

def double: int -> int = { n -> n * 2 }

def total: list(int) -> int = { xs ->
  for xs | n -> size(n \double)
  for 1..3 | i -> dbg(area(1.5))
  0
}
"""
    let wat, report = compileWithReport source
    Assert.Equal<string list>([ "area"; "size"; "double"; "total" ], withoutPrinters report.Functions)
    Assert.Contains("loop $__for_next_0", wat)
    Assert.Contains("f64.mul\n    call $__nanyx_float_box", wat)

[<Fact>]
let ``The start function and errors deferred to run time go through the core IR`` () =
    // A module with a type error is still compiled: the expression that failed writes the error and stops the program
    // when it is reached.
    let source =
        normalized """def broken: int -> string = { n -> n ++ "x" }

def fine: int -> int = { n -> n + 1 }

dbg(fine(1))
dbg(broken(2))
"""
    let result = Compiler.compile source
    let wat, report = transpileTypedModuleWithReport result.Typed.Value
    // The script's statements are its entry point, run by the console host's `run`, which the module has too.
    let functions = withoutPrinters report.Functions
    Assert.Equal<string list>([ "broken"; "fine" ], functions |> List.filter (fun name -> name = "broken" || name = "fine"))
    Assert.Equal("__nanyx_start", List.last functions)
    Assert.Contains("call $proc_exit\n    unreachable", wat)
    Assert.Contains("(start $__nanyx_start)", wat)

[<Fact>]
let ``A generic function imported from another module is specialised where it is called`` () =
    // `List.map` comes from the prelude, inlined into the module with the types its own module gave its nodes.
    let source =
        normalized """def doubled: list(int) -> list(int) = { xs -> list.map(xs, { x -> x * 2 }) }

def positives: list(int) -> list(bool) = { xs -> list.map(xs, { x -> x > 0 }) }
"""
    let _, report = compileWithReport source
    let mapSpecialisations = report.Specialisations |> List.filter (snd >> (=) "list_map") |> List.map fst
    // Named by its annotation's variables in order, `a` then `b`.
    Assert.Equal<string list>([ "list_map__int__int"; "list_map__int__bool" ], mapSpecialisations)
    Assert.Contains("doubled", report.Functions)
    Assert.Contains("positives", report.Functions)

[<Fact>]
let ``A generic function over an open union, and a list's length, go through the core IR`` () =
    // `mapSome` takes any union with `#some`: its rest is the tags of the union it is called with. `xs.length` is a
    // call to `List.length` the type checker recorded as a property.
    let source =
        normalized """def bump: #some(int) | #none -> #some(int) | #none = { o -> mapSome(o, { v -> v + 1 }) }

def count: list(int) -> int = { xs -> xs.length }
"""
    let _, report = compileWithReport source
    Assert.Contains("bump", report.Functions)
    Assert.Contains("count", report.Functions)
    Assert.Contains("mapSome", report.Specialisations |> List.map snd)
    Assert.Contains("list_length", report.Specialisations |> List.map snd)

[<Fact>]
let ``A type variable that nothing constrains is unit in the core IR`` () =
    // The element of `[]` is fixed by nothing: no value of it can be made or inspected.
    let source = "def none: () -> int = { list.length([]) }\n"
    let _, report = compileWithReport source
    Assert.Contains("none", report.Functions)
    Assert.Contains("list_length__unit", report.Functions)

[<Fact>]
let ``A guard's predicate is inlined with the value matched in a local, in the core IR`` () =
    let source =
        normalized """def limit = 10

def size: int -> int = { n ->
  match n
    | { >= limit * 2 } -> 2
    | { >= limit } -> 1
    | else -> 0
}
"""
    let wat, report = compileWithReport source
    Assert.Contains("size", report.Functions)
    Assert.Contains("local.set $__guard_i64\n      local.get $__guard_i64\n    call $limit\n    i64.const 2\n    call $__nanyx_int_mul\n    i64.ge_s", wat)

[<Fact>]
let ``A function that reads a field of any record is specialised for each layout it is given`` () =
    let source =
        normalized """def nOf = { r -> r.n }

def main = {
  def small = (n = 1)
  nOf(small) + nOf((a = 2, n = 3)) + nOf((a = 4, b = 5, n = 6)) + nOf((n = 7))
}
"""
    let wat, report = compileWithReport source
    // One specialisation per layout, named by the record's other fields.
    Assert.Equal<string list>([ "nOf__int__rec_"; "nOf__int__rec_a_int"; "nOf__int__rec_a_int_b_int"; "main" ], report.Functions)
    // Each reads `n` from where its layout puts it, rather than looking the field up at run time.
    for name, load in [ "nOf__int__rec_", "i64.load"; "nOf__int__rec_a_int", "i64.load offset=8"; "nOf__int__rec_a_int_b_int", "i64.load offset=16" ] do
        Assert.Contains($"(func ${name} (param $r i32) (result i64)\n    local.get $r\n    {load}\n  )", wat)
    let specialisations = wat.Substring(wat.IndexOf "(func $nOf__")
    Assert.DoesNotContain("call $__nanyx_open_field", specialisations)

/// What the core IR lowers from `typed`, with no contexts, records or lifted functions.
let private lowerInput (typed: TypedModule) : CoreIrLowerInput.LowerInput =
    { Definitions =
        typed.Module |> List.choose (function Kind (Def (ValueDef(_, name, _, expr))) -> Some (name, expr) | _ -> None)
      ExprTypes = typed.ExprTypes
      CalleeTypes = typed.CalleeTypes
      DefinitionTypes = typed.Types
      ContextParameters = Map.empty
      ContextArguments = fun _ _ _ _ -> Ok []
      ContextLayouts = Map.empty
      AmbientMembers = Map.empty
      UseKeys = Map.empty
      BuiltInHoles = Set.empty
      RecordConstructors = Set.empty
      NominalWrappers = Set.empty
      NominalTypes = Set.empty
      Lifted = Map.empty
      TopLevel = []
      NamedTypes = Map.empty }

[<Fact>]
let ``Every intermediate value in the core IR is named`` () =
    let input = lowerInput (typedModule "def f = { x, y -> (x + 1) * (y - 2) }\n")
    let lowered = CoreIrLowerModule.lowerModule input id
    let func, _ = lowered.Roots.["f"]
    let printed = CoreIr.Print.func func
    let expected =
        normalized """fn f(x: int, y: int): int =
  let __t3: int = x + 1
  let __t4: int = y - 2
  __t3 * __t4"""
    Assert.Equal(expected, printed)

[<Fact>]
let ``A function of 2,000 statements compiles on a 1 MB stack`` () =
    let statements =
        [ for i in 1 .. 1000 do
              $"  def v{i} = v{i - 1} + 1"
              $"  dbg(\"{{v{i}}}\")" ]
    let source = String.concat "\n" ([ "def main = {"; "  def v0 = 0" ] @ statements @ [ "  v1000"; "}"; "" ])
    let typed = typedModule source
    let _, report = Nanyx.LargeStack.runWithStack (1024 * 1024) (fun () -> transpileTypedModuleWithReport typed)
    Assert.Contains("main", report.Functions)

[<Fact>]
let ``A script of 2,000 top-level statements compiles on a 1 MB stack`` () =
    let source = String.concat "\n" [ for i in 1 .. 2000 do $"dbg(\"line{i}\")"; "" ]
    let typed = typedModule source
    let wat = Nanyx.LargeStack.runWithStack (1024 * 1024) (fun () -> transpileTypedModuleToWat typed)
    Assert.Contains("line2000", wat)

[<Fact>]
let ``A broken invariant in the emitter is an internal error, said once, that asks for a bug report`` () =
    let input = lowerInput (typedModule "def f = { x -> x + 1 }\n")
    // An emitter that finds the core IR broken, as `CoreIrEmit` does when a temporary is never used.
    let lowered = CoreIrLowerModule.lowerModule input (fun _ -> CoreIr.internalError "a temporary in the core IR is never used")
    match lowered.Failures with
    | [ "f", failure, span ] ->
        let diagnostic = lowerFailureDiagnostic failure span
        Assert.Equal(DiagnosticCodes.BackendInternalError.Code, diagnostic.Code)
        Assert.Equal(
            "Internal error: a temporary in the core IR is never used. This is a bug in the compiler: please report it at https://github.com/nanyx-lang/nanyx/issues",
            diagnostic.Message)
    | failures -> Assert.Fail $"Expected one failure in f, got %A{failures}"
